using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace TigaIpc.Synchronization;

/// <summary>
/// 锁恢复服务，用于检测和清理僵尸锁
/// </summary>
public class LockRecoveryService : IDisposable
{
    private readonly ILogger<LockRecoveryService> _logger;
    private readonly TimeSpan _checkInterval;
    private readonly ConcurrentDictionary<string, DateTime> _knownLocks = new();
    private readonly TimeSpan _lockAbandonedThreshold;
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;
    private bool _disposed;

    /// <summary>
    /// 初始化锁恢复服务的新实例
    /// </summary>
    /// <param name="logger">记录器</param>
    /// <param name="checkIntervalSeconds">检查间隔（秒）</param>
    /// <param name="lockAbandonedThresholdSeconds">锁被视为废弃的阈值（秒）</param>
    public LockRecoveryService(
        ILogger<LockRecoveryService> logger,
        int checkIntervalSeconds = 60,
        int lockAbandonedThresholdSeconds = 300)
    {
        _logger = logger;
        _checkInterval = TimeSpan.FromSeconds(checkIntervalSeconds);
        _lockAbandonedThreshold = TimeSpan.FromSeconds(lockAbandonedThresholdSeconds);
    }

    /// <summary>
    /// 启动锁监控服务
    /// </summary>
    public void Start()
    {
        if (_monitorTask != null)
        {
            return;
        }

        _monitorTask = Task.Run(ExecuteAsync);
        _logger.LogInformation("Lock recovery service started");
    }

    /// <summary>
    /// 停止锁监控服务
    /// </summary>
    public void Stop()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        _logger.LogInformation("Lock recovery service stopping");
    }

    /// <summary>
    /// 注册一个锁以进行监控
    /// </summary>
    /// <param name="lockName">锁名称</param>
    public void RegisterLock(string lockName)
    {
        _knownLocks.AddOrUpdate(lockName, DateTime.UtcNow, (_, _) => DateTime.UtcNow);
        _logger.LogDebug("Registered lock {LockName} for monitoring", lockName);
    }

    /// <summary>
    /// 更新锁的活动时间戳
    /// </summary>
    /// <param name="lockName">锁名称</param>
    public void UpdateLockActivity(string lockName)
    {
        if (_knownLocks.TryGetValue(lockName, out _))
        {
            _knownLocks[lockName] = DateTime.UtcNow;
            _logger.LogTrace("Updated activity timestamp for lock {LockName}", lockName);
        }
    }

    /// <summary>
    /// 取消对锁的监控
    /// </summary>
    /// <param name="lockName">锁名称</param>
    public void UnregisterLock(string lockName)
    {
        if (_knownLocks.TryRemove(lockName, out _))
        {
            _logger.LogDebug("Unregistered lock {LockName} from monitoring", lockName);
        }
    }

    /// <summary>
    /// 执行服务
    /// </summary>
    private async Task ExecuteAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkInterval, _cts.Token);
                CheckForAbandonedLocks();
            }
            catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
            {
                // 正常取消，不需要处理
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during abandoned lock check");
            }
        }

        _logger.LogInformation("Lock recovery service stopped");
    }

    private void CheckForAbandonedLocks()
    {
        var now = DateTime.UtcNow;
        var abandonedLocks = new List<string>();

        foreach (var lockEntry in _knownLocks)
        {
            if ((now - lockEntry.Value) > _lockAbandonedThreshold)
            {
                abandonedLocks.Add(lockEntry.Key);
            }
        }

        foreach (var lockName in abandonedLocks)
        {
            try
            {
                // 尝试强制释放锁
                if (TryForceReleaseLock(lockName))
                {
                    _logger.LogWarning("Force released potentially abandoned lock: {LockName}", lockName);
                    _knownLocks.TryRemove(lockName, out _);
                }
                else
                {
                    // 如果无法释放，更新时间戳，避免频繁尝试释放同一个锁
                    _knownLocks[lockName] = DateTime.UtcNow.AddSeconds(-_lockAbandonedThreshold.TotalSeconds + 60);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to force release abandoned lock: {LockName}", lockName);
            }
        }
    }

    private bool TryForceReleaseLock(string lockName)
    {
        try
        {
            // 创建新的信号量和互斥体实例，尝试进行清理
            using var mutex = TigaReadWriteLock.CreateMutex(lockName);
            if (mutex.WaitOne(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    // 安全地创建并使用信号量
                    using var semaphore =
                        TigaReadWriteLock.CreateSemaphore(lockName, TigaIpcOptions.DefaultMaxReaderCount);
                    _logger.LogInformation("Successfully acquired mutex for abandoned lock: {LockName}", lockName);
                    return true;
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error trying to force release lock: {LockName}", lockName);
            return false;
        }
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            Stop();
            _cts.Dispose();
        }

        _disposed = true;
    }
}