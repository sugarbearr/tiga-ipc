using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using TigaIpc.IO;

namespace TigaIpc
{
    /// <summary>
    /// Options for TigaIPC configuration.
    /// </summary>
    public class TigaIpcOptions
    {
        public const int DefaultMaxFileSize = 1024 * 1024;
        public const int DefaultCompressionThreshold = 256;
        public const bool DefaultEnableCompression = true;

        public static readonly TimeSpan DefaultMinMessageAge = TimeSpan.FromMilliseconds(1_000);
        public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan DefaultInvokeTimeout = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan DefaultWriterSleepDuration = TimeSpan.FromMilliseconds(1);
        public static readonly TimeSpan DefaultClientDiscoveryInterval = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Gets or sets the logical channel name for this IPC topology, default value is process name.
        /// </summary>
        public string ChannelName { get; set; } = GetDefaultChannelName();

        /// <summary>
        /// Gets or sets the maximum amount of data that can be written to the file memory mapped file, default is 1 MiB
        /// </summary>
        public long MaxFileSize { get; set; } = DefaultMaxFileSize;

        /// <summary>
        /// Gets or sets the minimum amount of time messages are required to live before removal from the file, default is 1 second
        /// </summary>
        public TimeSpan MinMessageAge { get; set; } = DefaultMinMessageAge;

        /// <summary>
        /// Gets or sets how long to wait before giving up aquiring read and write locks, default is 5 seconds
        /// </summary>
        public TimeSpan WaitTimeout { get; set; } = DefaultWaitTimeout;

        /// <summary>
        /// Gets or sets how long to wait before resetting reader counts in wait-free mode, default is 5 seconds.
        /// </summary>
        public TimeSpan ReaderGraceTimeout { get; set; } = DefaultWaitTimeout;

        /// <summary>
        /// Gets or sets how long the writer sleeps between reader checks, default is 1ms.
        /// </summary>
        public TimeSpan WriterSleepDuration { get; set; } = DefaultWriterSleepDuration;

        /// <summary>
        /// Gets or sets how often the server scans for new client channels when using per-client topology.
        /// </summary>
        public TimeSpan ClientDiscoveryInterval { get; set; } = DefaultClientDiscoveryInterval;

        /// <summary>
        /// Gets or sets whether to verify checksum on read operations by default.
        /// </summary>
        public bool VerifyChecksumOnRead { get; set; } = true;

        /// <summary>
        /// Gets or sets custom checksum provider (default is WyHash).
        /// </summary>
        public ChecksumProvider? ChecksumProvider { get; set; }

        /// <summary>
        /// Gets or sets the IPC directory for file-backed mappings.
        /// Required when <see cref="MappingType.File"/> is used.
        /// </summary>
        public string? IpcDirectory { get; set; }

        /// <summary>
        /// Gets or sets log book schema version for serialization compatibility (default 1).
        /// </summary>
        public int LogBookSchemaVersion { get; set; } = 1;

        /// <summary>
        /// Gets or sets whether legacy log book payloads are allowed (default true).
        /// </summary>
        public bool AllowLegacyLogBook { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to use a single-writer file lock (flock) when MappingType.File is used.
        /// </summary>
        public bool UseSingleWriterLock { get; set; }

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
        /// Optional factory for file-backed mapping to apply custom ACLs.
        /// </summary>
        public Func<string, long, FileStream>? FileStreamFactory { get; set; }

        /// <summary>
        /// Optional factory for named memory mapped file to apply custom ACLs.
        /// </summary>
        public Func<string, long, MemoryMappedFile>? NamedMemoryMappedFileFactory { get; set; }

        /// <summary>
        /// Optional factory for named event wait handle to apply custom ACLs.
        /// </summary>
        public Func<string, EventWaitHandle>? EventWaitHandleFactory { get; set; }

        private static string GetDefaultChannelName()
        {
#if NET6_0_OR_GREATER
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                return Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "unknown";
            }
#endif

            using var process = Process.GetCurrentProcess();
            return process.ProcessName;
        }

    }
}
