const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const nativePath = require.resolve('./native');
const serverPath = require.resolve('./server');

const ARTIFACT_SUFFIXES = ['_state', '_notify', '_data_0', '_data_1'];

const withServerModule = (binding) => {
  delete require.cache[serverPath];
  require.cache[nativePath] = {
    id: nativePath,
    filename: nativePath,
    loaded: true,
    exports: binding,
  };

  const serverModule = require('./server');
  delete require.cache[serverPath];
  delete require.cache[nativePath];
  return serverModule;
};

const createTempDirectory = () =>
  fs.mkdtempSync(path.join(os.tmpdir(), 'tiga-runtime-server-'));

const artifactPaths = (ipcDirectory, name) =>
  ARTIFACT_SUFFIXES.map((suffix) =>
    path.join(ipcDirectory, `tiga_${name}${suffix}`),
  );

const createArtifacts = (ipcDirectory, name, mtimeMs) => {
  for (const artifactPath of artifactPaths(ipcDirectory, name)) {
    fs.writeFileSync(artifactPath, Buffer.alloc(32));
    fs.utimesSync(artifactPath, new Date(mtimeMs), new Date(mtimeMs));
  }
};

const allArtifactsExist = (ipcDirectory, name) =>
  artifactPaths(ipcDirectory, name).every((artifactPath) =>
    fs.existsSync(artifactPath),
  );

const anyArtifactsExist = (ipcDirectory, name) =>
  artifactPaths(ipcDirectory, name).some((artifactPath) =>
    fs.existsSync(artifactPath),
  );

const createServer = (binding, options = {}) => {
  const { TigaServer } = withServerModule(binding);
  const server = new TigaServer({
    channelName: options.channelName || 'sample',
    ipcDirectory: options.ipcDirectory,
    onInvoke() {
      return 'ok';
    },
  });

  server.startWorker = () => {};
  return server;
};

test(
  'initial scavenge removes fresh untracked per-client artifacts',
  async () => {
    const ipcDirectory = createTempDirectory();
    const channelName = `sample-${Date.now()}`;
    const clientId = 'client-a';
    const requestName = `${channelName}.req.${clientId}`;
    const responseName = `${channelName}.resp.${clientId}`;

    try {
      createArtifacts(ipcDirectory, requestName, Date.now());
      createArtifacts(ipcDirectory, responseName, Date.now());

      const binding = {
        tigaHasLiveListener() {
          return false;
        },
      };
      const server = createServer(binding, { channelName, ipcDirectory });

      await server.ensureInitialScavenge();

      assert.equal(anyArtifactsExist(ipcDirectory, requestName), false);
      assert.equal(anyArtifactsExist(ipcDirectory, responseName), false);
      assert.equal(server.clients.size, 0);
    } finally {
      fs.rmSync(ipcDirectory, { recursive: true, force: true });
    }
  },
);

test('discover preserves active per-client artifacts and registers the client', async () => {
  const ipcDirectory = createTempDirectory();
  const channelName = `sample-${Date.now()}`;
  const clientId = 'client-b';
  const requestName = `${channelName}.req.${clientId}`;
  const responseName = `${channelName}.resp.${clientId}`;
  const oldMtimeMs = Date.now() - 10 * 60 * 1000;

  try {
    createArtifacts(ipcDirectory, requestName, oldMtimeMs);
    createArtifacts(ipcDirectory, responseName, oldMtimeMs);

    const binding = {
      tigaHasLiveListener(name) {
        return name === responseName;
      },
    };
    const server = createServer(binding, { channelName, ipcDirectory });

    await server.discover();

    assert.equal(allArtifactsExist(ipcDirectory, requestName), true);
    assert.equal(allArtifactsExist(ipcDirectory, responseName), true);
    assert.equal(server.clients.has(clientId), true);
  } finally {
    fs.rmSync(ipcDirectory, { recursive: true, force: true });
  }
});

test('scavenge removes stale tracked clients after response listener disappears', async () => {
  const ipcDirectory = createTempDirectory();
  const channelName = `sample-${Date.now()}`;
  const clientId = 'client-c';
  const requestName = `${channelName}.req.${clientId}`;
  const responseName = `${channelName}.resp.${clientId}`;
  const oldMtimeMs = Date.now() - 10 * 60 * 1000;

  try {
    createArtifacts(ipcDirectory, requestName, oldMtimeMs);
    createArtifacts(ipcDirectory, responseName, oldMtimeMs);

    let closedCount = 0;
    const binding = {
      tigaHasLiveListener() {
        return false;
      },
    };
    const server = createServer(binding, { channelName, ipcDirectory });
    server.closeClient = async (client) => {
      closedCount += 1;
      client.closing = true;
    };

    server.registerClient({
      key: clientId,
      clientId,
      requestName,
      responseName,
    });

    await server.scavengeOnce();

    assert.equal(closedCount, 1);
    assert.equal(server.clients.has(clientId), false);
    assert.equal(anyArtifactsExist(ipcDirectory, requestName), false);
    assert.equal(anyArtifactsExist(ipcDirectory, responseName), false);
  } finally {
    fs.rmSync(ipcDirectory, { recursive: true, force: true });
  }
});

test('scavenge removes tracked clients whose artifacts are already missing', async () => {
  const ipcDirectory = createTempDirectory();
  const channelName = `sample-${Date.now()}`;
  const clientId = 'client-d';
  const requestName = `${channelName}.req.${clientId}`;
  const responseName = `${channelName}.resp.${clientId}`;

  try {
    const binding = {
      tigaHasLiveListener() {
        return false;
      },
    };
    const server = createServer(binding, { channelName, ipcDirectory });
    server.closeClient = async (client) => {
      client.closing = true;
    };

    server.registerClient({
      key: clientId,
      clientId,
      requestName,
      responseName,
    });

    await server.scavengeOnce();

    assert.equal(server.clients.has(clientId), false);
  } finally {
    fs.rmSync(ipcDirectory, { recursive: true, force: true });
  }
});

test('scavenge claim blocks re-registering a tracked client mid-cleanup', async () => {
  const ipcDirectory = createTempDirectory();
  const channelName = `sample-${Date.now()}`;
  const clientId = 'client-e';
  const requestName = `${channelName}.req.${clientId}`;
  const responseName = `${channelName}.resp.${clientId}`;
  const oldMtimeMs = Date.now() - 10 * 60 * 1000;

  try {
    createArtifacts(ipcDirectory, requestName, oldMtimeMs);
    createArtifacts(ipcDirectory, responseName, oldMtimeMs);

    const binding = {
      tigaHasLiveListener() {
        return false;
      },
    };
    const server = createServer(binding, { channelName, ipcDirectory });
    let reRegisterBlocked = 0;
    server.closeClient = async (client) => {
      client.closing = true;
      const added = server.registerClient({
        key: clientId,
        clientId,
        requestName,
        responseName,
      });
      assert.equal(added, false);
      reRegisterBlocked += 1;
    };

    server.registerClient({
      key: clientId,
      clientId,
      requestName,
      responseName,
    });

    await server.scavengeOnce();

    assert.equal(reRegisterBlocked, 1);
    assert.equal(server.clients.has(clientId), false);
    assert.equal(anyArtifactsExist(ipcDirectory, requestName), false);
    assert.equal(anyArtifactsExist(ipcDirectory, responseName), false);
  } finally {
    fs.rmSync(ipcDirectory, { recursive: true, force: true });
  }
});

test('initial scavenge failure is not retried on later ensure calls', async () => {
  const ipcDirectory = createTempDirectory();

  try {
    const binding = {
      tigaHasLiveListener() {
        return false;
      },
    };
    const server = createServer(binding, { ipcDirectory });
    let attempts = 0;
    server.scavengeOnce = async () => {
      attempts += 1;
      throw new Error('boom');
    };

    await assert.rejects(server.ensureInitialScavenge(), /boom/);
    await server.ensureInitialScavenge();
    await server.discover();

    assert.equal(attempts, 1);
  } finally {
    fs.rmSync(ipcDirectory, { recursive: true, force: true });
  }
});
