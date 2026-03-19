# @tiga-ipc/mmap

`@tiga-ipc/mmap` is the Node.js package for the Tiga memory-mapped IPC protocol.

It provides a small CommonJS wrapper around the native `index.node` addon and exposes the current Tiga channel APIs:

- `initialized()`
- `tigaWrite(name, message, options?)`
- `tigaRead(name, options?)`
- `tigaInvoke(requestName, responseName, method, data, options?)`

## Install

```bash
npm install @tiga-ipc/mmap
```

Current package scope:

- Windows only
- File-backed mapping usage
- `mappingDirectory` must be passed explicitly by the caller

## Usage

```js
const { tigaInvoke, tigaWrite, tigaRead } = require('@tiga-ipc/mmap');

const mappingDirectory = 'C:\\temp\\tiga-ipc';

const reply = tigaInvoke(
  'sample.req.client-a',
  'sample.resp.client-a',
  'echo',
  'hello from node',
  {
    mappingDirectory,
    timeoutMs: 3000,
  },
);

console.log(reply);

tigaWrite('sample.events', 'event payload', {
  mappingDirectory,
  mediaType: 'text/plain',
});

const result = tigaRead('sample.events', {
  mappingDirectory,
  lastId: 0,
});

console.log(result.lastId, result.entries.length);
```

## API

### `tigaWrite(name, message, options?)`

- `name: string`
- `message: Buffer | string`
- `options?.mappingDirectory: string`
- `options?.mediaType?: string`

Returns a short write result string from the native addon.

### `tigaRead(name, options?)`

- `name: string`
- `options?.mappingDirectory: string`
- `options?.lastId?: number`

Returns:

```ts
interface TigaReadResult {
  lastId: number;
  entries: Array<{
    id: number;
    message: Buffer;
    mediaType?: string;
  }>;
}
```

### `tigaInvoke(requestName, responseName, method, data, options?)`

- `requestName: string`
- `responseName: string`
- `method: string`
- `data: string`
- `options?.mappingDirectory: string`
- `options?.timeoutMs?: number`

Returns the response payload string.

## Notes

- This package intentionally uses the current `tiga*` API only. The old generic `write/read` export surface is not part of the published package entry.
- The native binary is packaged as `index.node`, so publish from a Windows environment after rebuilding the addon you want to ship.
- For repository examples and cross-language smoke tests, see the monorepo root README and `examples/`.

## License

MIT
