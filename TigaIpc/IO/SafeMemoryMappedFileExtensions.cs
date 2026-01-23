using Microsoft.Extensions.Logging;

namespace TigaIpc.IO;

/// <summary>
/// 为ITigaMemoryMappedFile提供安全读写的扩展方法
/// </summary>
public static class SafeMemoryMappedFileExtensions
{
    /// <summary>
    /// 安全地读取内存映射文件，即使在出现异常时也能保证资源释放
    /// </summary>
    /// <typeparam name="T">返回值类型</typeparam>
    /// <param name="memoryMappedFile">内存映射文件</param>
    /// <param name="readData">读取数据的函数</param>
    /// <param name="defaultValue">出现异常时的默认返回值</param>
    /// <param name="logger">记录器</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>操作结果或默认值</returns>
    public static T TryRead<T>(
        this ITigaMemoryMappedFile memoryMappedFile,
        Func<MemoryStream, T> readData,
        T defaultValue,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return memoryMappedFile.Read(readData, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            logger?.LogWarning(ex, "Timeout occurred while reading from memory mapped file");
            return defaultValue;
        }
        catch (OperationCanceledException)
        {
            // 预期的取消异常，不记录警告
            return defaultValue;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error occurred while reading from memory mapped file");
            return defaultValue;
        }
    }

    /// <summary>
    /// 安全地写入内存映射文件，即使在出现异常时也能保证资源释放
    /// </summary>
    /// <param name="memoryMappedFile">内存映射文件</param>
    /// <param name="data">要写入的数据</param>
    /// <param name="logger">记录器</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="retryCount">重试次数</param>
    /// <returns>操作是否成功</returns>
    public static bool TryWrite(
        this ITigaMemoryMappedFile memoryMappedFile,
        MemoryStream data,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        int retryCount = 3)
    {
        for (int i = 0; i < retryCount; i++)
        {
            try
            {
                memoryMappedFile.Write(data, cancellationToken);
                return true;
            }
            catch (TimeoutException ex)
            {
                if (i == retryCount - 1)
                {
                    logger?.LogWarning(ex, "Timeout occurred while writing to memory mapped file (attempt {Attempt}/{MaxAttempts})",
                        i + 1, retryCount);
                }
                // 在下一次重试前稍微延迟
                if (i < retryCount - 1)
                {
                    Thread.Sleep(50 * (i + 1));
                }
            }
            catch (OperationCanceledException)
            {
                // 取消操作，不再重试
                return false;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error occurred while writing to memory mapped file (attempt {Attempt}/{MaxAttempts})",
                    i + 1, retryCount);
                if (i < retryCount - 1)
                {
                    Thread.Sleep(50 * (i + 1));
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 安全地读写内存映射文件，即使在出现异常时也能保证资源释放
    /// </summary>
    /// <param name="memoryMappedFile">内存映射文件</param>
    /// <param name="updateFunc">更新函数</param>
    /// <param name="logger">记录器</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="retryCount">重试次数</param>
    /// <returns>操作是否成功</returns>
    public static bool TryReadWrite(
        this ITigaMemoryMappedFile memoryMappedFile,
        Action<MemoryStream, MemoryStream> updateFunc,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        int retryCount = 3)
    {
        for (int i = 0; i < retryCount; i++)
        {
            try
            {
                memoryMappedFile.ReadWrite(updateFunc, cancellationToken);
                return true;
            }
            catch (TimeoutException ex)
            {
                if (i == retryCount - 1)
                {
                    logger?.LogWarning(ex, "Timeout occurred during ReadWrite operation on memory mapped file (attempt {Attempt}/{MaxAttempts})",
                        i + 1, retryCount);
                }
                // 在下一次重试前稍微延迟
                if (i < retryCount - 1)
                {
                    Thread.Sleep(50 * (i + 1));
                }
            }
            catch (OperationCanceledException)
            {
                // 取消操作，不再重试
                return false;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error occurred during ReadWrite operation on memory mapped file (attempt {Attempt}/{MaxAttempts})",
                    i + 1, retryCount);
                if (i < retryCount - 1)
                {
                    Thread.Sleep(50 * (i + 1));
                }
            }
        }
        return false;
    }
}
