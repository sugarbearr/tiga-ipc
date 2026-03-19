const mmap = require('..');

if (typeof mmap.tigaRead !== 'function') {
  console.error(
    'tigaRead not found. Run `npm run build` in mmap-napi to rebuild index.node.',
  );
  process.exit(1);
}

const clientId = process.env.TIGA_IPC_CLIENT_ID || `node-${process.pid}`;
const base = 'Excel';
const resp = `${base}.resp.${clientId}`;

let lastId = 0;
console.log(`listening on ${resp}`);

setInterval(() => {
  const result = mmap.tigaRead(resp, lastId);
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
