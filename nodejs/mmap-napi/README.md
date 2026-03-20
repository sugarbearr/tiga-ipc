# @tiga-ipc/mmap

`@tiga-ipc/mmap` is the Node.js package for the Tiga memory-mapped IPC protocol.

It provides a CommonJS wrapper around the native `index.node` addon and exposes both low-level channel primitives and a higher-level server helper:

- `initialized()`
- `tigaWrite(name, message, options?)`
- `tigaRead(name, options?)`
- `tigaInvoke(requestName, responseName, method, data, options?)`
- `startTigaServer(options)`
- `createTigaServer(options)`

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
const {
  startTigaServer,
  tigaInvoke,
  tigaWrite,
  tigaRead,
} = require('@tiga-ipc/mmap');

async function main() {
  const mappingDirectory = 'C:\\temp\\tiga-ipc';
  const server = startTigaServer({
    baseName: 'sample',
    mappingDirectory,
    onInvoke(method, data) {
      if (method === 'echo') {
        return `reply:${data}`;
      }

      throw new Error(`method not supported: ${method}`);
    },
  });

  try {
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
  } finally {
    await server.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
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

### `startTigaServer(options)` / `createTigaServer(options)`

- `options.baseName: string`
- `options.mappingDirectory: string`
- `options.discoveryIntervalMs?: number`
- `options.waitTimeoutMs?: number`
- `options.onInvoke(method, data, context): unknown | Promise<unknown>`
- `options.onError?(error, context): void`

Returns a `TigaServer` instance. `startTigaServer(...)` starts it immediately. `createTigaServer(...)` returns the instance so the caller can decide when to call `server.start()`.

`context` includes:

```ts
interface TigaServerContext {
  baseName: string;
  clientId: string;
  requestName: string;
  responseName: string;
  mappingDirectory: string;
  requestId: string;
  entryId: number;
  mediaType?: string | null;
}
```

The server helper keeps transport concerns inside the package:

- discovers per-client request channels under the configured `mappingDirectory`
- registers request listeners using the native notification mechanism
- decodes invoke payloads and writes response payloads back to the matching response channel
- surfaces business logic as a single `onInvoke(...)` callback

### `createTigaNotificationListener(name, options?)`

Advanced low-level API for consumers that need direct access to the native notification wait handle. Most applications should use `startTigaServer(...)` instead.

## Notes

- This package intentionally uses the current `tiga*` API only. The old generic `write/read` export surface is not part of the published package entry.
- The native binary is packaged as `index.node`, so publish from a Windows environment after rebuilding the addon you want to ship.
- For repository examples and cross-language smoke tests, see the monorepo root README and `examples/`.
- `tigaInvoke(...)` is synchronous. In practice the server and client should live in different processes, which matches the real Tiga IPC usage model.
- For per-client channels, the first invoke may wait up to the discovery interval until the server notices the newly created request channel and registers a listener.

## License

MIT
