using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigaIpc.Synchronization;

namespace TigaIpc.IO
{
    /// <summary>
    /// 基于 mmap-sync 思路实现的跨进程共享内存文件，使用双缓冲和版本号提供等待无关的同步能力。
    /// </summary>
    public sealed partial class TigaMemoryMappedFile : ITigaMemoryMappedFile
    {
        private const int ActiveIndexBits = 1;
        private const int SizeBits = 31;
        private const int ChecksumBits = 32;
        private const ulong ActiveIndexMask = (1UL << ActiveIndexBits) - 1UL;
        private const ulong SizeMask = (1UL << SizeBits) - 1UL;
        private const ulong ChecksumMask = (1UL << ChecksumBits) - 1UL;
        private const long StateRegionSize = 4096;
        private const long RegionAlignment = 64;

        private readonly MemoryMappedFile _memoryMappedFile;
        private readonly EventWaitHandle _fileWaitHandle;
        private readonly EventWaitHandle _disposeWaitHandle;
        private readonly TaskCompletionSource<bool> _watcherTaskCompletionSource = new();
        private readonly Task _fileWatcherTask;
        private readonly ILogger<TigaMemoryMappedFile>? _logger;
        private readonly long[] _dataOffsets;
        private readonly long _capacity;

        private MemoryMappedViewAccessor? _stateAccessor;
        private bool _statePointerAcquired;

        private unsafe byte* _stateBasePointer;
        private unsafe long* _instanceVersionPtr;
        private unsafe int* _readerCountsPtr;

        private bool _disposed;

        public event EventHandler? FileUpdated;

        public long MaxFileSize { get; }

        public string? Name { get; }

        #region Constructors

#if NET
        [SupportedOSPlatform("windows")]
#endif
        public TigaMemoryMappedFile(ITigaReadWriteLock readWriteLock, IOptions<TigaIpcOptions> options,
            ILogger<TigaMemoryMappedFile> logger)
            : this((options ?? throw new ArgumentNullException(nameof(options))).Value.Name, MappingType.Memory,
                options.Value.MaxFileSize, logger)
        {
            _ = readWriteLock ?? throw new ArgumentNullException(nameof(readWriteLock));
        }

#if NET
        [SupportedOSPlatform("windows")]
#endif
        public TigaMemoryMappedFile(string name, MappingType type, ILogger<TigaMemoryMappedFile>? logger = null)
            : this(name, type, TigaIpcOptions.DefaultMaxFileSize, logger)
        {
        }

#if NET
        [SupportedOSPlatform("windows")]
#endif
        public TigaMemoryMappedFile(string name, MappingType type, long maxFileSize,
            ILogger<TigaMemoryMappedFile>? logger = null)
            : this(CreateOrOpenMemoryMappedFile(name, maxFileSize, type), CreateEventWaitHandle(name), maxFileSize,
                logger)
        {
            Name = name;
        }

        private TigaMemoryMappedFile(MemoryMappedFile memoryMappedFile, EventWaitHandle fileWaitHandle, long maxFileSize,
            ILogger<TigaMemoryMappedFile>? logger)
        {
            _memoryMappedFile = memoryMappedFile ?? throw new ArgumentNullException(nameof(memoryMappedFile));
            _fileWaitHandle = fileWaitHandle ?? throw new ArgumentNullException(nameof(fileWaitHandle));
            _logger = logger;

            MaxFileSize = maxFileSize;

            _disposeWaitHandle = new EventWaitHandle(false, EventResetMode.ManualReset);
            _capacity = CalculateCapacity(maxFileSize);

            _dataOffsets = new[]
            {
                AlignOffset(StateRegionSize),
                AlignOffset(StateRegionSize) + maxFileSize,
            };

            InitializeStateAccessor();

            _fileWatcherTask = Task.Run(FileWatcher);
        }

        #endregion

        ~TigaMemoryMappedFile()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposeWaitHandle?.Set();
            _fileWatcherTask?.Wait(TigaIpcOptions.DefaultWaitTimeout);

            if (disposing)
            {
                ReleaseStateAccessor();
                _memoryMappedFile.Dispose();
                _fileWaitHandle.Dispose();
                _disposeWaitHandle.Dispose();
            }

            _disposed = true;
        }

        public int GetFileSize(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var spin = new SpinWait();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var before = ReadInstanceVersion();
                var after = ReadInstanceVersion();

                if (before == after)
                {
                    var size = ExtractSize(before);

                    if (_logger is not null)
                    {
                        LogReadFileSize(_logger, size);
                    }

                    return size;
                }

                spin.SpinOnce();
            }
        }

        public T Read<T>(Func<MemoryStream, T> readData, CancellationToken cancellationToken = default)
        {
            if (readData is null)
            {
                throw new ArgumentNullException(nameof(readData));
            }

            ThrowIfDisposed();

            using var readStream = MemoryStreamPool.Manager.GetStream(nameof(TigaMemoryMappedFile));
            ReadCurrentDataInto(readStream, cancellationToken);
            readStream.Seek(0, SeekOrigin.Begin);

            if (_logger is not null)
            {
                LogReadFile(_logger, readStream.Length);
            }

            return readData(readStream);
        }

        public void Write(MemoryStream data, CancellationToken cancellationToken = default)
        {
            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (data.Length > MaxFileSize)
            {
                throw new ArgumentOutOfRangeException(nameof(data), "Length greater than max file size");
            }

            ThrowIfDisposed();

            _watcherTaskCompletionSource.Task.GetAwaiter().GetResult();

            InternalWrite(data, cancellationToken);

            if (_logger is not null)
            {
                LogWroteFile(_logger, data.Length);
            }
        }

        public void ReadWrite(Action<MemoryStream, MemoryStream> updateFunc, CancellationToken cancellationToken = default)
        {
            if (updateFunc is null)
            {
                throw new ArgumentNullException(nameof(updateFunc));
            }

            ThrowIfDisposed();

            _watcherTaskCompletionSource.Task.GetAwaiter().GetResult();

            using var readStream = MemoryStreamPool.Manager.GetStream(nameof(TigaMemoryMappedFile));
            using var writeStream = MemoryStreamPool.Manager.GetStream(nameof(TigaMemoryMappedFile));

            ReadCurrentDataInto(readStream, cancellationToken);
            readStream.Seek(0, SeekOrigin.Begin);
            writeStream.SetLength(0);

            updateFunc(readStream, writeStream);
            writeStream.Seek(0, SeekOrigin.Begin);

            if (_logger is not null)
            {
                LogReadFile(_logger, readStream.Length);
            }

            InternalWrite(writeStream, cancellationToken);

            if (_logger is not null)
            {
                LogWroteFile(_logger, writeStream.Length);
            }
        }

        private void FileWatcher()
        {
            _watcherTaskCompletionSource.SetResult(true);

            var waitHandles = new[]
            {
                _disposeWaitHandle,
                _fileWaitHandle,
            };

            while (!_disposed)
            {
                var result = WaitHandle.WaitAny(waitHandles, TigaIpcOptions.DefaultWaitTimeout);

                if (result == 0 || _disposed)
                {
                    return;
                }

                if (result == 1)
                {
                    FileUpdated?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private void ReadCurrentDataInto(Stream target, CancellationToken cancellationToken)
        {
            target.SetLength(0);

            var (activeIndex, size, checksum) = EnterReadSection(cancellationToken);

            try
            {
                if (size == 0)
                {
                    return;
                }

                var buffer = ArrayPool<byte>.Shared.Rent(size);
                try
                {
                    using var accessor = _memoryMappedFile.CreateViewAccessor(_dataOffsets[activeIndex], size,
                        MemoryMappedFileAccess.Read);
                    accessor.ReadArray(0, buffer, 0, size);

                    var computed = ComputeChecksum(buffer, size);
                    if (computed != checksum)
                    {
                        throw new InvalidDataException(
                            $"Checksum mismatch while reading memory mapped file. Expected {checksum}, actual {computed}.");
                    }

                    target.Write(buffer, 0, size);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            finally
            {
                LeaveReadSection(activeIndex);
            }
        }

        private void InternalWrite(MemoryStream input, CancellationToken cancellationToken)
        {
            var length = (int)input.Length;
            var buffer = ArrayPool<byte>.Shared.Rent(length);

            try
            {
                input.Read(buffer, 0, length);
                input.Seek(0, SeekOrigin.Begin);

                var checksum = ComputeChecksum(buffer, length);

                var currentVersion = ReadInstanceVersion();
                var activeIndex = ExtractActiveIndex(currentVersion);
                var targetIndex = activeIndex ^ 1;

                WaitForSlot(targetIndex, cancellationToken);

                using (var accessor = _memoryMappedFile.CreateViewAccessor(_dataOffsets[targetIndex], length,
                           MemoryMappedFileAccess.Write))
                {
                    accessor.WriteArray(0, buffer, 0, length);
                }

                var newVersion = ComposeInstanceVersion(targetIndex, length, checksum);
                ExchangeInstanceVersion(newVersion);

                _fileWaitHandle.Set();
                _fileWaitHandle.Reset();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private (int activeIndex, int size, uint checksum) EnterReadSection(CancellationToken cancellationToken)
        {
            unsafe
            {
                var spin = new SpinWait();

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var versionBefore = ReadInstanceVersion();
                    var activeIndex = ExtractActiveIndex(versionBefore);

                    Interlocked.Increment(ref Unsafe.AsRef<int>(_readerCountsPtr + activeIndex));

                    var versionAfter = ReadInstanceVersion();
                    if (versionBefore == versionAfter)
                    {
                        var size = ExtractSize(versionBefore);
                        var checksum = ExtractChecksum(versionBefore);
                        return (activeIndex, size, checksum);
                    }

                    Interlocked.Decrement(ref Unsafe.AsRef<int>(_readerCountsPtr + activeIndex));
                    spin.SpinOnce();
                }
            }
        }

        private void LeaveReadSection(int activeIndex)
        {
            unsafe
            {
                Interlocked.Decrement(ref Unsafe.AsRef<int>(_readerCountsPtr + activeIndex));
            }
        }

        private void WaitForSlot(int slotIndex, CancellationToken cancellationToken)
        {
            unsafe
            {
                var spin = new SpinWait();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (Volatile.Read(ref Unsafe.AsRef<int>(_readerCountsPtr + slotIndex)) == 0)
                    {
                        return;
                    }

                    spin.SpinOnce();
                }
            }
        }

        private void InitializeStateAccessor()
        {
            _stateAccessor = _memoryMappedFile.CreateViewAccessor(0, StateRegionSize, MemoryMappedFileAccess.ReadWrite);

            unsafe
            {
                byte* pointer = null;
                _stateAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                _statePointerAcquired = true;
                _stateBasePointer = pointer + _stateAccessor.PointerOffset;
                _instanceVersionPtr = (long*)_stateBasePointer;
                _readerCountsPtr = (int*)(_stateBasePointer + sizeof(long));
            }
        }

        private void ReleaseStateAccessor()
        {
            if (_stateAccessor is null)
            {
                return;
            }

            if (_statePointerAcquired)
            {
                _stateAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _statePointerAcquired = false;
            }

            _stateAccessor.Dispose();
            _stateAccessor = null;
        }

        private void ThrowIfDisposed()
        {
#if NET7_0_OR_GREATER
            ObjectDisposedException.ThrowIf(_disposed, this);
#else
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TigaMemoryMappedFile));
            }
#endif
        }

        private static long AlignOffset(long value)
        {
            var remainder = value % RegionAlignment;
            return remainder == 0 ? value : value + (RegionAlignment - remainder);
        }

        private static long CalculateCapacity(long maxFileSize)
        {
            var dataRegion = AlignOffset(StateRegionSize) + (maxFileSize * 2);
            return AlignOffset(dataRegion);
        }

        private static uint ComputeChecksum(byte[] buffer, int length)
        {
            const uint fnvOffset = 2166136261;
            const uint fnvPrime = 16777619;

            uint hash = fnvOffset;
            for (var i = 0; i < length; i++)
            {
                hash ^= buffer[i];
                hash *= fnvPrime;
            }

            return hash;
        }

        private ulong ReadInstanceVersion()
        {
            unsafe
            {
                return unchecked((ulong)Volatile.Read(ref Unsafe.AsRef<long>(_instanceVersionPtr)));
            }
        }

        private void ExchangeInstanceVersion(ulong newVersion)
        {
            unsafe
            {
                Interlocked.Exchange(ref Unsafe.AsRef<long>(_instanceVersionPtr), (long)newVersion);
            }
        }

        private static int ExtractActiveIndex(ulong version)
        {
            return (int)(version & ActiveIndexMask);
        }

        private static int ExtractSize(ulong version)
        {
            return (int)((version >> ActiveIndexBits) & SizeMask);
        }

        private static uint ExtractChecksum(ulong version)
        {
            return (uint)((version >> (ActiveIndexBits + SizeBits)) & ChecksumMask);
        }

        private static ulong ComposeInstanceVersion(int activeIndex, int size, uint checksum)
        {
            return ((ulong)checksum << (ActiveIndexBits + SizeBits)) |
                   ((ulong)size << ActiveIndexBits) |
                   (ulong)activeIndex;
        }

#if NET
        [SupportedOSPlatform("windows")]
#endif
        public static MemoryMappedFile CreateOrOpenMemoryMappedFile(string name, long maxFileSize,
            MappingType mappingType = MappingType.Memory)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("File must be named", nameof(name));
            }

            if (maxFileSize <= 0)
            {
                throw new ArgumentException("Max file size can not be less than 1 byte", nameof(maxFileSize));
            }

            var capacity = CalculateCapacity(maxFileSize);
            MemoryMappedFile memoryMappedFile;

            if (mappingType == MappingType.File)
            {
                var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var cachePath = Path.Combine(localAppDataPath, "Innodealing", ".cache");
                if (!Directory.Exists(cachePath))
                {
                    Directory.CreateDirectory(cachePath);
                }

                var filePath = Path.Combine(cachePath, $"DmCommunication_{name}.tiga");
                using var fileStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.ReadWrite);
                memoryMappedFile = MemoryMappedFile.CreateFromFile(fileStream, null, capacity,
                    MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
            }
            else
            {
                var mapName = $"DmCommunicationMappedFile_{name}";
                memoryMappedFile = MemoryMappedFile.CreateOrOpen(mapName, capacity);
            }

            return memoryMappedFile;
        }

        public static EventWaitHandle CreateEventWaitHandle(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("EventWaitHandle must be named", nameof(name));
            }

            return new EventWaitHandle(false, EventResetMode.ManualReset, "TinyMemoryMappedFile_WaitHandle_" + name);
        }

        [LoggerMessage(0, LogLevel.Trace, "Read file size, memory mapped file was {file_size} bytes")]
        private static partial void LogReadFileSize(ILogger logger, long file_size);

        [LoggerMessage(1, LogLevel.Trace, "Read {file_size} bytes from memory mapped file")]
        private static partial void LogReadFile(ILogger logger, long file_size);

        [LoggerMessage(2, LogLevel.Trace, "Wrote {file_size} bytes to memory mapped file")]
        private static partial void LogWroteFile(ILogger logger, long file_size);
    }
}