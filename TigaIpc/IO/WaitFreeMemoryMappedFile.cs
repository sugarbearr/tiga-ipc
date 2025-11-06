using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace TigaIpc.IO;

/// <summary>
/// Memory mapped file implementation inspired by Cloudflare mmap-sync.
/// Provides wait-free reads with double buffering and reader counters.
/// </summary>
public sealed class WaitFreeMemoryMappedFile : ITigaMemoryMappedFile
{
    private const string WriterMutexPrefix = "WaitFreeMemoryMappedFile_Mutex_";

    private readonly MemoryMappedFile _memoryMappedFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly EventWaitHandle _fileWaitHandle;
    private readonly EventWaitHandle _disposeWaitHandle;
    private readonly Task _fileWatcherTask;
    private readonly TaskCompletionSource<bool> _watcherReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Mutex _writerMutex;
    private readonly ILogger<WaitFreeMemoryMappedFile>? _logger;
    private readonly TimeSpan _waitTimeout;
    private readonly long _regionSize;
    private readonly int _headerSize;

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WaitFreeMemoryMappedFile"/> class.
    /// </summary>
    /// <param name="name">Shared name.</param>
    /// <param name="type">Mapping type.</param>
    /// <param name="options">IPC options.</param>
    /// <param name="logger">Logger.</param>
    public WaitFreeMemoryMappedFile(
        string name,
        MappingType type,
        TigaIpcOptions options,
        ILogger<WaitFreeMemoryMappedFile>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("File must be named", nameof(name));
        }

        Name = name;
        _logger = logger;
        _waitTimeout = options?.WaitTimeout ?? TigaIpcOptions.DefaultWaitTimeout;
        MaxFileSize = options?.MaxFileSize ?? TigaIpcOptions.DefaultMaxFileSize;

        _regionSize = checked(MaxFileSize);
        _headerSize = Unsafe.SizeOf<SynchronizerHeader>();

        var capacity = ComputeCapacity(_regionSize, _headerSize);
        _memoryMappedFile = CreateOrOpen(name, type, capacity);
        _accessor = _memoryMappedFile.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);

        _fileWaitHandle = TigaMemoryMappedFile.CreateEventWaitHandle(name);
        _disposeWaitHandle = new EventWaitHandle(false, EventResetMode.ManualReset);
        _writerMutex = new Mutex(false, WriterMutexPrefix + name);

        InitializeHeader();

        _fileWatcherTask = Task.Run(FileWatcher);
    }

    /// <inheritdoc />
    public event EventHandler? FileUpdated;

    /// <inheritdoc />
    public long MaxFileSize { get; }

    /// <inheritdoc />
    public string? Name { get; }

    /// <inheritdoc />
    public int GetFileSize(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var snapshot = ReadHeaderSnapshot();
        var activeIndex = snapshot.ActiveIndex is 0 or 1 ? snapshot.ActiveIndex : 0;
        return activeIndex == 0 ? snapshot.Size0 : snapshot.Size1;
    }

    /// <inheritdoc />
    public T Read<T>(Func<MemoryStream, T> readData, CancellationToken cancellationToken = default)
    {
        if (readData is null)
        {
            throw new ArgumentNullException(nameof(readData));
        }
        ThrowIfDisposed();

        using var readStream = MemoryStreamPool.Manager.GetStream(nameof(WaitFreeMemoryMappedFile));

        var snapshot = EnterRead(cancellationToken);
        try
        {
            if (snapshot.Size == 0)
            {
                readStream.SetLength(0);
            }
            else
            {
                CopyRegionToStream(snapshot.ActiveIndex, snapshot.Size, readStream);
                ValidateChecksum(snapshot, readStream);
            }

            readStream.Seek(0, SeekOrigin.Begin);
            return readData(readStream);
        }
        finally
        {
            ExitRead(snapshot.ActiveIndex);
        }
    }

    /// <inheritdoc />
    public void Write(MemoryStream data, CancellationToken cancellationToken = default)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        ReadWrite((_, writer) =>
        {
            data.Seek(0, SeekOrigin.Begin);
            data.CopyTo(writer);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public void ReadWrite(Action<MemoryStream, MemoryStream> updateFunc, CancellationToken cancellationToken = default)
    {
        if (updateFunc is null)
        {
            throw new ArgumentNullException(nameof(updateFunc));
        }
        ThrowIfDisposed();

        _watcherReady.Task.GetAwaiter().GetResult();

        if (!_writerMutex.WaitOne(_waitTimeout))
        {
            throw new TimeoutException("Failed to acquire writer mutex for wait-free memory mapped file");
        }

        try
        {
            using var readStream = MemoryStreamPool.Manager.GetStream(nameof(WaitFreeMemoryMappedFile));
            using var writeStream = MemoryStreamPool.Manager.GetStream(nameof(WaitFreeMemoryMappedFile));

            var header = ReadHeaderSnapshot();
            var activeIndex = header.ActiveIndex is 0 or 1 ? header.ActiveIndex : 0;
            var inactiveIndex = activeIndex == 0 ? 1 : 0;

            // Copy current active region for update delegate
            if ((activeIndex == 0 ? header.Size0 : header.Size1) > 0)
            {
                var activeSize = activeIndex == 0 ? header.Size0 : header.Size1;
                CopyRegionToStream(activeIndex, activeSize, readStream);
                readStream.Seek(0, SeekOrigin.Begin);
            }

            updateFunc(readStream, writeStream);
            writeStream.Seek(0, SeekOrigin.Begin);

            if (writeStream.Length > _regionSize)
            {
                throw new ArgumentOutOfRangeException(nameof(updateFunc), "Serialized size exceeds configured MaxFileSize");
            }

            var size = (int)writeStream.Length;
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                if (size > 0)
                {
                    writeStream.Read(buffer, 0, size);
                }

                WaitForReaders(inactiveIndex, cancellationToken);
                WriteRegion(inactiveIndex, buffer, size);

                var checksum = size == 0 ? 0 : ComputeChecksum(buffer.AsSpan(0, size));
                UpdateHeader(inactiveIndex, size, checksum);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            _writerMutex.ReleaseMutex();
        }
    }

    /// <inheritdoc />
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

        _disposed = true;
        _disposeWaitHandle.Set();
        _fileWatcherTask?.Wait(_waitTimeout);

        if (disposing)
        {
            _accessor.Dispose();
            _memoryMappedFile.Dispose();
            _fileWaitHandle.Dispose();
            _disposeWaitHandle.Dispose();
            _writerMutex.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WaitFreeMemoryMappedFile));
        }
    }

    private static long ComputeCapacity(long regionSize, int headerSize)
    {
        return headerSize + (regionSize * 2);
    }

    private static MemoryMappedFile CreateOrOpen(string name, MappingType type, long capacity)
    {
        if (type == MappingType.File)
        {
            var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var cachePath = Path.Combine(localAppDataPath, "Innodealing", ".cache");
            if (!Directory.Exists(cachePath))
            {
                Directory.CreateDirectory(cachePath);
            }

            var filePath = Path.Combine(cachePath, $"DmCommunication_{name}_wf.tiga");
            using var fileStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (fileStream.Length != capacity)
            {
                fileStream.SetLength(capacity);
            }

            return MemoryMappedFile.CreateFromFile(
                fileStream,
                null,
                capacity,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                false);
        }

        return MemoryMappedFile.CreateOrOpen($"DmCommunicationMappedFile_{name}_wf", capacity);
    }

    private unsafe void InitializeHeader()
    {
        byte* pointer = null;
        var handle = _accessor.SafeMemoryMappedViewHandle;
        try
        {
            handle.AcquirePointer(ref pointer);
            var header = (SynchronizerHeader*)pointer;

            if (header->Magic == SynchronizerHeader.ExpectedMagic)
            {
                return;
            }

            *header = new SynchronizerHeader
            {
                Magic = SynchronizerHeader.ExpectedMagic,
                Version = 0,
                ActiveIndex = 0,
                Size0 = 0,
                Size1 = 0,
                ReaderCount0 = 0,
                ReaderCount1 = 0,
                Checksum0 = 0,
                Checksum1 = 0,
            };
        }
        finally
        {
            if (pointer != null)
            {
                handle.ReleasePointer();
            }
        }
    }

    private HeaderSnapshot ReadHeaderSnapshot()
    {
        unsafe
        {
            byte* pointer = null;
            var handle = _accessor.SafeMemoryMappedViewHandle;
            try
            {
                handle.AcquirePointer(ref pointer);
                var header = (SynchronizerHeader*)pointer;
                return new HeaderSnapshot(*header);
            }
            finally
            {
                if (pointer != null)
                {
                    handle.ReleasePointer();
                }
            }
        }
    }

    private HeaderSnapshot EnterRead(CancellationToken cancellationToken)
    {
        unsafe
        {
            byte* pointer = null;
            var handle = _accessor.SafeMemoryMappedViewHandle;

            try
            {
                handle.AcquirePointer(ref pointer);
                var header = (SynchronizerHeader*)pointer;

                var spin = new SpinWait();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var headerValue = *header;
                    var snapshot = new HeaderSnapshot(headerValue);
                    var activeIndex = snapshot.ActiveIndex is 0 or 1 ? snapshot.ActiveIndex : 0;
                    ref var readerCount = ref activeIndex == 0 ? ref header->ReaderCount0 : ref header->ReaderCount1;
                    Interlocked.Increment(ref readerCount);

                    var confirmHeader = *header;
                    var confirm = new HeaderSnapshot(confirmHeader);
                    if (confirm.ActiveIndex == activeIndex && confirm.Version == snapshot.Version)
                    {
                        return new HeaderSnapshot(confirmHeader, activeIndex);
                    }

                    Interlocked.Decrement(ref readerCount);
                    spin.SpinOnce();
                }
            }
            finally
            {
                if (pointer != null)
                {
                    handle.ReleasePointer();
                }
            }
        }
    }

    private void ExitRead(int bufferIndex)
    {
        unsafe
        {
            byte* pointer = null;
            var handle = _accessor.SafeMemoryMappedViewHandle;
            try
            {
                handle.AcquirePointer(ref pointer);
                var header = (SynchronizerHeader*)pointer;
                ref var readerCount = ref bufferIndex == 0 ? ref header->ReaderCount0 : ref header->ReaderCount1;
                Interlocked.Decrement(ref readerCount);
            }
            finally
            {
                if (pointer != null)
                {
                    handle.ReleasePointer();
                }
            }
        }
    }

    private void WaitForReaders(int bufferIndex, CancellationToken cancellationToken)
    {
        unsafe
        {
            byte* pointer = null;
            var handle = _accessor.SafeMemoryMappedViewHandle;
            try
            {
                handle.AcquirePointer(ref pointer);
                var header = (SynchronizerHeader*)pointer;
                ref var readerCount = ref bufferIndex == 0 ? ref header->ReaderCount0 : ref header->ReaderCount1;

                var spin = new SpinWait();
                var start = DateTime.UtcNow;
                while (Volatile.Read(ref readerCount) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (DateTime.UtcNow - start > _waitTimeout)
                    {
                        throw new TimeoutException("Timed out waiting for readers to drain in wait-free memory mapped file");
                    }

                    spin.SpinOnce();
                }
            }
            finally
            {
                if (pointer != null)
                {
                    handle.ReleasePointer();
                }
            }
        }
    }

    private void CopyRegionToStream(int bufferIndex, int size, MemoryStream destination)
    {
        destination.SetLength(0);
        if (size <= 0)
        {
            return;
        }

        var offset = _headerSize + (_regionSize * bufferIndex);
        var temp = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            _accessor.ReadArray(offset, temp, 0, size);
            destination.Write(temp, 0, size);
            destination.Seek(0, SeekOrigin.Begin);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    private void WriteRegion(int bufferIndex, byte[] data, int length)
    {
        var offset = _headerSize + (_regionSize * bufferIndex);

        if (length > 0)
        {
            _accessor.WriteArray(offset, data, 0, length);
        }
    }

    private void UpdateHeader(int newActiveIndex, int size, uint checksum)
    {
        unsafe
        {
            byte* pointer = null;
            var handle = _accessor.SafeMemoryMappedViewHandle;
            try
            {
                handle.AcquirePointer(ref pointer);
                var header = (SynchronizerHeader*)pointer;

                if (newActiveIndex == 0)
                {
                    header->Size0 = size;
                    header->Checksum0 = checksum;
                }
                else
                {
                    header->Size1 = size;
                    header->Checksum1 = checksum;
                }

                Interlocked.Increment(ref header->Version);
                Volatile.Write(ref header->ActiveIndex, newActiveIndex);
            }
            finally
            {
                if (pointer != null)
                {
                    handle.ReleasePointer();
                }
            }
        }

        SignalUpdated();
    }

    private void SignalUpdated()
    {
        _fileWaitHandle.Set();
        _fileWaitHandle.Reset();
    }

    private void ValidateChecksum(HeaderSnapshot snapshot, MemoryStream stream)
    {
        if (snapshot.Size == 0 || snapshot.Checksum == 0)
        {
            return;
        }

        if (!stream.TryGetBuffer(out var segment))
        {
            segment = new ArraySegment<byte>(stream.ToArray());
        }

        var checksum = ComputeChecksum(segment.AsSpan(0, snapshot.Size));
        if (checksum != snapshot.Checksum)
        {
            _logger?.LogWarning(
                "Checksum mismatch detected in wait-free memory mapped file. Expected {Expected} but computed {Actual}",
                snapshot.Checksum,
                checksum);
        }
    }

    private static uint ComputeChecksum(ReadOnlySpan<byte> data)
    {
        const uint fnvOffset = 2166136261;
        const uint fnvPrime = 16777619;

        var hash = fnvOffset;
        foreach (var b in data)
        {
            hash ^= b;
            hash *= fnvPrime;
        }

        return hash;
    }

    private async Task FileWatcher()
    {
        _watcherReady.TrySetResult(true);

        var waitHandles = new[]
        {
            _disposeWaitHandle,
            _fileWaitHandle,
        };

        while (!_disposed)
        {
            var result = WaitHandle.WaitAny(waitHandles, _waitTimeout);

            if (result == 0 || _disposed)
            {
                return;
            }

            if (result == 1)
            {
                FileUpdated?.Invoke(this, EventArgs.Empty);
            }

            await Task.Yield();
        }
    }

    private readonly struct HeaderSnapshot
    {
        public HeaderSnapshot(SynchronizerHeader header)
            : this(header, header.ActiveIndex)
        {
        }

        public HeaderSnapshot(SynchronizerHeader header, int activeIndex)
        {
            Magic = header.Magic;
            Version = header.Version;
            ActiveIndex = activeIndex;
            Size0 = header.Size0;
            Size1 = header.Size1;
            ReaderCount0 = header.ReaderCount0;
            ReaderCount1 = header.ReaderCount1;
            Checksum0 = header.Checksum0;
            Checksum1 = header.Checksum1;
            Size = activeIndex == 0 ? header.Size0 : header.Size1;
            Checksum = activeIndex == 0 ? header.Checksum0 : header.Checksum1;
        }

        public int Magic { get; }

        public long Version { get; }

        public int ActiveIndex { get; }

        public int Size0 { get; }

        public int Size1 { get; }

        public int ReaderCount0 { get; }

        public int ReaderCount1 { get; }

        public uint Checksum0 { get; }

        public uint Checksum1 { get; }

        public int Size { get; }

        public uint Checksum { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SynchronizerHeader
    {
        public const int ExpectedMagic = 0x54494741; // "TIGA"

        public int Magic;
        public long Version;
        public int ActiveIndex;
        public int Size0;
        public int Size1;
        public int ReaderCount0;
        public int ReaderCount1;
        public uint Checksum0;
        public uint Checksum1;
    }
}

