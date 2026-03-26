const fs = require('fs');
const path = require('path');
const { Worker } = require('worker_threads');

const binding = require('./native');
const {
  TIGA_MEDIA_TYPE_MSGPACK,
  buildTigaResponseBuffer,
  getTigaErrorMessage,
  parseTigaInvokeEntry,
  stringifyTigaResponseData,
} = require('./protocol');

const FILE_PREFIX = 'tiga_';
const STATE_SUFFIX = '_state';
const NOTIFY_SUFFIX = '_notify';
const DATA_SUFFIXES = ['_data_0', '_data_1'];
const SINGLE_CLIENT_ID = '__single__';
const DEFAULT_DISCOVERY_INTERVAL_MS = 1000;
const DEFAULT_WAIT_TIMEOUT_MS = 1000;
const WORKER_CLOSE_TIMEOUT_MS = 2000;
const STALE_CLIENT_ARTIFACT_AGE_MS = 5 * 60 * 1000;

const ensureString = (value, label) => {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new Error(`${label} is required`);
  }

  return value.trim();
};

const getStatePath = (ipcDirectory, name) =>
  path.join(ipcDirectory, `${FILE_PREFIX}${name}${STATE_SUFFIX}`);

const getArtifactPaths = (ipcDirectory, name) => [
  getStatePath(ipcDirectory, name),
  path.join(ipcDirectory, `${FILE_PREFIX}${name}${NOTIFY_SUFFIX}`),
  ...DATA_SUFFIXES.map((suffix) =>
    path.join(ipcDirectory, `${FILE_PREFIX}${name}${suffix}`),
  ),
];

const hasSingleChannel = (channelName, ipcDirectory) =>
  fs.existsSync(getStatePath(ipcDirectory, channelName));

const listClientIds = (channelName, ipcDirectory) => {
  try {
    const prefix = `${FILE_PREFIX}${channelName}.req.`;
    return fs
      .readdirSync(ipcDirectory)
      .filter((fileName) => fileName.startsWith(prefix))
      .filter((fileName) => fileName.endsWith(STATE_SUFFIX))
      .map((fileName) =>
        fileName.slice(prefix.length, fileName.length - STATE_SUFFIX.length),
      )
      .filter((clientId) => clientId.length > 0);
  } catch {
    return [];
  }
};

const getLatestArtifactMtimeMs = (ipcDirectory, name) => {
  let latestMtimeMs = null;
  for (const artifactPath of getArtifactPaths(ipcDirectory, name)) {
    try {
      const stats = fs.statSync(artifactPath);
      latestMtimeMs =
        latestMtimeMs === null
          ? stats.mtimeMs
          : Math.max(latestMtimeMs, stats.mtimeMs);
    } catch (error) {
      if (!error || error.code !== 'ENOENT') {
        throw error;
      }
    }
  }

  return latestMtimeMs;
};

const deleteArtifacts = (ipcDirectory, name) => {
  for (const artifactPath of getArtifactPaths(ipcDirectory, name)) {
    try {
      fs.unlinkSync(artifactPath);
    } catch (error) {
      if (
        !error ||
        (error.code !== 'ENOENT' &&
          error.code !== 'EBUSY' &&
          error.code !== 'EPERM')
      ) {
        throw error;
      }
    }
  }
};

const toClientState = (clientId, channelName) =>
  clientId === SINGLE_CLIENT_ID
    ? {
        key: SINGLE_CLIENT_ID,
        clientId,
        requestName: channelName,
        responseName: channelName,
      }
    : {
        key: clientId,
        clientId,
        requestName: `${channelName}.req.${clientId}`,
        responseName: `${channelName}.resp.${clientId}`,
      };

const SCAVENGE_INTERVAL_MS = 30 * 1000;

class TigaServer {
  constructor(options) {
    const resolved = options || {};
    this.channelName = ensureString(resolved.channelName, 'channelName');
    this.ipcDirectory = ensureString(resolved.ipcDirectory, 'ipcDirectory');
    if (typeof resolved.onInvoke !== 'function') {
      throw new Error('onInvoke must be a function');
    }

    this.onInvoke = resolved.onInvoke;
    this.onError =
      typeof resolved.onError === 'function' ? resolved.onError : null;
    this.discoveryIntervalMs = Math.max(
      100,
      Number(resolved.discoveryIntervalMs || DEFAULT_DISCOVERY_INTERVAL_MS),
    );
    this.waitTimeoutMs = Math.max(
      100,
      Number(resolved.waitTimeoutMs || DEFAULT_WAIT_TIMEOUT_MS),
    );

    this.clients = new Map();
    this.scavengeClaims = new Set();
    this.discoveryTimer = null;
    this.scavengeTimer = null;
    this.scavenging = false;
    this.initialScavengeCompleted = false;
    this.initialScavengeAttempted = false;
    this.initialScavengePromise = null;
    this.closed = false;
    this.started = false;
  }

  start() {
    if (this.started) {
      return this;
    }

    fs.mkdirSync(this.ipcDirectory, { recursive: true });
    this.started = true;
    this.closed = false;
    void this.ensureInitialScavenge().catch((error) => {
      this.reportError(error, {
        stage: 'scavenge-initial',
        channelName: this.channelName,
        ipcDirectory: this.ipcDirectory,
      });
    });
    this.discoveryTimer = setInterval(() => {
      this.discover().catch((error) => {
        this.reportError(error, {
          stage: 'discover',
          channelName: this.channelName,
          ipcDirectory: this.ipcDirectory,
        });
      });
    }, this.discoveryIntervalMs);
    this.discover().catch((error) => {
      this.reportError(error, {
        stage: 'discover',
        channelName: this.channelName,
        ipcDirectory: this.ipcDirectory,
      });
    });

    // Low-frequency scavenger: runs every SCAVENGE_INTERVAL_MS, cleans up
    // orphaned artifacts from crashed clients without touching the discovery path.
    this.scavengeTimer = setInterval(() => {
      this.runBackgroundScavenge().catch((error) => {
        this.reportError(error, {
          stage: 'scavenge',
          channelName: this.channelName,
          ipcDirectory: this.ipcDirectory,
        });
      });
    }, SCAVENGE_INTERVAL_MS);

    return this;
  }

  async close() {
    if (this.closed) {
      return;
    }

    this.closed = true;
    this.started = false;
    if (this.discoveryTimer) {
      clearInterval(this.discoveryTimer);
      this.discoveryTimer = null;
    }

    if (this.scavengeTimer) {
      clearInterval(this.scavengeTimer);
      this.scavengeTimer = null;
    }

    const clients = Array.from(this.clients.values());
    this.clients.clear();
    await Promise.all(clients.map((client) => this.closeClient(client)));
  }

  async discover() {
    if (this.closed) {
      return;
    }

    await this.ensureInitialScavenge();

    const nextClients = listClientIds(this.channelName, this.ipcDirectory);
    for (const clientId of nextClients) {
      // Discovery only registers — cleanup is handled by the low-frequency scavenger.
      if (!this.clients.has(clientId)) {
        this.registerClient(toClientState(clientId, this.channelName));
      }
    }

    if (
      !this.clients.has(SINGLE_CLIENT_ID) &&
      hasSingleChannel(this.channelName, this.ipcDirectory)
    ) {
      this.registerClient(toClientState(SINGLE_CLIENT_ID, this.channelName));
    }
  }

  registerClient(client) {
    if (this.clients.has(client.key) || this.scavengeClaims.has(client.key)) {
      return false;
    }

    const state = {
      ...client,
      lastId: 0,
      draining: false,
      pendingDrain: false,
      closing: false,
      worker: null,
      workerExit: null,
      restartTimer: null,
    };

    this.clients.set(state.key, state);
    this.startWorker(state);
    return true;
  }

  async removeClient(key) {
    const client = this.clients.get(key);
    if (!client) {
      return false;
    }

    this.clients.delete(key);
    await this.closeClient(client);
    return true;
  }

  /**
   * Runs exactly once before discovery starts tracking on-disk clients, so
   * stale crash residue is deleted instead of being re-registered.
   */
  async ensureInitialScavenge() {
    if (
      this.initialScavengeCompleted ||
      (this.initialScavengeAttempted && !this.initialScavengePromise)
    ) {
      return;
    }

    if (!this.initialScavengePromise) {
      this.initialScavengePromise = this.scavengeOnce({
        aggressiveUntrackedCleanup: true,
      })
        .then(() => {
          this.initialScavengeCompleted = true;
        })
        .finally(() => {
          this.initialScavengeAttempted = true;
          this.initialScavengePromise = null;
        });
    }

    await this.initialScavengePromise;
  }

  async runBackgroundScavenge() {
    if (this.closed || this.scavenging) {
      return;
    }

    this.scavenging = true;
    try {
      await this.scavengeOnce();
    } finally {
      this.scavenging = false;
    }
  }

  /**
   * Low-frequency scavenger: scans all req.* state files on disk, removes stale
   * untracked artifacts, and also evicts tracked clients whose response
   * listener has disappeared.
   */
  async scavengeOnce({ aggressiveUntrackedCleanup = false } = {}) {
    if (this.closed) {
      return;
    }

    const trackedClients = Array.from(this.clients.values()).filter(
      (client) => client.key !== SINGLE_CLIENT_ID,
    );
    for (const client of trackedClients) {
      await this.scavengeTrackedClient(client);
    }

    const clientIds = listClientIds(this.channelName, this.ipcDirectory);
    for (const clientId of clientIds) {
      const requestName = `${this.channelName}.req.${clientId}`;
      const responseName = `${this.channelName}.resp.${clientId}`;

      if (this.clients.has(clientId)) {
        continue;
      }

      // Startup scavenging runs before discovery tracks on-disk clients, so
      // dead residue can be removed immediately. The periodic scavenger stays
      // conservative and still waits for the stale-age window.
      if (
        !aggressiveUntrackedCleanup &&
        !this.isArtifactGroupStale(requestName, responseName)
      ) {
        continue;
      }

      if (!this.tryBeginScavengeClaim(clientId, false)) {
        continue;
      }

      try {
        // Slightly more expensive: raw notify-file slot scan (one FileStream
        // read, no MemoryMappedFile, no kernel section object).
        if (
          this.hasLiveListener(requestName) ||
          this.hasLiveListener(responseName)
        ) {
          continue;
        }

        this.deleteArtifactGroup(requestName, responseName);
      } finally {
        this.endScavengeClaim(clientId);
      }
    }
  }

  async scavengeTrackedClient(client) {
    if (!client || client.key === SINGLE_CLIENT_ID) {
      return;
    }

    if (
      !this.shouldScavengeTrackedClient(client.requestName, client.responseName)
    ) {
      return;
    }

    // The server owns the request listener for tracked clients, so the
    // response listener is the remote liveness signal.
    if (this.hasLiveListener(client.responseName)) {
      return;
    }

    if (!this.tryBeginScavengeClaim(client.key, true)) {
      return;
    }

    try {
      await this.removeClient(client.key);
      this.deleteArtifactGroup(client.requestName, client.responseName);
    } finally {
      this.endScavengeClaim(client.key);
    }
  }

  hasLiveListener(name) {
    return binding.tigaHasLiveListener(name, {
      ipcDirectory: this.ipcDirectory,
    });
  }

  shouldScavengeTrackedClient(requestName, responseName) {
    if (!this.anyArtifactGroupExists(requestName, responseName)) {
      return true;
    }

    return this.isArtifactGroupStale(requestName, responseName);
  }

  anyArtifactGroupExists(requestName, responseName) {
    for (const name of [requestName, responseName]) {
      if (
        getArtifactPaths(this.ipcDirectory, name).some((artifactPath) =>
          fs.existsSync(artifactPath),
        )
      ) {
        return true;
      }
    }

    return false;
  }

  tryBeginScavengeClaim(clientId, requireTrackedClient) {
    if (this.scavengeClaims.has(clientId)) {
      return false;
    }

    const isTracked = this.clients.has(clientId);
    if (requireTrackedClient ? !isTracked : isTracked) {
      return false;
    }

    this.scavengeClaims.add(clientId);
    return true;
  }

  endScavengeClaim(clientId) {
    this.scavengeClaims.delete(clientId);
  }

  isArtifactGroupStale(requestName, responseName) {
    const latestRequestMtimeMs = getLatestArtifactMtimeMs(
      this.ipcDirectory,
      requestName,
    );
    const latestResponseMtimeMs = getLatestArtifactMtimeMs(
      this.ipcDirectory,
      responseName,
    );
    const latestMtimeMs = [latestRequestMtimeMs, latestResponseMtimeMs]
      .filter((value) => Number.isFinite(value))
      .reduce((max, value) => Math.max(max, value), -Infinity);

    if (!Number.isFinite(latestMtimeMs)) {
      return false;
    }

    return Date.now() - latestMtimeMs >= STALE_CLIENT_ARTIFACT_AGE_MS;
  }

  deleteArtifactGroup(requestName, responseName) {
    deleteArtifacts(this.ipcDirectory, requestName);
    deleteArtifacts(this.ipcDirectory, responseName);
  }

  startWorker(client) {
    if (this.closed || client.closing) {
      return;
    }

    const worker = new Worker(
      path.join(__dirname, 'workers', 'notification-listener.js'),
      {
        workerData: {
          name: client.requestName,
          ipcDirectory: this.ipcDirectory,
          waitTimeoutMs: this.waitTimeoutMs,
        },
      },
    );

    client.worker = worker;
    client.workerExit = new Promise((resolve) => {
      worker.once('exit', () => resolve());
    });

    worker.on('message', (message) => {
      if (!message || client.closing || this.closed) {
        return;
      }

      if (message.type === 'ready' || message.type === 'signal') {
        this.scheduleDrain(client);
        return;
      }

      if (message.type === 'error') {
        this.reportError(new Error(message.message), {
          stage: 'listener',
          clientId: client.clientId,
          requestName: client.requestName,
          responseName: client.responseName,
          channelName: this.channelName,
          ipcDirectory: this.ipcDirectory,
        });
      }
    });

    worker.on('error', (error) => {
      this.reportError(error, {
        stage: 'listener',
        clientId: client.clientId,
        requestName: client.requestName,
        responseName: client.responseName,
        channelName: this.channelName,
        ipcDirectory: this.ipcDirectory,
      });
    });

    worker.on('exit', () => {
      client.worker = null;
      client.workerExit = null;
      if (!this.closed && !client.closing) {
        this.scheduleWorkerRestart(client);
      }
    });
  }

  scheduleWorkerRestart(client) {
    if (client.restartTimer || this.closed || client.closing) {
      return;
    }

    client.restartTimer = setTimeout(() => {
      client.restartTimer = null;
      if (!this.closed && !client.closing && this.clients.has(client.key)) {
        this.startWorker(client);
      }
    }, 200);
  }

  scheduleDrain(client) {
    if (this.closed || client.closing) {
      return;
    }

    client.pendingDrain = true;
    if (client.draining) {
      return;
    }

    client.draining = true;
    void this.runDrainLoop(client);
  }

  async runDrainLoop(client) {
    try {
      while (client.pendingDrain && !client.closing && !this.closed) {
        client.pendingDrain = false;
        await this.drainClient(client);
      }
    } finally {
      client.draining = false;
      if (client.pendingDrain && !client.closing && !this.closed) {
        this.scheduleDrain(client);
      }
    }
  }

  async drainClient(client) {
    let result;
    try {
      result = binding.tigaRead(client.requestName, {
        lastId: client.lastId,
        ipcDirectory: this.ipcDirectory,
      });
    } catch (error) {
      this.reportError(error, {
        stage: 'read',
        clientId: client.clientId,
        requestName: client.requestName,
        responseName: client.responseName,
        channelName: this.channelName,
        ipcDirectory: this.ipcDirectory,
      });
      return;
    }

    if (
      result &&
      typeof result.lastId === 'number' &&
      result.lastId < client.lastId
    ) {
      client.lastId = result.lastId;
    }

    const entries = Array.isArray(result && result.entries)
      ? result.entries.filter((entry) => entry && entry.id > client.lastId)
      : [];

    for (const entry of entries) {
      await this.processEntry(client, entry);
      client.lastId = Math.max(client.lastId, entry.id);
    }
  }

  async processEntry(client, entry) {
    const invoke = parseTigaInvokeEntry(entry);
    if (!invoke) {
      return;
    }

    const context = {
      channelName: this.channelName,
      clientId: client.clientId,
      requestName: client.requestName,
      responseName: client.responseName,
      ipcDirectory: this.ipcDirectory,
      requestId: invoke.id,
      entryId: entry.id,
      mediaType: entry.mediaType || null,
    };

    try {
      const result = await Promise.resolve(
        this.onInvoke(invoke.method, invoke.data, context),
      );
      this.writeResponse(
        client,
        buildTigaResponseBuffer(
          invoke.id,
          stringifyTigaResponseData(result),
          0,
        ),
      );
    } catch (error) {
      const message = getTigaErrorMessage(error);
      this.reportError(error, {
        ...context,
        stage: 'invoke',
        method: invoke.method,
      });
      this.writeResponse(
        client,
        buildTigaResponseBuffer(invoke.id, message, -1),
      );
    }
  }

  writeResponse(client, payload) {
    try {
      binding.tigaWrite(client.responseName, payload, {
        mediaType: TIGA_MEDIA_TYPE_MSGPACK,
        ipcDirectory: this.ipcDirectory,
      });
    } catch (error) {
      this.reportError(error, {
        stage: 'write',
        clientId: client.clientId,
        requestName: client.requestName,
        responseName: client.responseName,
        channelName: this.channelName,
        ipcDirectory: this.ipcDirectory,
      });
    }
  }

  async closeClient(client) {
    client.closing = true;
    client.pendingDrain = false;
    if (client.restartTimer) {
      clearTimeout(client.restartTimer);
      client.restartTimer = null;
    }

    const worker = client.worker;
    const workerExit = client.workerExit;
    client.worker = null;
    client.workerExit = null;
    if (!worker) {
      return;
    }

    try {
      worker.postMessage({ type: 'close' });
    } catch {
      // Ignore worker shutdown races.
    }

    if (workerExit) {
      const terminated = workerExit.then(() => true);
      const timeout = new Promise((resolve) => {
        setTimeout(() => resolve(false), WORKER_CLOSE_TIMEOUT_MS);
      });

      const exited = await Promise.race([terminated, timeout]);
      if (exited) {
        return;
      }
    }

    await worker.terminate();
  }

  reportError(error, context) {
    if (!this.onError) {
      return;
    }

    try {
      const normalizedError =
        error instanceof Error ? error : new Error(String(error));
      this.onError(normalizedError, context);
    } catch {
      // Ignore user callback errors to keep the server alive.
    }
  }
}

const createTigaServer = (options) => new TigaServer(options);

const startTigaServer = (options) => createTigaServer(options).start();

module.exports = {
  TigaServer,
  createTigaServer,
  startTigaServer,
};
