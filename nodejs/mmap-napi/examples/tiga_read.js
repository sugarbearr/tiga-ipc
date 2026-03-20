const mmap = require('..');

if (typeof mmap.tigaRead !== 'function') {
  console.error(
    'tigaRead not found. Run `npm run build` in mmap-napi to rebuild index.node.',
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

const channelName = process.env.TIGA_CHANNEL_NAME || 'SampleChannel';
const resp = `${channelName}.resp.${clientId}`;

let lastId = 0;
console.log(`channelName=${channelName}`);
console.log(`ipcDirectory=${ipcDirectory}`);
console.log(`listening on ${resp}`);

setInterval(() => {
  const result = mmap.tigaRead(resp, { lastId, ipcDirectory });
  lastId = result.lastId;
  for (const entry of result.entries) {
    console.log(
      'entry',
      entry.id,
      entry.mediaType,
      entry.message.toString('utf8'),
    );
  }
}, 200);
