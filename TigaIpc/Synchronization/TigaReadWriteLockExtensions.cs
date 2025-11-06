using Microsoft.Extensions.Logging;

namespace TigaIpc.Synchronization;

/// <summary>
/// 提供TigaReadWriteLock的安全使用扩展方法
/// </summary>
public static class TigaReadWriteLockExtensions
{
    /// <summary>
    /// 安全地获取并使用读锁，确保即使在异常情况下也会释放锁
    /// </summary>
    /// <param name="readWriteLock">读写锁实例</param>
    /// <param name="action">获取锁后要执行的操作</param>
    /// <param name="logger">记录器</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>操作结果</returns>
    public static bool TryWithReadLock(
        this ITigaReadWriteLock readWriteLock,
        Action action,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var readLock = readWriteLock.AcquireReadLock(cancellationToken);
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error occurred while holding read lock");
                return false;
            }
        }
        catch (TimeoutException ex)
        {
            logger?.LogWarning(ex, "Timeout occurred while acquiring read lock");
            return false;
        }
        catch (OperationCanceledException)
        {
            // 预期的取消异常，不记录警告
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unexpected error occurred while acquiring read lock");
            return false;
        }
    }

    /// <summary>
    /// 安全地获取并使用读锁，确保即使在异常情况下也会释放锁
    /// </summary>
    /// <typeparam name="T">返回值类型</typeparam>
    /// <param name="readWriteLock">读写锁实例</param>
    /// <param name="func">获取锁后要执行的操作</param>
    /// <param name="defaultValue">发生异常时的默认返回值</param>
    /// <param name="logger">记录器</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>操作结果或默认值</returns>
    public static T TryWithReadLock<T>(
        this ITigaReadWriteLock readWriteLock,
        Func<T> func,
        T defaultValue,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var readLock = readWriteLock.AcquireReadLock(cancellationToken);
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error occurred while holding read lock");
                return defaultValue;
            }
        }
        catch (TimeoutException ex)
        {
            logger?.LogWarning(ex, "Timeout occurred while acquiring read lock");
            return defaultValue;
        }
        catch (OperationCanceledException)
        {
            // 预期的取消异常，不记录警告
            return defaultValue;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unexpected error occurred while acquiring read lock");
            return defaultValue;
        }
    }

    /// <summary>
    /// 安全地获取并使用写锁，确保即使在异常情况下也会释放锁
    /// </summary>
    /// <param name="readWriteLock">读写锁实例</param>
    /// <param name="action">获取锁后要执行的操作</param>
    /// <param name="logger">记录器</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>操作是否成功</returns>
    public static bool TryWithWriteLock(
        this ITigaReadWriteLock readWriteLock,
        Action action,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var writeLock = readWriteLock.AcquireWriteLock(cancellationToken);
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error occurred while holding write lock");
                return false;
            }
        }
        catch (TimeoutException ex)
        {
            logger?.LogWarning(ex, "Timeout occurred while acquiring write lock");
            return false;
        }
        catch (OperationCanceledException)
        {
            // 预期的取消异常，不记录警告
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unexpected error occurred while acquiring write lock");
            return false;
        }
    }

    /// <summary>
    /// 安全地获取并使用写锁，确保即使在异常情况下也会释放锁
    /// </summary>
    /// <typeparam name="T">返回值类型</typeparam>
    /// <param name="readWriteLock">读写锁实例</param>
    /// <param name="func">获取锁后要执行的操作</param>
    /// <param name="defaultValue">发生异常时的默认返回值</param>
    /// <param name="logger">记录器</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>操作结果或默认值</returns>
    public static T TryWithWriteLock<T>(
        this ITigaReadWriteLock readWriteLock,
        Func<T> func,
        T defaultValue,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var writeLock = readWriteLock.AcquireWriteLock(cancellationToken);
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error occurred while holding write lock");
                return defaultValue;
            }
        }
        catch (TimeoutException ex)
        {
            logger?.LogWarning(ex, "Timeout occurred while acquiring write lock");
            return defaultValue;
        }
        catch (OperationCanceledException)
        {
            // 预期的取消异常，不记录警告
            return defaultValue;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unexpected error occurred while acquiring write lock");
            return defaultValue;
        }
    }
}