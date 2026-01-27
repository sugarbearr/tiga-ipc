# TigaIPC

TigaIPC 是一个基于内存映射文件（Memory Mapped File）的高性能、无锁（Wait-free）进程间通信（IPC）库。

它借鉴了 [Cloudflare/mmap-sync](https://github.com/cloudflare/mmap-sync) 的设计思路，实现了单写者多读者的无锁同步机制，非常适合高吞吐、低延迟的本地 IPC 场景。

## ✨ 特性

- **Wait-free 同步模式**：
  - 采用双缓冲（Double Buffering）与版本协调机制，实现单写多读的无锁访问。
  - 读者永远不会被写者阻塞，写者在超时后可强制重置读者状态，优先保证系统可用性。
- **高性能数据传输**：
  - `ReadLease`/`ReadRaw` 提供零拷贝读取能力。
  - `WriteRaw` 支持直接写入原始字节，减少内存分配。
- **类型安全与灵活性**：
  - `Write<T>`/`Read<T>` 支持泛型 API，可自定义序列化器（默认集成 MessagePack）。
  - 支持请求/响应（Request/Response）模式和发布/订阅（Publish/Subscribe）模式。
- **鲁棒性设计**：
  - 数据完整性校验：默认使用 WyHash 算法，支持自定义校验策略。
  - 进程崩溃恢复：自动检测并重置失效的读者/写者状态。
- **跨平台支持**：
  - 支持 Windows 和 Linux（优先使用 `/dev/shm`）。
  - 支持 Docker 容器环境下的共享内存通信。

## 🚀 快速开始

本项目包含 `TigaIpc.Server` 和 `TigaIpc.Client` 两个示例项目，展示了如何使用 TigaIPC 进行通信。

### 1. 启动服务端

服务端负责初始化 IPC 通道并注册消息处理函数。

```bash
cd TigaIpc.Server
dotnet run
```

服务端启动后，会监听名为 `Excel` 的通道，并提供以下服务：
- `method`: 简单的字符串回显。
- `GetAllCookie`: 接收复杂对象并返回结果。
- `method2`: 模拟后台耗时任务。
- `method3`: 无返回值的异步任务。
- 定时发送心跳消息。

### 2. 启动客户端

客户端连接到服务端，并可以通过命令行交互式发送请求。

```bash
cd TigaIpc.Client
dotnet run
```

客户端支持以下命令：
- `invoke <text>`: 调用服务端的 `method` 方法。
- `cookie <name>`: 调用 `GetAllCookie` 方法，发送结构化数据。
- `bg`: 调用 `method2`，测试后台任务。
- `void`: 调用 `method3`，测试无返回值调用。
- `publish <text>`: 发送单向消息给服务端。
- `stress <count>`: 进行压力测试，连续发送请求。

### 示例交互

**Client:**
```text
> invoke HelloTiga
Sending invoke: HelloTiga...
Response (2ms): Echo from Server: HelloTiga
```

**Server:**
```text
[10:30:01.123] [Invoke] method received: 'HelloTiga'
```

## 📚 核心概念

### Wait-free 机制
TigaIPC 使用独立的状态文件 (`state`) 和两个数据文件 (`data_0`, `data_1`)。
- **State**: 存储版本号、当前使用的数据文件索引 (`idx`)、数据大小和校验和。
- **Data**: 实际的数据存储区域。

写者写入数据时，会先写入非当前活动的那个数据文件，然后原子更新 `state` 中的索引和版本号。读者读取时，通过校验版本号来确保读取到的一致性快照。

### 通道 (Channel)
- **TigaMessageBus**: 基础消息总线，用于点对点或广播通信。
- **TigaPerClientServer**: 封装了服务端逻辑，支持多客户端管理。它为每个连接的客户端创建独立的请求/响应通道，避免多客户端写入冲突。

## 🛠️ 配置选项

可以通过 `TigaIpcOptions` 进行详细配置：

```csharp
var options = new TigaIpcOptions
{
    Name = "MyChannel",
    FileMappingDirectory = "/tmp/tiga-ipc", // 指定映射文件目录
    MessageBusType = MessageBusType.WaitFree, // 同步模式
    Capacity = 1024 * 1024, // 初始容量 1MB
    ReaderGraceTimeout = TimeSpan.FromSeconds(1) // 读者超时时间
};
```

## 📦 项目结构

- **TigaIpc**: 核心库。
- **TigaIpc.Server**: 示例服务端。
- **TigaIpc.Client**: 示例客户端。
- **TigaIpc.Tests**: 单元测试与集成测试。
- **TigaIpc.Benchmarks**: 性能基准测试。

## 📄 License

MIT
