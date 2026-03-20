const mmap = require('..');

const ipcDirectory = process.env.TIGA_IPC_DIRECTORY || process.argv[2];
const channelName = process.env.TIGA_CHANNEL_NAME || 'SampleChannel';

if (!ipcDirectory) {
  console.error(
    'ipcDirectory is required. Set TIGA_IPC_DIRECTORY or pass it as argv[2].',
  );
  process.exit(1);
}

const server = mmap.startTigaServer({
  channelName,
  ipcDirectory,
  onInvoke(method, data) {
    switch (method) {
      case 'echo':
        return `Echo response: ${data}`;
      default:
        throw new Error(`method not supported: ${method}`);
    }
  },
  onError(error, context) {
    console.error('[tiga-server]', error.message, context);
  },
});

console.log(`channelName=${channelName}`);
console.log(`ipcDirectory=${ipcDirectory}`);
console.log('server ready');

const shutdown = async () => {
  await server.close();
  process.exit(0);
};

process.on('SIGINT', () => {
  shutdown().catch((error) => {
    console.error('shutdown failed', error);
    process.exit(1);
  });
});

process.on('SIGTERM', () => {
  shutdown().catch((error) => {
    console.error('shutdown failed', error);
    process.exit(1);
  });
});
