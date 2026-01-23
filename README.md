## TigaIPC

### Wait-free 同步模式

- 采用 [Cloudflare/mmap-sync](https://github.com/cloudflare/mmap-sync) 的双缓冲与版本协调思路，实现单写多读的无锁访问。
- 使用独立 state + data_0/data_1 文件布局，版本号打包 `idx/size/checksum` 与读者计数。
- wait-free 模式在超过 `ReaderGraceTimeout` 后会重置读计数继续写，偏可用性优先；`WriterSleepDuration` 控制写者轮询间隔。
- `ReadLease`/`ReadRaw` 提供零拷贝读取；`WriteRaw` 支持直接写入原始字节。
- `Write<T>`/`Read<T>` 支持自定义序列化/校验函数，覆盖 mmap-sync 的 typed API 语义。
- 默认使用 WyHash 校验，可通过 `ChecksumProvider` 替换；`VerifyChecksumOnRead` 控制读校验开关。
- File-backed 默认优先使用 `/dev/shm`（Unix），也可通过 `FileMappingDirectory` 指定目录。
- 可通过 `ITigaMessageBus.GetSynchronizationMetrics()` 获取 lock timeout / abandoned / reader reset 统计。
- 通过 `LogBookSchemaVersion`/`AllowLegacyLogBook` 控制日志结构版本与兼容策略（默认兼容旧格式）。
- 可通过 `FileStreamFactory`/`NamedMemoryMappedFileFactory`/`EventWaitHandleFactory` 注入 ACL 配置。
- `UseSingleWriterLock` 在 `MappingType.File` 下启用 `flock`（Unix-only）确保单写者。
- `TigaPerClientServer` + `PerClientChannelNames` 支持 per-client 请求/响应通道，避免多客户端并发写冲突；`ClientDiscoveryInterval` 控制文件映射发现频率。
