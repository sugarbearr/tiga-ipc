const mmap = require('..');

const mappingDirectory = process.env.TIGA_IPC_DIR || process.argv[2];
const baseName = process.env.TIGA_IPC_NAME || 'SampleChannel';

if (!mappingDirectory) {
  console.error(
    'mappingDirectory is required. Set TIGA_IPC_DIR or pass it as argv[2].',
  );
  process.exit(1);
}

const server = mmap.startTigaServer({
  baseName,
  mappingDirectory,
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

console.log(`baseName=${baseName}`);
console.log(`mappingDirectory=${mappingDirectory}`);
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
