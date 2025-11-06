## TigaIPC

### Wait-free 同步模式

- 新增配置项 `TigaIpcOptions.UseWaitFreeSynchronization`，借鉴 [Cloudflare/mmap-sync](https://github.com/cloudflare/mmap-sync) 的双缓冲与版本协调思路，实现单写多读的无锁访问。
- 启用方式：在服务注册时将 `UseWaitFreeSynchronization` 设为 `true`（例如调用 `new TigaIpcOptions().WithRobustConfiguration()`）。
- 兼容性：未开启该开关时仍保留原有互斥/信号量方案，便于渐进迁移。