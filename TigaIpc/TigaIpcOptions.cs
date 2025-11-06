using System.Diagnostics;

namespace TigaIpc
{
    /// <summary>
    /// Options for TigaIPC configuration.
    /// </summary>
    public class TigaIpcOptions
    {
        public const int DefaultMaxFileSize = 1024 * 1024;
        public const int DefaultMaxReaderCount = 6;
        public const int DefaultCompressionThreshold = 256;
        public const bool DefaultEnableCompression = true;

        public static readonly TimeSpan DefaultMinMessageAge = TimeSpan.FromMilliseconds(1_000);
        public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan DefaultInvokeTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the name of this set of locks and memory mapped file, default value is process name.
        /// </summary>
        public string Name { get; set; } = Process.GetCurrentProcess().ProcessName;

        /// <summary>
        /// Gets or sets the maximum amount of data that can be written to the file memory mapped file, default is 1 MiB
        /// </summary>
        public long MaxFileSize { get; set; } = DefaultMaxFileSize;

        /// <summary>
        /// Gets or sets maxium simultaneous readers, default is 6
        /// </summary>
        public int MaxReaderCount { get; set; } = DefaultMaxReaderCount;

        /// <summary>
        /// Gets or sets the minimum amount of time messages are required to live before removal from the file, default is 1 second
        /// </summary>
        public TimeSpan MinMessageAge { get; set; } = DefaultMinMessageAge;

        /// <summary>
        /// Gets or sets how long to wait before giving up aquiring read and write locks, default is 5 seconds
        /// </summary>
        public TimeSpan WaitTimeout { get; set; } = DefaultWaitTimeout;

        /// <summary>
        /// Gets or sets a value indicating whether 是否启用消息压缩功能，默认为true
        /// </summary>
        public bool EnableCompression { get; set; } = DefaultEnableCompression;

        /// <summary>
        /// Gets or sets 消息压缩阈值（字节），超过此大小的消息将被压缩，默认为256字节
        /// </summary>
        public int CompressionThreshold { get; set; } = DefaultCompressionThreshold;

        /// <summary>
        /// Gets or sets 默认的方法调用超时时间，默认为30秒
        /// </summary>
        public TimeSpan InvokeTimeout { get; set; } = DefaultInvokeTimeout;

        /// <summary>
        /// Gets or sets 消息发布最大重试次数，默认为3次
        /// </summary>
        public int MaxPublishRetries { get; set; } = 3;

        /// <summary>
        /// Gets or sets a value indicating whether 是否使用借鉴 mmap-sync 的无等待同步模型，默认为 false
        /// </summary>
        public bool UseWaitFreeSynchronization { get; set; } = true;
    }
}