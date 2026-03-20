# Tiga IPC interoperability examples

These examples interoperate with `TigaIpc.Server` and `TigaIpc.Client` using the per-client channels.

## 1) Start the C# server

From the monorepo root:

```powershell
# build once
dotnet build .\csharp\TigaIpc.Server\TigaIpc.Server.csproj -c Release

# run server
$env:TIGA_IPC_DIRECTORY = 'C:\Users\Administrator\AppData\Local\Temp\tiga-ipc'
$env:TIGA_CHANNEL_NAME = 'SampleChannel'
dotnet run --project .\csharp\TigaIpc.Server\TigaIpc.Server.csproj -c Release
```

## 2) Run the Node server helper (optional)

From `.\nodejs\mmap-napi`:

```powershell
# build native addon (creates index.node)
npm run build

# run the Node server helper
$env:TIGA_IPC_DIRECTORY = 'C:\Users\Administrator\AppData\Local\Temp\tiga-ipc'
$env:TIGA_CHANNEL_NAME = 'SampleChannel'
node .\examples\tiga_server.js
```

## 3) Run the Node client (invoke)

From `.\nodejs\mmap-napi`:

```powershell
# build native addon (creates index.node)
npm run build

# run example
$env:TIGA_IPC_DIRECTORY = 'C:\Users\Administrator\AppData\Local\Temp\tiga-ipc'
$env:TIGA_CHANNEL_NAME = 'SampleChannel'
node .\examples\tiga_invoke.js
```

## 4) Run the Node response listener (optional)

```powershell
$env:TIGA_IPC_DIRECTORY = 'C:\Users\Administrator\AppData\Local\Temp\tiga-ipc'
$env:TIGA_CHANNEL_NAME = 'SampleChannel'
node .\examples\tiga_read.js
```

Notes:
- The default channel name is `SampleChannel` (matches the C# samples).
- The invoke example calls the `echo` handler on the sample server.
- `TIGA_IPC_CLIENT_ID` can be set to a fixed client id for testing.
- The library no longer uses a hidden default local cache directory.
- The examples read `TIGA_IPC_DIRECTORY` and pass it into the addon as `ipcDirectory`.
- `tigaInvoke(...)` is synchronous, so do not run the server helper and invoke caller on the same Node event loop when you want to test request/response behavior.
