const zlib = require('zlib');

const { decode, encode } = require('@msgpack/msgpack');

const TIGA_MEDIA_TYPE_MSGPACK = 'application/x-msgpack';
const TIGA_MEDIA_TYPE_MSGPACK_COMPRESSED =
  'application/x-msgpack-compressed';

const parseTigaInvokeEntry = (entry) => {
  try {
    const raw =
      entry && entry.mediaType === TIGA_MEDIA_TYPE_MSGPACK_COMPRESSED
        ? zlib.gunzipSync(entry.message)
        : entry && entry.message;
    const decoded = decode(raw);
    if (!Array.isArray(decoded) || decoded.length < 4) {
      return null;
    }

    const [id, protocol, method, data] = decoded;
    if (protocol !== 1 || typeof id !== 'string' || typeof method !== 'string') {
      return null;
    }

    return { id, method, data };
  } catch {
    return null;
  }
};

const buildTigaResponseBuffer = (id, data, code) =>
  Buffer.from(encode([id, 2, data, code]));

const stringifyTigaResponseData = (value) => {
  if (typeof value === 'string') {
    return value;
  }

  if (Buffer.isBuffer(value)) {
    return value.toString('utf8');
  }

  if (value === undefined) {
    return '';
  }

  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
};

const getTigaErrorMessage = (error, fallback = 'invoke failed') => {
  if (error instanceof Error && error.message) {
    return error.message;
  }

  if (typeof error === 'string' && error.trim()) {
    return error;
  }

  return fallback;
};

module.exports = {
  TIGA_MEDIA_TYPE_MSGPACK,
  TIGA_MEDIA_TYPE_MSGPACK_COMPRESSED,
  buildTigaResponseBuffer,
  getTigaErrorMessage,
  parseTigaInvokeEntry,
  stringifyTigaResponseData,
};
