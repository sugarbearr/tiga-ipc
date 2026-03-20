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
const SINGLE_CLIENT_ID = '__single__';
const DEFAULT_DISCOVERY_INTERVAL_MS = 1000;
const DEFAULT_WAIT_TIMEOUT_MS = 1000;
const WORKER_CLOSE_TIMEOUT_MS = 2000;

const ensureString = (value, label) => {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new Error(`${label} is required`);
  }

  return value.trim();
};

const getStatePath = (mappingDirectory, name) =>
  path.join(mappingDirectory, `${FILE_PREFIX}${name}${STATE_SUFFIX}`);

const hasSingleChannel = (baseName, mappingDirectory) =>
  fs.existsSync(getStatePath(mappingDirectory, baseName));

const listClientIds = (baseName, mappingDirectory) => {
  try {
    const prefix = `${FILE_PREFIX}${baseName}.req.`;
    return fs
      .readdirSync(mappingDirectory)
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

const toClientState = (clientId, baseName) =>
  clientId === SINGLE_CLIENT_ID
    ? {
        key: SINGLE_CLIENT_ID,
        clientId,
        requestName: baseName,
        responseName: baseName,
      }
    : {
        key: clientId,
        clientId,
        requestName: `${baseName}.req.${clientId}`,
        responseName: `${baseName}.resp.${clientId}`,
      };

class TigaServer {
  constructor(options) {
    const resolved = options || {};
    this.baseName = ensureString(resolved.baseName, 'baseName');
    this.mappingDirectory = ensureString(
      resolved.mappingDirectory,
      'mappingDirectory',
    );
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
    this.discoveryTimer = null;
    this.closed = false;
    this.started = false;
  }

  start() {
    if (this.started) {
      return this;
    }

    fs.mkdirSync(this.mappingDirectory, { recursive: true });
    this.started = true;
    this.closed = false;
    this.discoveryTimer = setInterval(() => {
      this.discover().catch((error) => {
        this.reportError(error, {
          stage: 'discover',
          baseName: this.baseName,
          mappingDirectory: this.mappingDirectory,
        });
      });
    }, this.discoveryIntervalMs);
    this.discover().catch((error) => {
      this.reportError(error, {
        stage: 'discover',
        baseName: this.baseName,
        mappingDirectory: this.mappingDirectory,
      });
    });

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

    const clients = Array.from(this.clients.values());
    this.clients.clear();
    await Promise.all(clients.map((client) => this.closeClient(client)));
  }

  async discover() {
    if (this.closed) {
      return;
    }

    const nextClients = listClientIds(this.baseName, this.mappingDirectory);
    nextClients.forEach((clientId) => {
      if (!this.clients.has(clientId)) {
        this.registerClient(toClientState(clientId, this.baseName));
      }
    });

    if (
      !this.clients.has(SINGLE_CLIENT_ID) &&
      hasSingleChannel(this.baseName, this.mappingDirectory)
    ) {
      this.registerClient(toClientState(SINGLE_CLIENT_ID, this.baseName));
    }
  }

  registerClient(client) {
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
          mappingDirectory: this.mappingDirectory,
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
          baseName: this.baseName,
          mappingDirectory: this.mappingDirectory,
        });
      }
    });

    worker.on('error', (error) => {
      this.reportError(error, {
        stage: 'listener',
        clientId: client.clientId,
        requestName: client.requestName,
        responseName: client.responseName,
        baseName: this.baseName,
        mappingDirectory: this.mappingDirectory,
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
        mappingDirectory: this.mappingDirectory,
      });
    } catch (error) {
      this.reportError(error, {
        stage: 'read',
        clientId: client.clientId,
        requestName: client.requestName,
        responseName: client.responseName,
        baseName: this.baseName,
        mappingDirectory: this.mappingDirectory,
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
      baseName: this.baseName,
      clientId: client.clientId,
      requestName: client.requestName,
      responseName: client.responseName,
      mappingDirectory: this.mappingDirectory,
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
        mappingDirectory: this.mappingDirectory,
      });
    } catch (error) {
      this.reportError(error, {
        stage: 'write',
        clientId: client.clientId,
        requestName: client.requestName,
        responseName: client.responseName,
        baseName: this.baseName,
        mappingDirectory: this.mappingDirectory,
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
