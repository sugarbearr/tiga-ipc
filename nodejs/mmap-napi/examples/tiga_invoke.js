const mmap = require('..');

if (typeof mmap.tigaInvoke !== 'function') {
  console.error(
    'tigaInvoke not found. Run `npm run build` in mmap-napi to rebuild index.node.',
  );
  process.exit(1);
}

const clientId = process.env.TIGA_IPC_CLIENT_ID || `node-${process.pid}`;
const base = 'Excel';
const req = `${base}.req.${clientId}`;
const resp = `${base}.resp.${clientId}`;

console.log(`clientId=${clientId}`);
console.log(`request=${req}`);
console.log(`response=${resp}`);

const reply = mmap.tigaInvoke(
  req,
  resp,
  'method',
  `hello from ${clientId}`,
  5000,
);
console.log('invoke reply:', reply);
