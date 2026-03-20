# TigaIpc

TigaIpc 是一个基于内存映射文件（Memory Mapped File）的高性能、本地进程间通信（IPC）项目。

项目的核心设计参考了 [Cloudflare/mmap-sync](https://github.com/cloudflare/mmap-sync)，在 C# 侧实现了单写者、多读者的 wait-free 数据同步模型；同时，这个仓库也包含了与当前 C# 协议对齐的 Node.js N-API 适配层，方便在 Windows 本地环境中做 C# / Node 互通。

当前仓库已经整理为一个总仓库：

- `csharp/`：TigaIpc 核心库、示例程序和测试
- `nodejs/mmap-napi/`：Node.js N-API 封装，提供 `tigaWrite` / `tigaRead` / `tigaInvoke`

如果你要发布 Node 包到 npm，请直接参考：

- [RELEASE.md](./RELEASE.md)

截至 **2026-03-19**，以下链路已经在当前仓库内完成实测：

- `csharp/TigaIpc.Server` 启动文件映射服务
- `nodejs/mmap-napi/examples/tiga_invoke.js` 调用 C# 服务端
- Node 成功收到服务端回包

为避免在文档里暴露业务命名，下面统一使用这些中性占位：

- `<BaseChannel>`：逻辑通道名
- `<clientId>`：客户端标识
- `<FilePrefix>`：底层映射文件前缀

说明：

- 当前仓库里的示例代码默认使用中性的示例通道名 `SampleChannel`
- 当前仓库里的底层固定映射前缀已统一为 `tiga_`
- README 继续使用占位名，是为了说明协议和使用方式，而不是要求你必须使用某个固定业务名称

## 仓库结构

```text
TigaIpc/
├─ csharp/
│  ├─ TigaIpc/                # 核心库
│  ├─ TigaIpc.Server/         # 示例服务端
│  ├─ TigaIpc.Client/         # 示例客户端
│  ├─ TigaIpc.Tests/          # 单元测试 / 集成测试
│  ├─ TigaIpc.TestHost/       # 测试宿主
│  └─ TigaIpc.Benchmarks/     # 基准测试
├─ nodejs/
│  └─ mmap-napi/
│     ├─ src/                 # Rust + N-API 实现
│     ├─ examples/            # Node 互通示例
│     ├─ index.js             # JS 导出入口
│     └─ index.node           # 本地编译产物
└─ README.md
```

发版说明见：

- [RELEASE.md](./RELEASE.md)

## 核心特性

- Wait-free 双缓冲同步模型
  - 使用 `state + data_0 + data_1` 组合管理一致性快照
  - 读者不会被写者长时间阻塞
- 文件映射协议
  - 支持通过共享目录进行本地进程通信
  - 文件映射目录需要由调用侧显式提供；核心库不再内置默认本地缓存目录
- 请求 / 响应与发布 / 订阅
  - C# 侧通过 `TigaMessageBus` / `TigaPerClientServer` 暴露消息模式
  - Node 侧通过 `tigaWrite` / `tigaRead` / `tigaInvoke` 对接
- 兼容当前通知协议
  - Node 侧已对齐当前 C# 的 `_notify` slot-based 通知方案
  - 已修复跨语言 listener 判活中的进程启动时间基准差异
- 多客户端拓扑
  - 服务端按 `base.req.<clientId>` / `base.resp.<clientId>` 为每个客户端建立独立通道
  - 当前跨语言实测链路聚焦于 `MappingType.File` + per-client request/response

## 协议概览

### 数据面

每个逻辑通道对应以下文件：

- `<prefix>_state`
- `<prefix>_data_0`
- `<prefix>_data_1`
- `<prefix>_notify`

其中：

- `state` 记录当前活动缓冲区索引、数据大小和校验信息
- `data_0` / `data_1` 采用双缓冲承载实际负载
- `notify` 存放监听槽位，用于进程间非轮询唤醒

### 每客户端通道命名

以逻辑通道 `<BaseChannel>` 为例：

- 请求通道：`<BaseChannel>.req.<clientId>`
- 响应通道：`<BaseChannel>.resp.<clientId>`

服务端会在共享目录中发现：

- `<FilePrefix>_<BaseChannel>.req.<clientId>_state`

随后自动为该客户端建立消息总线并注册处理器。

## 运行环境

### 当前推荐环境

- Windows
- .NET SDK 6+/10 SDK 均可运行当前示例
- Rust toolchain
- Node.js + npm

### 平台说明

- C# 核心库的文件映射设计本身兼容 Windows / Linux 场景
- 当前仓库中的 **示例服务端** 和 **Node N-API 互通链路** 以 Windows 为主要验证平台
- `nodejs/mmap-napi` 当前依赖 `windows-sys`，因此 Node 互通部分应视为 Windows 优先

## 环境变量速查

| 变量名                  | 作用                          | C# 示例                                           | Node 示例                      |
| -------------------- | --------------------------- | ----------------------------------------------- | ---------------------------- |
| `TIGA_IPC_DIR`       | 指定文件映射目录；服务端与客户端必须一致        | 示例程序读取后传给 `TigaIpcOptions.FileMappingDirectory` | 示例程序读取后传给 `mappingDirectory` |
| `TIGA_IPC_CLIENT_ID` | 指定客户端标识，用于拼接 `req/resp` 通道名 | 默认使用 `进程号-Guid`                                 | 默认使用 `node-<pid>`            |

如果你只是想先把互通跑通，最关键的是：

- 服务端和客户端使用同一个 `TIGA_IPC_DIR`
- 每个客户端使用唯一的 `TIGA_IPC_CLIENT_ID`
- Node 侧 `index.node` 与当前 Rust 代码是同一轮构建产物

## 最快互通路径

第一次接手这个仓库，推荐直接按这个顺序验证：

如果你只是想最快确认当前仓库的 C# / Node 互通链路，可以直接运行：

```powershell
.\scripts\smoke-interop.ps1
```

这个脚本会：

- 显式创建或使用一个 `MappingDirectory`
- 构建 C# 服务端与 Node addon
- 启动 C# 服务端
- 调用 `nodejs/mmap-napi/examples/tiga_invoke.js`
- 在成功后输出服务端日志路径

如果你已经提前构建过，也可以跳过构建阶段：

```powershell
.\scripts\smoke-interop.ps1 -SkipBuild
```

### 1. 启动 C# 服务端

```powershell
cd .\csharp\TigaIpc.Server
$env:TIGA_IPC_DIR = 'C:\Users\Administrator\AppData\Local\Temp\tiga-ipc'
dotnet run -c Release
```

预期看到：

- `Channel Name: SampleChannel`
- `Mapping Directory: ...\tiga-ipc`
- `Server ready. Press Ctrl+C to exit.`

### 2. 构建 Node 本地插件

```powershell
cd .\nodejs\mmap-napi
cargo build --release
Copy-Item .\target\release\mmap_napi.dll .\index.node -Force
```

### 3. 从 Node 调用服务端

```powershell
cd .\nodejs\mmap-napi
$env:TIGA_IPC_CLIENT_ID = 'sample-client'
$env:TIGA_IPC_DIR = 'C:\Users\Administrator\AppData\Local\Temp\tiga-ipc'
node .\examples\tiga_invoke.js
```

预期输出：

```text
clientId=sample-client
mappingDirectory=C:\Users\Administrator\AppData\Local\Temp\tiga-ipc
request=SampleChannel.req.sample-client
response=SampleChannel.resp.sample-client
invoke reply: Echo response: hello from sample-client
```

如果这一组命令能跑通，说明下面几件事同时成立：

- `_state / _data_0 / _data_1 / _notify` 文件布局匹配
- C# 服务端已经正确发现 `<BaseChannel>.req.<clientId>`
- Rust/Node 侧生成的消息体能被 C# 正常解析
- C# 返回值能被 Node 侧正确读回

## C# 快速开始

### 1. 启动服务端

```powershell
cd .\csharp\TigaIpc.Server
$env:TIGA_IPC_DIR = 'C:\Users\Administrator\AppData\Local\Temp\tiga-ipc'
dotnet run
```

当前示例要求显式提供映射目录：

- 通道名：`SampleChannel`
- 映射目录：通过 `TIGA_IPC_DIR` 或第一个命令行参数传入，然后写入 `TigaIpcOptions.FileMappingDirectory`

服务端当前注册的示例方法：

- `echo`
- `fetchProfile`
- `backgroundJob`
- `notifyOnly`

### 2. 启动 C# 客户端

```powershell
cd .\csharp\TigaIpc.Client
$env:TIGA_IPC_DIR = 'C:\Users\Administrator\AppData\Local\Temp\tiga-ipc'
dotnet run
```

客户端支持命令：

- `invoke <text>`
- `profile <name>`
- `publish <text>`
- `bg`
- `void`
- `stress <count>`

## Node.js 快速开始

### 导出函数

`nodejs/mmap-napi` 当前导出：

- `initialized()`
- `tigaWrite(name, message, options?)`
- `tigaRead(name, options?)`
- `tigaInvoke(requestName, responseName, method, data, options?)`

其中 `options` 当前统一走对象风格：

- `tigaWrite(..., { mappingDirectory, mediaType? })`
- `tigaRead(..., { mappingDirectory, lastId? })`
- `tigaInvoke(..., { mappingDirectory, timeoutMs? })`

类型定义位于：

- [index.d.ts](./nodejs/mmap-napi/index.d.ts)

### 构建 Node 插件

先进入目录：

```powershell
cd .\nodejs\mmap-napi
```

首选命令：

```powershell
npm run build
```

如果你的环境里 `npx @napi-rs/cli build` 出现：

```text
npm error could not determine executable to run
```

可以使用当前仓库已经验证过的兜底方式：

```powershell
cargo build --release
Copy-Item .\target\release\mmap_napi.dll .\index.node -Force
```

### 运行 Node 示例

```powershell
cd .\nodejs\mmap-napi
$env:TIGA_IPC_CLIENT_ID = 'sample-client'
$env:TIGA_IPC_DIR = 'C:\Users\Administrator\AppData\Local\Temp\tiga-ipc'
node .\examples\tiga_invoke.js
```

成功时会输出：

```text
clientId=sample-client
mappingDirectory=C:\Users\Administrator\AppData\Local\Temp\tiga-ipc
request=SampleChannel.req.sample-client
response=SampleChannel.resp.sample-client
invoke reply: Echo response: hello from sample-client
```

## C# / Node 互通示例

这是当前仓库内已经验证通过的一组命令。

### 1. 启动服务端

```powershell
cd .\csharp\TigaIpc.Server
$env:TIGA_IPC_DIR = 'C:\Users\Administrator\AppData\Local\Temp\tiga-ipc'
dotnet run -c Release
```

### 2. 构建 Node 插件

```powershell
cd .\nodejs\mmap-napi
cargo build --release
Copy-Item .\target\release\mmap_napi.dll .\index.node -Force
```

### 3. 调用服务端

```powershell
$env:TIGA_IPC_CLIENT_ID = 'sample-client'
$env:TIGA_IPC_DIR = 'C:\Users\Administrator\AppData\Local\Temp\tiga-ipc'
node .\examples\tiga_invoke.js
```

## 兼容边界

这一轮 README 和代码整理，文档里只对已经实测过的范围做承诺：

- 已验证：Windows 下 `MappingType.File` 的 C# 服务端 与 Node `mmap-napi` 通过 per-client `req/resp` 通道互通
- 已验证：Node `tigaInvoke` 调用 C# `Register / RegisterAsync` 处理器
- 已验证：当前仓库里的 `_notify` slot-based 通知协议
- 已修复：Rust 与 C# 之间 listener 判活使用的时间基准不一致问题

下面这些不应被视为“这轮已经完成验证”的范围：

- Node 与 C# 的命名内存映射模式互通
- 非 Windows 平台上的 Node 互通
- 旧通知布局与当前通知布局的混跑兼容
- 所有业务消息形态都完成了跨语言回归，只是 `tigaInvoke` 主链路已经实测通过

## 验证命令

### C# 测试

```powershell
dotnet test .\csharp\TigaIpc.Tests\TigaIpc.Tests.csproj -c Release
```

当前实测结果：

- `23` 个测试全部通过

### Node Rust 单元测试

```powershell
cd .\nodejs\mmap-napi
cargo test
```

当前实测结果：

- `4` 个测试通过

说明：

- `cargo test` 结束后可能会看到一串 Node-API `GetProcAddress failed` 日志
- 当前 `mmap-napi` 的纯 Rust 测试仍然是通过的
- 这些日志不会影响我们已经验证过的通知层单元测试结果

## 当前代码组织说明

### C# 侧

- `TigaIpc/IO/WaitFreeMemoryMappedFile.cs`
  - wait-free 数据面实现
- `TigaIpc/Messaging/TigaMessageBus*.cs`
  - 消息总线、注册、响应、发布逻辑
- `TigaIpc/Messaging/TigaPerClientServer.cs`
  - 每客户端拓扑与发现逻辑

### Node 侧

- `src/tiga_channel.rs`
  - `state/data_0/data_1` 双缓冲读写
- `src/tiga_notify.rs`
  - 通知公共入口与测试
- `src/tiga_notify_windows.rs`
  - Windows slot-based notify 实现
- `src/tiga/`
  - `paths.rs`：路径解析
  - `common.rs`：共享 helper
  - `read.rs`：`tigaRead`
  - `write.rs`：`tigaWrite`
  - `invoke.rs`：`tigaInvoke`

## 排障建议

### 1. Node 提示 `tigaInvoke not found`

先确认 `index.node` 是当前代码重新编译后的产物：

```powershell
cd .\nodejs\mmap-napi
cargo build --release
Copy-Item .\target\release\mmap_napi.dll .\index.node -Force
```

### 2. 服务端启动了，但 Node 调用没有响应

优先检查以下三项：

- 两边最终传入的映射目录是否完全一致
- Node 侧请求通道是否是 `<BaseChannel>.req.<clientId>`
- 服务端是否已经以 `Release` 或当前本地可运行配置正常启动

### 3. 收到的是旧目录里的残留文件

切换目录或反复调试后，最容易出现“程序跑起来了，但其实双方没连到同一套映射文件”的问题。最简单的做法是：

- 显式设置统一的 `TIGA_IPC_DIR`，或在两边传入相同的目录参数
- 调试前确认该目录下生成的是当前这次运行对应的 `<FilePrefix>_*` 文件

### 4. C# 客户端 / Node 客户端并行调试时互相干扰

请确保它们使用不同的 `TIGA_IPC_CLIENT_ID`。否则多个客户端会争用同一组：

- `<BaseChannel>.req.<clientId>`
- `<BaseChannel>.resp.<clientId>`

## 已知注意事项

### 1. .NET 6 警告

当前 C# 项目仍然包含 `net6.0` 目标框架，因此在较新的 SDK 下会看到：

- `NETSDK1138`
- 若干 `Package ... doesn't support net6.0` 警告

这些警告不会阻止当前示例和测试运行，但后续建议升级目标框架或清理依赖版本。

### 2. NuGet 漏洞源告警

当前环境中可能会看到：

```text
NU1900: 获取包漏洞数据时出错
```

这通常是因为本地配置的某个 NuGet 源不可访问，不影响当前本地构建和测试通过。

### 3. npm / npx 构建脚本在部分环境下失败

当前仓库环境里，`npm run build` 可能因为 `npx` 解析问题失败。

如果出现这个问题，请直接使用：

```powershell
cargo build --release
Copy-Item .\target\release\mmap_napi.dll .\index.node -Force
```

## License

MIT. See [LICENSE](./LICENSE).
