const mmap = require('..');

if (typeof mmap.tigaInvoke !== 'function') {
  console.error(
    'tigaInvoke not found. Run `npm run build` in mmap-napi to rebuild index.node.',
  );
  process.exit(1);
}

const clientId = process.env.TIGA_IPC_CLIENT_ID || `node-${process.pid}`;
const mappingDirectory = process.argv[2] || process.env.TIGA_IPC_DIR;

if (!mappingDirectory) {
  console.error(
    'mappingDirectory is required. Pass it as argv[2] or set TIGA_IPC_DIR.',
  );
  process.exit(1);
}

const base = 'SampleChannel';
const req = `${base}.req.${clientId}`;
const resp = `${base}.resp.${clientId}`;

console.log(`clientId=${clientId}`);
console.log(`mappingDirectory=${mappingDirectory}`);
console.log(`request=${req}`);
console.log(`response=${resp}`);

const reply = mmap.tigaInvoke(
  req,
  resp,
  'echo',
  `hello from ${clientId}`,
  { timeoutMs: 5000, mappingDirectory },
);
console.log('invoke reply:', reply);
