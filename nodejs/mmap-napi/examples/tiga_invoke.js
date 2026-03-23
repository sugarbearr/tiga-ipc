const mmap = require('..');

if (typeof mmap.tigaInvoke !== 'function') {
  console.error(
    'tigaInvoke not found. Run `npm run build` in mmap-napi to rebuild index.node.',
  );
  process.exit(1);
}

const clientId = process.env.TIGA_IPC_CLIENT_ID || `node-${process.pid}`;
const ipcDirectory = process.argv[2] || process.env.TIGA_IPC_DIRECTORY;

if (!ipcDirectory) {
  console.error(
    'ipcDirectory is required. Pass it as argv[2] or set TIGA_IPC_DIRECTORY.',
  );
  process.exit(1);
}

const channelName = process.env.TIGA_CHANNEL_NAME || 'sample';
const req = `${channelName}.req.${clientId}`;
const resp = `${channelName}.resp.${clientId}`;

console.log(`clientId=${clientId}`);
console.log(`channelName=${channelName}`);
console.log(`ipcDirectory=${ipcDirectory}`);
console.log(`request=${req}`);
console.log(`response=${resp}`);

const reply = mmap.tigaInvoke(
  req,
  resp,
  'echo',
  `hello from ${clientId}`,
  { timeoutMs: 5000, ipcDirectory },
);
console.log('invoke reply:', reply);
