using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TigaIpc.Core;

/// <summary>
/// 为TigaIpcOptions提供与MessageBusOptions兼容的扩展方法
/// </summary>
public static class TigaIpcOptionsExtensions
{
    /// <summary>
    /// 获取默认超时时间，从TigaIpcOptions.InvokeTimeout兼容
    /// </summary>
    public static TimeSpan GetDefaultTimeout(this TigaIpcOptions options)
    {
        return options.InvokeTimeout;
    }

    /// <summary>
    /// 获取最大重试次数，从TigaIpcOptions.MaxPublishRetries兼容
    /// </summary>
    public static int GetMaxRetries(this TigaIpcOptions options)
    {
        return options.MaxPublishRetries;
    }

    /// <summary>
    /// 检查是否已经释放资源，兼容不同的.NET版本
    /// </summary>
    public static void ThrowIfDisposed(bool disposed, object instance)
    {
#if NET7_0_OR_GREATER
        ObjectDisposedException.ThrowIf(disposed, instance);
#else
        if (disposed)
        {
            throw new ObjectDisposedException(instance.GetType().Name);
        }
#endif
    }

    /// <summary>
    /// 检查参数是否为null，兼容不同的.NET版本
    /// </summary>
    public static void ThrowIfParameterNull<T>(T? parameter, string paramName)
        where T : class
    {
#if NET7_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(parameter, paramName);
#else
        if (parameter == null)
        {
            throw new ArgumentNullException(paramName);
        }
#endif
    }

    /// <summary>
    /// Configures TigaIpcOptions with service collection
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="optionsAction">Action to configure options</param>
    /// <returns>The service collection</returns>
    public static IServiceCollection ConfigureTigaIpc(
        this IServiceCollection services,
        Action<TigaIpcOptions> optionsAction
    )
    {
        return services.Configure<TigaIpcOptions>(optionsAction);
    }

    /// <summary>
    /// Creates a TigaIpcOptions instance with increased timeout values for high-latency scenarios
    /// </summary>
    /// <param name="waitTimeoutSeconds">Wait timeout in seconds (default 30)</param>
    /// <param name="invokeTimeoutSeconds">Invoke timeout in seconds (default 60)</param>
    /// <returns>A configured TigaIpcOptions instance</returns>
    public static TigaIpcOptions WithIncreasedTimeouts(
        this TigaIpcOptions options,
        int waitTimeoutSeconds = 30,
        int invokeTimeoutSeconds = 60
    )
    {
        options.WaitTimeout = TimeSpan.FromSeconds(waitTimeoutSeconds);
        options.ReaderGraceTimeout = TimeSpan.FromSeconds(waitTimeoutSeconds);
        options.InvokeTimeout = TimeSpan.FromSeconds(invokeTimeoutSeconds);
        return options;
    }

    /// <summary>
    /// 配置更健壮的TigaIpcOptions实例，增加错误恢复能力
    /// </summary>
    /// <param name="options">要配置的选项</param>
    /// <param name="waitTimeoutSeconds">等待超时（秒）</param>
    /// <param name="invokeTimeoutSeconds">调用超时（秒）</param>
    /// <param name="maxRetries">最大重试次数</param>
    /// <returns>配置好的TigaIpcOptions实例</returns>
    public static TigaIpcOptions WithRobustConfiguration(
        this TigaIpcOptions options,
        int waitTimeoutSeconds = 30,
        int invokeTimeoutSeconds = 60,
        int maxRetries = 5
    )
    {
        // 增加超时时间
        options.WaitTimeout = TimeSpan.FromSeconds(waitTimeoutSeconds);
        options.ReaderGraceTimeout = TimeSpan.FromSeconds(waitTimeoutSeconds);
        options.InvokeTimeout = TimeSpan.FromSeconds(invokeTimeoutSeconds);

        // 增加重试次数
        options.MaxPublishRetries = maxRetries;

        // 减小最小消息年龄，允许更快地清理过期消息
        options.MinMessageAge = TimeSpan.FromMilliseconds(500);

        return options;
    }
}
