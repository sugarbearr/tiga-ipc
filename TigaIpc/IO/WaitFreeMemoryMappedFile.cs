using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace TigaIpc.IO;

/// <summary>
/// Memory mapped file implementation inspired by Cloudflare mmap-sync.
/// Provides wait-free reads with double buffering and reader counters.
/// </summary>
public sealed class WaitFreeMemoryMappedFile : ITigaMemoryMappedFile, ISynchronizationMetricsProvider
{
    private const string FilePrefix = "DmCommunication_";
    private const string MemoryPrefix = "DmCommunicationMappedFile_";
    private const string StateSuffix = "_state";
    private const string DataSuffix0 = "_data_0";
    private const string DataSuffix1 = "_data_1";
    private const string EventPrefix = "TinyMemoryMappedFile_WaitHandle_";

    private const int DataSizeBits = 39;
    private const int DataChecksumBits = 24;
    private const long MaxDataSize = (1L << DataSizeBits) - 1;
    private const long ChecksumMask = (1L << DataChecksumBits) - 1;

    private readonly MemoryMappedFile _stateMap;
    private readonly MemoryMappedViewAccessor _stateAccessor;
    private readonly MemoryMappedFile[] _dataMaps = new MemoryMappedFile[2];
    private readonly MemoryMappedViewAccessor[] _dataAccessors = new MemoryMappedViewAccessor[2];
    private readonly FileStream? _stateFileStream;
    private readonly FileStream?[] _dataFileStreams = new FileStream?[2];
    private readonly EventWaitHandle _fileWaitHandle;
    private readonly EventWaitHandle _disposeWaitHandle;
    private readonly Task _fileWatcherTask;
    private readonly TaskCompletionSource<bool> _watcherReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger<WaitFreeMemoryMappedFile>? _logger;
    private readonly TimeSpan _waitTimeout;
    private readonly TimeSpan _readerGraceTimeout;
    private readonly TimeSpan _writerSleepDuration;
    private readonly ChecksumProvider _checksumProvider;
    private readonly bool _useDefaultChecksum;
    private readonly bool _verifyChecksumOnRead;
    private readonly long _regionSize;
    private readonly int _stateSize;
    private readonly MappingType _mappingType;
    private readonly bool _useSingleWriterLock;

    private bool _disposed;
    private long _lockTimeouts;
    private long _lockAbandoned;
    private long _readerResets;
    private long _lastReadVersion;

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

        options ??= new TigaIpcOptions();

        Name = name;
        _logger = logger;
        _waitTimeout = options.WaitTimeout;
        _readerGraceTimeout = options.ReaderGraceTimeout;
        if (_readerGraceTimeout <= TimeSpan.Zero)
        {
            _readerGraceTimeout = _waitTimeout;
        }

        _writerSleepDuration = options.WriterSleepDuration;
        if (_writerSleepDuration < TimeSpan.Zero)
        {
            _writerSleepDuration = TimeSpan.Zero;
        }

        _checksumProvider = options.ChecksumProvider ?? WyHash.Hash;
        _useDefaultChecksum = options.ChecksumProvider == null;
        _verifyChecksumOnRead = options.VerifyChecksumOnRead;

        MaxFileSize = options.MaxFileSize;
        _regionSize = checked(MaxFileSize);
        _stateSize = Unsafe.SizeOf<StateHeader>();
        _mappingType = type;
        _useSingleWriterLock = options.UseSingleWriterLock;

        if (_useSingleWriterLock && _mappingType != MappingType.File)
        {
            throw new PlatformNotSupportedException("Single-writer lock is supported only for file-backed mappings.");
        }

        var stateInitRequired = false;
        if (_mappingType == MappingType.File)
        {
            var prefix = GetFilePrefix(name, options);
            var statePath = prefix + StateSuffix;
            var dataPath0 = prefix + DataSuffix0;
            var dataPath1 = prefix + DataSuffix1;

            _stateMap = CreateFileMapping(statePath, _stateSize, options, out _stateFileStream, out stateInitRequired);
            _dataMaps[0] = CreateFileMapping(dataPath0, _regionSize, options, out _dataFileStreams[0], out _);
            _dataMaps[1] = CreateFileMapping(dataPath1, _regionSize, options, out _dataFileStreams[1], out _);

            if (_useSingleWriterLock && _stateFileStream != null)
            {
                if (!SingleWriterFileLock.TryAcquire(_stateFileStream))
                {
                    Interlocked.Increment(ref _lockTimeouts);
                    _stateMap.Dispose();
                    _dataMaps[0].Dispose();
                    _dataMaps[1].Dispose();
                    _stateFileStream.Dispose();
                    _dataFileStreams[0]?.Dispose();
                    _dataFileStreams[1]?.Dispose();
                    throw new InvalidOperationException("Single-writer lock is already held by another process.");
                }
            }
        }
        else
        {
            var stateName = MemoryPrefix + name + StateSuffix;
            var dataName0 = MemoryPrefix + name + DataSuffix0;
            var dataName1 = MemoryPrefix + name + DataSuffix1;

            _stateMap = CreateNamedMapping(stateName, _stateSize, options);
            _dataMaps[0] = CreateNamedMapping(dataName0, _regionSize, options);
            _dataMaps[1] = CreateNamedMapping(dataName1, _regionSize, options);
        }

        _stateAccessor = _stateMap.CreateViewAccessor(0, _stateSize, MemoryMappedFileAccess.ReadWrite);
        _dataAccessors[0] = _dataMaps[0].CreateViewAccessor(0, _regionSize, MemoryMappedFileAccess.ReadWrite);
        _dataAccessors[1] = _dataMaps[1].CreateViewAccessor(0, _regionSize, MemoryMappedFileAccess.ReadWrite);

        InitializeState(stateInitRequired);

        _fileWaitHandle = CreateEventWaitHandle(name, options);
        _disposeWaitHandle = new EventWaitHandle(false, EventResetMode.ManualReset);
        _fileWatcherTask = Task.Run(FileWatcher);
    }

    /// <inheritdoc />
    public event EventHandler? FileUpdated;

    /// <inheritdoc />
    public long MaxFileSize { get; }

    /// <inheritdoc />
    public string? Name { get; }

    public SynchronizationMetrics GetSynchronizationMetrics()
    {
        return new SynchronizationMetrics(
            Interlocked.Read(ref _lockTimeouts),
            Interlocked.Read(ref _lockAbandoned),
            Interlocked.Read(ref _readerResets));
    }

    /// <inheritdoc />
    public int GetFileSize(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var version = ReadVersion();
        if (!version.IsInitialized)
        {
            return 0;
        }

        if (version.Size > int.MaxValue)
        {
            throw new NotSupportedException("File size exceeds int.MaxValue for stream-based access.");
        }

        return (int)version.Size;
    }

    /// <summary>
    /// Returns the current wait-free instance version snapshot.
    /// </summary>
    public WaitFreeVersion GetVersion()
    {
        ThrowIfDisposed();
        return ReadVersion();
    }

    /// <summary>
    /// Reads the current data as a zero-copy lease. The lease must be disposed to release reader count.
    /// </summary>
    public ReadResult ReadLease(bool verifyChecksum = true, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var snapshot = EnterRead(cancellationToken);
        if (!snapshot.Version.IsInitialized)
        {
            return ReadResult.CreateEmpty(this, snapshot.Version, snapshot.Switched);
        }

        if (snapshot.Size == 0)
        {
            ExitRead(snapshot.ActiveIndex);
            return ReadResult.CreateEmpty(this, snapshot.Version, snapshot.Switched);
        }

        if (snapshot.Size > _regionSize || snapshot.Size < 0)
        {
            ExitRead(snapshot.ActiveIndex);
            throw new InvalidDataException("Invalid data size in wait-free version header.");
        }

        var accessor = _dataAccessors[snapshot.ActiveIndex];
        var handle = accessor.SafeMemoryMappedViewHandle;
        unsafe
        {
            byte* pointer = null;
            handle.AcquirePointer(ref pointer);
            if (verifyChecksum && snapshot.Version.Checksum != 0)
            {
                var truncated = ComputeChecksum(pointer, snapshot.Size);
                if (truncated != snapshot.Version.Checksum)
                {
                    _logger?.LogWarning(
                        "Checksum mismatch detected in wait-free memory mapped file. Expected {Expected} but computed {Actual}",
                        snapshot.Version.Checksum,
                        truncated);
                }
            }

            return new ReadResult(this, handle, pointer, snapshot);
        }
    }

    /// <inheritdoc />
    public T Read<T>(Func<MemoryStream, T> readData, CancellationToken cancellationToken = default)
    {
        if (readData is null)
        {
            throw new ArgumentNullException(nameof(readData));
        }

        using var readStream = MemoryStreamPool.Manager.GetStream(nameof(WaitFreeMemoryMappedFile));
        using var lease = ReadLease(_verifyChecksumOnRead, cancellationToken);

        if (lease.Size > 0)
        {
            if (lease.Size > int.MaxValue)
            {
                throw new NotSupportedException("File size exceeds int.MaxValue for stream-based access.");
            }

            lease.Stream.CopyTo(readStream);
        }

        readStream.Seek(0, SeekOrigin.Begin);
        return readData(readStream);
    }

    /// <inheritdoc />
    public void Write(MemoryStream data, CancellationToken cancellationToken = default)
    {
        Write(data, _readerGraceTimeout, cancellationToken);
    }

    /// <summary>
    /// Writes a typed payload using a custom serializer.
    /// </summary>
    public void Write<T>(T value, WaitFreeSerializer<T> serializer, CancellationToken cancellationToken = default)
    {
        Write(value, serializer, null, _readerGraceTimeout, cancellationToken);
    }

    /// <summary>
    /// Writes a typed payload using a custom serializer and validator.
    /// </summary>
    public void Write<T>(
        T value,
        WaitFreeSerializer<T> serializer,
        WaitFreeValidator? validator,
        CancellationToken cancellationToken = default)
    {
        Write(value, serializer, validator, _readerGraceTimeout, cancellationToken);
    }

    /// <summary>
    /// Writes a typed payload using a custom serializer and grace duration.
    /// </summary>
    public void Write<T>(
        T value,
        WaitFreeSerializer<T> serializer,
        TimeSpan graceDuration,
        CancellationToken cancellationToken = default)
    {
        Write(value, serializer, null, graceDuration, cancellationToken);
    }

    /// <summary>
    /// Writes a typed payload using a custom serializer, validator, and grace duration.
    /// </summary>
    public void Write<T>(
        T value,
        WaitFreeSerializer<T> serializer,
        WaitFreeValidator? validator,
        TimeSpan graceDuration,
        CancellationToken cancellationToken = default)
    {
        if (serializer is null)
        {
            throw new ArgumentNullException(nameof(serializer));
        }

        var payload = serializer(value);
        if (validator is not null && !validator(payload.Span))
        {
            throw new InvalidDataException("Serialized payload failed validation.");
        }

        WriteRaw(payload.Span, graceDuration, cancellationToken);
    }

    /// <summary>
    /// Writes raw data bytes into the next available data buffer.
    /// </summary>
    public void WriteRaw(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        WriteRaw(data, _readerGraceTimeout, cancellationToken);
    }

    /// <summary>
    /// Writes raw data bytes into the next available data buffer using a custom grace duration.
    /// </summary>
    public void WriteRaw(ReadOnlySpan<byte> data, TimeSpan graceDuration, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _watcherReady.Task.GetAwaiter().GetResult();

        if (data.Length > _regionSize)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "Serialized size exceeds configured MaxFileSize");
        }

        var newIndex = AcquireNextIndex(graceDuration, cancellationToken, out var reset);
        if (reset)
        {
            _logger?.LogWarning(
                "Reader count reset after waiting {GraceTimeout} for buffer {BufferIndex}",
                graceDuration,
                newIndex);
        }

        WriteRegion(newIndex, data);

        var checksum = ComputeChecksum(data);
        var version = WaitFreeVersion.Create(newIndex, data.Length, checksum);
        UpdateVersion(version);
    }

    /// <summary>
    /// Reads raw data bytes as a zero-copy lease.
    /// </summary>
    public ReadResult ReadRaw(bool verifyChecksum = true, CancellationToken cancellationToken = default)
    {
        return ReadLease(verifyChecksum, cancellationToken);
    }

    /// <summary>
    /// Reads a typed payload using a custom deserializer.
    /// </summary>
    public T Read<T>(
        WaitFreeDeserializer<T> deserializer,
        bool verifyChecksum = true,
        CancellationToken cancellationToken = default)
    {
        return Read(deserializer, null, verifyChecksum, cancellationToken);
    }

    /// <summary>
    /// Reads a typed payload using a custom deserializer and validator.
    /// </summary>
    public T Read<T>(
        WaitFreeDeserializer<T> deserializer,
        WaitFreeValidator? validator,
        bool verifyChecksum = true,
        CancellationToken cancellationToken = default)
    {
        if (deserializer is null)
        {
            throw new ArgumentNullException(nameof(deserializer));
        }

        using var lease = ReadLease(verifyChecksum, cancellationToken);
        if (lease.Size == 0)
        {
            return deserializer(ReadOnlySpan<byte>.Empty);
        }

        var span = lease.AsSpan();
        if (validator is not null && !validator(span))
        {
            throw new InvalidDataException("Payload failed validation.");
        }

        return deserializer(span);
    }

    /// <summary>
    /// Writes data using a custom grace duration.
    /// </summary>
    public void Write(MemoryStream data, TimeSpan graceDuration, CancellationToken cancellationToken = default)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        ReadWrite((_, writer) =>
        {
            data.Seek(0, SeekOrigin.Begin);
            data.CopyTo(writer);
        }, graceDuration, cancellationToken);
    }

    /// <inheritdoc />
    public void ReadWrite(Action<MemoryStream, MemoryStream> updateFunc, CancellationToken cancellationToken = default)
    {
        ReadWrite(updateFunc, _readerGraceTimeout, cancellationToken);
    }

    /// <summary>
    /// Reads and then replaces the content of the memory mapped file using a custom grace duration.
    /// </summary>
    public void ReadWrite(
        Action<MemoryStream, MemoryStream> updateFunc,
        TimeSpan graceDuration,
        CancellationToken cancellationToken = default)
    {
        if (updateFunc is null)
        {
            throw new ArgumentNullException(nameof(updateFunc));
        }

        ThrowIfDisposed();
        _watcherReady.Task.GetAwaiter().GetResult();

        using var readStream = MemoryStreamPool.Manager.GetStream(nameof(WaitFreeMemoryMappedFile));
        using var writeStream = MemoryStreamPool.Manager.GetStream(nameof(WaitFreeMemoryMappedFile));

        using (var lease = ReadLease(false, cancellationToken))
        {
            if (lease.Size > 0)
            {
                if (lease.Size > int.MaxValue)
                {
                    throw new NotSupportedException("File size exceeds int.MaxValue for stream-based access.");
                }

                lease.Stream.CopyTo(readStream);
                readStream.Seek(0, SeekOrigin.Begin);
            }
        }

        updateFunc(readStream, writeStream);
        writeStream.Seek(0, SeekOrigin.Begin);

        if (writeStream.Length > _regionSize)
        {
            throw new ArgumentOutOfRangeException(nameof(updateFunc), "Serialized size exceeds configured MaxFileSize");
        }

        var size = writeStream.Length;
        if (size > int.MaxValue)
        {
            throw new NotSupportedException("File size exceeds int.MaxValue for stream-based access.");
        }

        var length = (int)size;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            if (length > 0)
            {
                writeStream.Read(buffer, 0, length);
            }

            var newIndex = AcquireNextIndex(graceDuration, cancellationToken, out var reset);
            if (reset)
            {
                _logger?.LogWarning(
                    "Reader count reset after waiting {GraceTimeout} for buffer {BufferIndex}",
                    graceDuration,
                    newIndex);
            }

            WriteRegion(newIndex, buffer, length);

            var checksum = ComputeChecksum(buffer.AsSpan(0, length));
            var version = WaitFreeVersion.Create(newIndex, size, checksum);
            UpdateVersion(version);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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
            _stateAccessor.Dispose();
            _stateMap.Dispose();
            _dataAccessors[0].Dispose();
            _dataAccessors[1].Dispose();
            _dataMaps[0].Dispose();
            _dataMaps[1].Dispose();
            _fileWaitHandle.Dispose();
            _disposeWaitHandle.Dispose();
            _stateFileStream?.Dispose();
            _dataFileStreams[0]?.Dispose();
            _dataFileStreams[1]?.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WaitFreeMemoryMappedFile));
        }
    }

    private static string GetFilePrefix(string name, TigaIpcOptions options)
    {
        var baseDirectory = options.FileMappingDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            if ((RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) &&
                Directory.Exists("/dev/shm"))
            {
                baseDirectory = "/dev/shm";
            }
            else
            {
                var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                baseDirectory = Path.Combine(localAppDataPath, "Innodealing", ".cache");
            }
        }

        if (!Directory.Exists(baseDirectory))
        {
            Directory.CreateDirectory(baseDirectory);
        }

        return Path.Combine(baseDirectory, FilePrefix + name);
    }

    private static MemoryMappedFile CreateNamedMapping(
        string mapName,
        long capacity,
        TigaIpcOptions options)
    {
        if (options?.NamedMemoryMappedFileFactory != null)
        {
            return options.NamedMemoryMappedFileFactory(mapName, capacity);
        }

        return MemoryMappedFile.CreateOrOpen(mapName, capacity);
    }

    private static MemoryMappedFile CreateFileMapping(
        string filePath,
        long capacity,
        TigaIpcOptions options,
        out FileStream? fileStream,
        out bool requiresInitialization)
    {
        fileStream = options?.FileStreamFactory != null
            ? options.FileStreamFactory(filePath, capacity)
            : new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

        requiresInitialization = false;
        if (fileStream.Length != capacity)
        {
            fileStream.SetLength(capacity);
            requiresInitialization = true;
        }

        return MemoryMappedFile.CreateFromFile(
            fileStream,
            null,
            capacity,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            false);
    }

    private static EventWaitHandle CreateEventWaitHandle(string name, TigaIpcOptions options)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("EventWaitHandle must be named", nameof(name));
        }

        var eventName = EventPrefix + name;
        if (options?.EventWaitHandleFactory != null)
        {
            return options.EventWaitHandleFactory(eventName);
        }

        return new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
    }

    private void InitializeState(bool forceReset)
    {
        unsafe
        {
            byte* pointer = null;
            var handle = _stateAccessor.SafeMemoryMappedViewHandle;
            try
            {
                handle.AcquirePointer(ref pointer);
                var state = (StateHeader*)pointer;
                if (!forceReset && Volatile.Read(ref state->Version) != 0)
                {
                    return;
                }

                state->Version = 0;
                state->ReaderCount0 = 0;
                state->ReaderCount1 = 0;
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

    private WaitFreeVersion ReadVersion()
    {
        unsafe
        {
            byte* pointer = null;
            var handle = _stateAccessor.SafeMemoryMappedViewHandle;
            try
            {
                handle.AcquirePointer(ref pointer);
                var state = (StateHeader*)pointer;
                var value = Volatile.Read(ref state->Version);
                return new WaitFreeVersion(value);
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

    private ReadSnapshot EnterRead(CancellationToken cancellationToken)
    {
        unsafe
        {
            byte* pointer = null;
            var handle = _stateAccessor.SafeMemoryMappedViewHandle;
            try
            {
                handle.AcquirePointer(ref pointer);
                var state = (StateHeader*)pointer;

                var spin = new SpinWait();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var versionValue = Volatile.Read(ref state->Version);
                    var version = new WaitFreeVersion(versionValue);
                    if (!version.IsInitialized)
                    {
                        return ReadSnapshot.Empty;
                    }

                    var activeIndex = version.Index;
                    ref var readerCount =
                        ref activeIndex == 0 ? ref state->ReaderCount0 : ref state->ReaderCount1;
                    Interlocked.Increment(ref readerCount);

                    var confirmValue = Volatile.Read(ref state->Version);
                    if (confirmValue == versionValue)
                    {
                        var switched = Interlocked.Exchange(ref _lastReadVersion, versionValue) != versionValue;
                        return new ReadSnapshot(version, activeIndex, switched);
                    }

                    DecrementReaderCount(ref readerCount);
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
        if (bufferIndex < 0)
        {
            return;
        }

        unsafe
        {
            byte* pointer = null;
            var handle = _stateAccessor.SafeMemoryMappedViewHandle;
            try
            {
                handle.AcquirePointer(ref pointer);
                var state = (StateHeader*)pointer;
                ref var readerCount =
                    ref bufferIndex == 0 ? ref state->ReaderCount0 : ref state->ReaderCount1;
                DecrementReaderCount(ref readerCount);
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

    private int AcquireNextIndex(TimeSpan graceDuration, CancellationToken cancellationToken, out bool reset)
    {
        reset = false;
        unsafe
        {
            byte* pointer = null;
            var handle = _stateAccessor.SafeMemoryMappedViewHandle;
            try
            {
                handle.AcquirePointer(ref pointer);
                var state = (StateHeader*)pointer;

                var versionValue = Volatile.Read(ref state->Version);
                var version = new WaitFreeVersion(versionValue);
                var nextIndex = version.IsInitialized ? (version.Index + 1) % 2 : 0;

                ref var readerCount =
                    ref nextIndex == 0 ? ref state->ReaderCount0 : ref state->ReaderCount1;

                var graceTicks = graceDuration <= TimeSpan.Zero
                    ? 0
                    : (long)(graceDuration.TotalSeconds * Stopwatch.Frequency);
                var start = Stopwatch.GetTimestamp();
                var spin = new SpinWait();

                while (Volatile.Read(ref readerCount) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (graceTicks == 0 || Stopwatch.GetTimestamp() - start >= graceTicks)
                    {
                        Interlocked.Exchange(ref readerCount, 0);
                        Interlocked.Increment(ref _readerResets);
                        reset = true;
                        break;
                    }

                    if (_writerSleepDuration > TimeSpan.Zero)
                    {
                        Thread.Sleep(_writerSleepDuration);
                    }
                    else
                    {
                        spin.SpinOnce();
                    }
                }

                return nextIndex;
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

    private static void DecrementReaderCount(ref int readerCount)
    {
        var newValue = Interlocked.Decrement(ref readerCount);
        if (newValue < 0)
        {
            Interlocked.Exchange(ref readerCount, 0);
        }
    }

    private void WriteRegion(int bufferIndex, byte[] data, int length)
    {
        var accessor = _dataAccessors[bufferIndex];

        if (length > 0)
        {
            accessor.WriteArray(0, data, 0, length);
            accessor.Flush();
        }
    }

    private void WriteRegion(int bufferIndex, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        var temp = ArrayPool<byte>.Shared.Rent(data.Length);
        try
        {
            data.CopyTo(temp);
            WriteRegion(bufferIndex, temp, data.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    private void UpdateVersion(WaitFreeVersion version)
    {
        unsafe
        {
            byte* pointer = null;
            var handle = _stateAccessor.SafeMemoryMappedViewHandle;
            try
            {
                handle.AcquirePointer(ref pointer);
                var state = (StateHeader*)pointer;
                Interlocked.Exchange(ref state->Version, version.RawValue);
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

    private uint ComputeChecksum(ReadOnlySpan<byte> data)
    {
        var hash = _checksumProvider(data);
        return (uint)(hash & ChecksumMask);
    }

    private unsafe uint ComputeChecksum(byte* data, long length)
    {
        if (_useDefaultChecksum)
        {
            var hash = WyHash.Hash(data, length);
            return (uint)(hash & ChecksumMask);
        }

        if (length > int.MaxValue)
        {
            throw new NotSupportedException("Custom checksum provider does not support payloads larger than 2GB.");
        }

        var hashSpan = _checksumProvider(new ReadOnlySpan<byte>(data, (int)length));
        return (uint)(hashSpan & ChecksumMask);
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

    public sealed class ReadResult : IDisposable
    {
        private readonly WaitFreeMemoryMappedFile _owner;
        private readonly int _bufferIndex;
        private readonly SafeMemoryMappedViewHandle? _handle;
        private readonly Stream? _stream;
        private readonly IntPtr _pointer;
        private readonly long _length;
        private bool _disposed;

        internal unsafe ReadResult(
            WaitFreeMemoryMappedFile owner,
            SafeMemoryMappedViewHandle handle,
            byte* pointer,
            ReadSnapshot snapshot)
        {
            _owner = owner;
            _handle = handle;
            _bufferIndex = snapshot.ActiveIndex;
            _stream = new UnmanagedMemoryStream(pointer, snapshot.Size);
            _pointer = (IntPtr)pointer;
            _length = snapshot.Size;
            Version = snapshot.Version;
            Switched = snapshot.Switched;
            Size = snapshot.Size;
        }

        private ReadResult(WaitFreeMemoryMappedFile owner, WaitFreeVersion version, bool switched)
        {
            _owner = owner;
            _handle = null;
            _bufferIndex = -1;
            _stream = Stream.Null;
            _pointer = IntPtr.Zero;
            _length = 0;
            Version = version;
            Switched = switched;
            Size = 0;
        }

        public Stream Stream => _stream ?? Stream.Null;

        public long Size { get; }

        public bool Switched { get; }

        public WaitFreeVersion Version { get; }

        public unsafe ReadOnlySpan<byte> AsSpan()
        {
            if (_length == 0 || _pointer == IntPtr.Zero)
            {
                return ReadOnlySpan<byte>.Empty;
            }

            if (_length > int.MaxValue)
            {
                throw new NotSupportedException("Payload size exceeds int.MaxValue for span access.");
            }

            return new ReadOnlySpan<byte>(_pointer.ToPointer(), (int)_length);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream?.Dispose();
            if (_handle != null)
            {
                _handle.ReleasePointer();
            }

            if (_bufferIndex >= 0)
            {
                _owner.ExitRead(_bufferIndex);
            }
        }

        internal static ReadResult CreateEmpty(WaitFreeMemoryMappedFile owner, WaitFreeVersion version, bool switched)
        {
            return new ReadResult(owner, version, switched);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StateHeader
    {
        public long Version;
        public int ReaderCount0;
        public int ReaderCount1;
    }

    internal readonly struct ReadSnapshot
    {
        public static readonly ReadSnapshot Empty = new ReadSnapshot(new WaitFreeVersion(0), -1, false);

        public ReadSnapshot(WaitFreeVersion version, int activeIndex, bool switched)
        {
            Version = version;
            ActiveIndex = activeIndex;
            Switched = switched;
            Size = version.IsInitialized ? version.Size : 0;
        }

        public WaitFreeVersion Version { get; }

        public int ActiveIndex { get; }

        public bool Switched { get; }

        public long Size { get; }
    }

    public readonly struct WaitFreeVersion
    {
        internal WaitFreeVersion(long value)
        {
            RawValue = value;
        }

        internal long RawValue { get; }

        public bool IsInitialized => RawValue != 0;

        public int Index => (int)(RawValue & 1);

        public long Size => (RawValue >> 1) & MaxDataSize;

        public uint Checksum => (uint)((RawValue >> (DataSizeBits + 1)) & ChecksumMask);

        internal static WaitFreeVersion Create(int index, long size, uint checksum)
        {
            if (index is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (size < 0 || size > MaxDataSize)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }

            var value = ((long)index & 1)
                        | (((long)size & MaxDataSize) << 1)
                        | (((long)checksum & ChecksumMask) << (DataSizeBits + 1));
            return new WaitFreeVersion(value);
        }
    }
}
