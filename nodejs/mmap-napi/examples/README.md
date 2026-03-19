# Tiga IPC interoperability examples

These examples interoperate with `TigaIpc.Server` and `TigaIpc.Client` using the per-client channels.

## 1) Start the C# server

From `E:\native\dm_native\TigaIpc`:

```powershell
# build once
 dotnet build .\TigaIpc.Server\TigaIpc.Server.csproj -c Release

# run server
 dotnet run --project .\TigaIpc.Server\TigaIpc.Server.csproj -c Release
```

## 2) Run the Node client (invoke)

From `E:\native\dm_cross\InnodealingNativeCross\mmap-napi`:

```powershell
# build native addon (creates index.node)
 npm run build

# run example
 node .\examples\tiga_invoke.js
```

## 3) Run the Node response listener (optional)

```powershell
node .\examples\tiga_read.js
```

Notes:
- The default base channel name is `Excel` (matches C# samples).
- `TIGA_IPC_CLIENT_ID` can be set to a fixed client id for testing.
- To match the C# server directory, set `TIGA_IPC_DIR` to the same directory as `FileMappingDirectory` (default: `%TEMP%\\tiga-ipc`).
