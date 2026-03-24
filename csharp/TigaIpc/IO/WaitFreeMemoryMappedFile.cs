using System.Buffers;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace TigaIpc.IO;

/// <summary>
/// Memory mapped file implementation inspired by Cloudflare mmap-sync.
/// Provides wait-free reads with double buffering and reader counters.
/// </summary>
public sealed class WaitFreeMemoryMappedFile
    : ITigaMemoryMappedFile,
        ISynchronizationMetricsProvider
{
    private const string FilePrefix = "tiga_";
    private const string MemoryPrefix = "tiga_mapped_file_";
    private const string StateSuffix = "_state";
    private const string DataSuffix0 = "_data_0";
    private const string DataSuffix1 = "_data_1";
    private const string EventPrefix = "tiga_wait_handle_";
    private const string NotificationSuffix = "_notify";
    private const string NotificationEventSuffix = "_slot_";
    private const string ListenerReadyEventSuffix = "_listener_ready";
    private const int NotificationSlotCount = 128;

    private const int DataSizeBits = 39;
    private const int DataChecksumBits = 24;
    private const long MaxDataSize = (1L << DataSizeBits) - 1;
    private const long ChecksumMask = (1L << DataChecksumBits) - 1;

    private readonly MemoryMappedFile _stateMap;
    private readonly MemoryMappedViewAccessor _stateAccessor;
    private readonly MemoryMappedFile _notificationMap;
    private readonly MemoryMappedViewAccessor _notificationAccessor;
    private readonly MemoryMappedFile[] _dataMaps = new MemoryMappedFile[2];
    private readonly MemoryMappedViewAccessor[] _dataAccessors = new MemoryMappedViewAccessor[2];
    private readonly FileStream? _stateFileStream;
    private readonly FileStream? _notificationFileStream;
    private readonly FileStream?[] _dataFileStreams = new FileStream?[2];
    private readonly EventWaitHandle?[] _slotSignalHandles = new EventWaitHandle?[
        NotificationSlotCount
    ];
    private readonly object _fileUpdatedLock = new();
    private readonly object _slotSignalHandlesLock = new();
    private readonly ILogger<WaitFreeMemoryMappedFile>? _logger;
    private readonly TimeSpan _waitTimeout;
    private readonly TimeSpan _readerGraceTimeout;
    private readonly TimeSpan _writerSleepDuration;
    private readonly ChecksumProvider _checksumProvider;
    private readonly bool _useDefaultChecksum;
    private readonly bool _verifyChecksumOnRead;
    private readonly long _regionSize;
    private readonly int _stateSize;
    private readonly int _notificationSize;
    private readonly MappingType _mappingType;
    private readonly bool _useSingleWriterLock;
    private readonly string _notificationEventScope;
    private readonly int _currentProcessId;
    private readonly long _currentProcessStartTimeUtcTicks;
    private readonly long _notificationSlotToken;
    private readonly TigaIpcOptions _options;

    private EventWaitHandle? _fileWaitHandle;
    private EventWaitHandle? _disposeWaitHandle;
    private Task? _fileWatcherTask;
    private bool _disposed;
    private bool _notificationListenerInitialized;
    private long _lockTimeouts;
    private long _lockAbandoned;
    private long _readerResets;
    private long _lastReadVersion;
    private int _notificationSlotIndex = -1;
    private EventHandler? _fileUpdated;

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
        ILogger<WaitFreeMemoryMappedFile>? logger = null
    )
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("File must be named", nameof(name));
        }

        options ??= new TigaIpcOptions();
        _options = options;

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
        _notificationSize = Unsafe.SizeOf<NotificationSlot>() * NotificationSlotCount;
        _mappingType = type;
        _useSingleWriterLock = options.UseSingleWriterLock;
        _currentProcessId = GetCurrentProcessId();
        _currentProcessStartTimeUtcTicks = GetCurrentProcessStartTimeUtcTicks();
        _notificationSlotToken = CreateNotificationSlotToken(
            _currentProcessId,
            _currentProcessStartTimeUtcTicks
        );

        if (_useSingleWriterLock && _mappingType != MappingType.File)
        {
            throw new PlatformNotSupportedException(
                "Single-writer lock is supported only for file-backed mappings."
            );
        }

        var stateInitRequired = false;
        string notificationIdentity;
        if (_mappingType == MappingType.File)
        {
            var prefix = GetFilePrefix(name, options);
            var statePath = prefix + StateSuffix;
            var notificationPath = prefix + NotificationSuffix;
            var dataPath0 = prefix + DataSuffix0;
            var dataPath1 = prefix + DataSuffix1;
            notificationIdentity = NormalizeNotificationIdentity(notificationPath);

            _stateMap = CreateFileMapping(
                statePath,
                _stateSize,
                options,
                out _stateFileStream,
                out stateInitRequired
            );
            _notificationMap = CreateFileMapping(
                notificationPath,
                _notificationSize,
                options,
                out _notificationFileStream,
                out _
            );
            _dataMaps[0] = CreateFileMapping(
                dataPath0,
                _regionSize,
                options,
                out _dataFileStreams[0],
                out _
            );
            _dataMaps[1] = CreateFileMapping(
                dataPath1,
                _regionSize,
                options,
                out _dataFileStreams[1],
                out _
            );

            if (_useSingleWriterLock && _stateFileStream != null)
            {
                if (!SingleWriterFileLock.TryAcquire(_stateFileStream))
                {
                    Interlocked.Increment(ref _lockTimeouts);
                    _notificationMap.Dispose();
                    _stateMap.Dispose();
                    _dataMaps[0].Dispose();
                    _dataMaps[1].Dispose();
                    _notificationFileStream?.Dispose();
                    _stateFileStream.Dispose();
                    _dataFileStreams[0]?.Dispose();
                    _dataFileStreams[1]?.Dispose();
                    throw new InvalidOperationException(
                        "Single-writer lock is already held by another process."
                    );
                }
            }
        }
        else
        {
            var stateName = MemoryPrefix + name + StateSuffix;
            var notificationName = MemoryPrefix + name + NotificationSuffix;
            var dataName0 = MemoryPrefix + name + DataSuffix0;
            var dataName1 = MemoryPrefix + name + DataSuffix1;
            notificationIdentity = notificationName;

            _stateMap = CreateNamedMapping(stateName, _stateSize, options);
            _notificationMap = CreateNamedMapping(notificationName, _notificationSize, options);
            _dataMaps[0] = CreateNamedMapping(dataName0, _regionSize, options);
            _dataMaps[1] = CreateNamedMapping(dataName1, _regionSize, options);
        }

        _stateAccessor = _stateMap.CreateViewAccessor(
            0,
            _stateSize,
            MemoryMappedFileAccess.ReadWrite
        );
        _notificationAccessor = _notificationMap.CreateViewAccessor(
            0,
            _notificationSize,
            MemoryMappedFileAccess.ReadWrite
        );
        _dataAccessors[0] = _dataMaps[0]
            .CreateViewAccessor(0, _regionSize, MemoryMappedFileAccess.ReadWrite);
        _dataAccessors[1] = _dataMaps[1]
            .CreateViewAccessor(0, _regionSize, MemoryMappedFileAccess.ReadWrite);

        InitializeState(stateInitRequired);
        InitializeNotificationState();
        _notificationEventScope = CreateNotificationEventScope(type, notificationIdentity);
    }

    /// <inheritdoc />
    public event EventHandler? FileUpdated
    {
        add
        {
            if (value == null)
            {
                return;
            }

            lock (_fileUpdatedLock)
            {
                ThrowIfDisposed();
                _fileUpdated += value;
                try
                {
                    EnsureNotificationListenerInitialized();
                }
                catch
                {
                    _fileUpdated -= value;
                    throw;
                }
            }
        }
        remove
        {
            if (value == null)
            {
                return;
            }

            lock (_fileUpdatedLock)
            {
                _fileUpdated -= value;
            }
        }
    }

    /// <inheritdoc />
    public long MaxFileSize { get; }

    /// <inheritdoc />
    public string? Name { get; }

    public SynchronizationMetrics GetSynchronizationMetrics()
    {
        return new SynchronizationMetrics(
            Interlocked.Read(ref _lockTimeouts),
            Interlocked.Read(ref _lockAbandoned),
            Interlocked.Read(ref _readerResets)
        );
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
            throw new NotSupportedException(
                "File size exceeds int.MaxValue for stream-based access."
            );
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
    public ReadResult ReadLease(
        bool verifyChecksum = true,
        CancellationToken cancellationToken = default
    )
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
                        truncated
                    );
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
                throw new NotSupportedException(
                    "File size exceeds int.MaxValue for stream-based access."
                );
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
    public void Write<T>(
        T value,
        WaitFreeSerializer<T> serializer,
        CancellationToken cancellationToken = default
    )
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
        CancellationToken cancellationToken = default
    )
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
        CancellationToken cancellationToken = default
    )
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
        CancellationToken cancellationToken = default
    )
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
    public void WriteRaw(
        ReadOnlySpan<byte> data,
        TimeSpan graceDuration,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();

        if (data.Length > _regionSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                "Serialized size exceeds configured MaxFileSize"
            );
        }

        var newIndex = AcquireNextIndex(graceDuration, cancellationToken, out var reset);
        if (reset)
        {
            _logger?.LogWarning(
                "Reader count reset after waiting {GraceTimeout} for buffer {BufferIndex}",
                graceDuration,
                newIndex
            );
        }

        WriteRegion(newIndex, data);

        var checksum = ComputeChecksum(data);
        var version = WaitFreeVersion.Create(newIndex, data.Length, checksum);
        UpdateVersion(version);
    }

    /// <summary>
    /// Reads raw data bytes as a zero-copy lease.
    /// </summary>
    public ReadResult ReadRaw(
        bool verifyChecksum = true,
        CancellationToken cancellationToken = default
    )
    {
        return ReadLease(verifyChecksum, cancellationToken);
    }

    /// <summary>
    /// Reads a typed payload using a custom deserializer.
    /// </summary>
    public T Read<T>(
        WaitFreeDeserializer<T> deserializer,
        bool verifyChecksum = true,
        CancellationToken cancellationToken = default
    )
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
        CancellationToken cancellationToken = default
    )
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
    public void Write(
        MemoryStream data,
        TimeSpan graceDuration,
        CancellationToken cancellationToken = default
    )
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        ReadWrite(
            (_, writer) =>
            {
                data.Seek(0, SeekOrigin.Begin);
                data.CopyTo(writer);
            },
            graceDuration,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public void ReadWrite(
        Action<MemoryStream, MemoryStream> updateFunc,
        CancellationToken cancellationToken = default
    )
    {
        ReadWrite(updateFunc, _readerGraceTimeout, cancellationToken);
    }

    /// <inheritdoc />
    public bool WaitForListenerReady(
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();

        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (HasLiveListener())
        {
            return true;
        }

        using var readyHandle = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            GetListenerReadyEventName(_notificationEventScope)
        );

        if (HasLiveListener())
        {
            return true;
        }

        return WaitForListenerReadyCore(readyHandle, timeout, cancellationToken);
    }

    /// <summary>
    /// Reads and then replaces the content of the memory mapped file using a custom grace duration.
    /// </summary>
    public void ReadWrite(
        Action<MemoryStream, MemoryStream> updateFunc,
        TimeSpan graceDuration,
        CancellationToken cancellationToken = default
    )
    {
        if (updateFunc is null)
        {
            throw new ArgumentNullException(nameof(updateFunc));
        }

        ThrowIfDisposed();

        using var readStream = MemoryStreamPool.Manager.GetStream(nameof(WaitFreeMemoryMappedFile));
        using var writeStream = MemoryStreamPool.Manager.GetStream(
            nameof(WaitFreeMemoryMappedFile)
        );

        using (var lease = ReadLease(false, cancellationToken))
        {
            if (lease.Size > 0)
            {
                if (lease.Size > int.MaxValue)
                {
                    throw new NotSupportedException(
                        "File size exceeds int.MaxValue for stream-based access."
                    );
                }

                lease.Stream.CopyTo(readStream);
                readStream.Seek(0, SeekOrigin.Begin);
            }
        }

        updateFunc(readStream, writeStream);
        writeStream.Seek(0, SeekOrigin.Begin);

        if (writeStream.Length > _regionSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updateFunc),
                "Serialized size exceeds configured MaxFileSize"
            );
        }

        var size = writeStream.Length;
        if (size > int.MaxValue)
        {
            throw new NotSupportedException(
                "File size exceeds int.MaxValue for stream-based access."
            );
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
                    newIndex
                );
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
        DisposeNotificationListener();

        if (disposing)
        {
            _notificationAccessor.Dispose();
            _notificationMap.Dispose();
            _stateAccessor.Dispose();
            _stateMap.Dispose();
            _dataAccessors[0].Dispose();
            _dataAccessors[1].Dispose();
            _dataMaps[0].Dispose();
            _dataMaps[1].Dispose();
            _notificationFileStream?.Dispose();
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
        var ipcDirectory = IpcDirectoryHelper.ResolveIpcDirectory(options);
        return Path.Combine(ipcDirectory, FilePrefix + name);
    }

    private static MemoryMappedFile CreateNamedMapping(
        string mapName,
        long capacity,
        TigaIpcOptions options
    )
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
        out bool requiresInitialization
    )
    {
        fileStream =
            options?.FileStreamFactory != null
                ? options.FileStreamFactory(filePath, capacity)
                : new FileStream(
                    filePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite
                );

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
            false
        );
    }

    private static EventWaitHandle CreateEventWaitHandle(
        string eventScope,
        int slotIndex,
        TigaIpcOptions options
    )
    {
        if (string.IsNullOrWhiteSpace(eventScope))
        {
            throw new ArgumentException("EventWaitHandle must be named", nameof(eventScope));
        }

        var eventName = GetNotificationEventName(eventScope, slotIndex);
        if (options?.EventWaitHandleFactory != null)
        {
            return options.EventWaitHandleFactory(eventName);
        }

        return new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
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

    private void EnsureNotificationListenerInitialized()
    {
        if (_notificationListenerInitialized)
        {
            return;
        }

        EventWaitHandle? fileWaitHandle = null;
        EventWaitHandle? disposeWaitHandle = null;
        Task? fileWatcherTask = null;
        TaskCompletionSource<bool>? watcherReady = null;
        var slotIndex = -1;

        try
        {
            slotIndex = RegisterNotificationSlot();
            fileWaitHandle = CreateEventWaitHandle(_notificationEventScope, slotIndex, _options);
            disposeWaitHandle = new EventWaitHandle(false, EventResetMode.ManualReset);
            watcherReady = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            _fileWaitHandle = fileWaitHandle;
            _notificationSlotIndex = slotIndex;
            _disposeWaitHandle = disposeWaitHandle;

            fileWatcherTask = Task.Run(() =>
                FileWatcher(watcherReady, disposeWaitHandle, fileWaitHandle)
            );
            _fileWatcherTask = fileWatcherTask;
            watcherReady.Task.GetAwaiter().GetResult();
            SyncListenerReadyEvent(true);
            _notificationListenerInitialized = true;
        }
        catch
        {
            if (disposeWaitHandle != null)
            {
                try
                {
                    disposeWaitHandle.Set();
                }
                catch
                {
                    // Best-effort rollback during listener initialization failure.
                }
            }

            try
            {
                fileWatcherTask?.Wait(_waitTimeout);
            }
            catch
            {
                // Best-effort rollback during listener initialization failure.
            }

            if (slotIndex >= 0)
            {
                UnregisterNotificationSlot(slotIndex);
            }

            try
            {
                SyncListenerReadyEvent(HasLiveListener());
            }
            catch
            {
                // Preserve the original initialization failure.
            }

            fileWaitHandle?.Dispose();
            disposeWaitHandle?.Dispose();

            _fileWaitHandle = null;
            _disposeWaitHandle = null;
            _fileWatcherTask = null;
            _notificationSlotIndex = -1;
            _notificationListenerInitialized = false;
            throw;
        }
    }

    private unsafe void InitializeNotificationState()
    {
        byte* pointer = null;
        var handle = _notificationAccessor.SafeMemoryMappedViewHandle;
        try
        {
            handle.AcquirePointer(ref pointer);
            var slots = (NotificationSlot*)pointer;
            for (var i = 0; i < NotificationSlotCount; i++)
            {
                if (Volatile.Read(ref slots[i].Token) == 0)
                {
                    Volatile.Write(ref slots[i].OwnerProcessId, 0);
                    Volatile.Write(ref slots[i].OwnerProcessStartTimeUtcTicks, 0);
                }
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

    private unsafe int RegisterNotificationSlot()
    {
        byte* pointer = null;
        var handle = _notificationAccessor.SafeMemoryMappedViewHandle;
        try
        {
            handle.AcquirePointer(ref pointer);
            var slots = (NotificationSlot*)pointer;

            for (var i = 0; i < NotificationSlotCount; i++)
            {
                ref var slot = ref slots[i];
                var token = Volatile.Read(ref slot.Token);
                if (token == 0)
                {
                    if (Interlocked.CompareExchange(ref slot.Token, _notificationSlotToken, 0) == 0)
                    {
                        Volatile.Write(
                            ref slot.OwnerProcessStartTimeUtcTicks,
                            _currentProcessStartTimeUtcTicks
                        );
                        Volatile.Write(ref slot.OwnerProcessId, _currentProcessId);
                        return i;
                    }

                    continue;
                }

                var ownerProcessId = Volatile.Read(ref slot.OwnerProcessId);
                if (ownerProcessId == 0)
                {
                    if (IsTokenOwnedByLiveProcess(token))
                    {
                        continue;
                    }

                    if (
                        Interlocked.CompareExchange(ref slot.Token, _notificationSlotToken, token)
                        == token
                    )
                    {
                        Volatile.Write(
                            ref slot.OwnerProcessStartTimeUtcTicks,
                            _currentProcessStartTimeUtcTicks
                        );
                        Volatile.Write(ref slot.OwnerProcessId, _currentProcessId);
                        return i;
                    }

                    continue;
                }

                var ownerProcessStartTimeUtcTicks = Volatile.Read(
                    ref slot.OwnerProcessStartTimeUtcTicks
                );
                if (IsSameLiveProcess(ownerProcessId, ownerProcessStartTimeUtcTicks))
                {
                    continue;
                }

                if (
                    Interlocked.CompareExchange(ref slot.Token, _notificationSlotToken, token)
                    == token
                )
                {
                    Volatile.Write(
                        ref slot.OwnerProcessStartTimeUtcTicks,
                        _currentProcessStartTimeUtcTicks
                    );
                    Volatile.Write(ref slot.OwnerProcessId, _currentProcessId);
                    return i;
                }
            }
        }
        finally
        {
            if (pointer != null)
            {
                handle.ReleasePointer();
            }
        }

        throw new InvalidOperationException(
            $"No notification slots are available for '{Name}'. Increase slot capacity or dispose unused readers."
        );
    }

    private void UnregisterNotificationSlot()
    {
        if (_notificationSlotIndex < 0)
        {
            return;
        }

        UnregisterNotificationSlot(_notificationSlotIndex);
        _notificationSlotIndex = -1;
    }

    private unsafe void UnregisterNotificationSlot(int slotIndex)
    {
        if (slotIndex < 0)
        {
            return;
        }

        byte* pointer = null;
        var handle = _notificationAccessor.SafeMemoryMappedViewHandle;
        try
        {
            handle.AcquirePointer(ref pointer);
            var slots = (NotificationSlot*)pointer;
            ref var slot = ref slots[slotIndex];
            Volatile.Write(ref slot.OwnerProcessStartTimeUtcTicks, 0);
            Volatile.Write(ref slot.OwnerProcessId, 0);
            Interlocked.CompareExchange(ref slot.Token, 0, _notificationSlotToken);
        }
        finally
        {
            if (pointer != null)
            {
                handle.ReleasePointer();
            }
        }
    }

    private unsafe void SignalUpdated()
    {
        byte* pointer = null;
        var handle = _notificationAccessor.SafeMemoryMappedViewHandle;
        try
        {
            handle.AcquirePointer(ref pointer);
            var slots = (NotificationSlot*)pointer;
            for (var i = 0; i < NotificationSlotCount; i++)
            {
                ref var slot = ref slots[i];
                var token = Volatile.Read(ref slot.Token);
                if (token == 0)
                {
                    continue;
                }

                var ownerProcessId = Volatile.Read(ref slot.OwnerProcessId);
                if (ownerProcessId == 0)
                {
                    if (!IsTokenOwnedByLiveProcess(token))
                    {
                        Volatile.Write(ref slot.OwnerProcessStartTimeUtcTicks, 0);
                        Volatile.Write(ref slot.OwnerProcessId, 0);
                        Interlocked.CompareExchange(ref slot.Token, 0, token);
                    }

                    continue;
                }

                var ownerProcessStartTimeUtcTicks = Volatile.Read(
                    ref slot.OwnerProcessStartTimeUtcTicks
                );
                if (!IsSameLiveProcess(ownerProcessId, ownerProcessStartTimeUtcTicks))
                {
                    Volatile.Write(ref slot.OwnerProcessStartTimeUtcTicks, 0);
                    Volatile.Write(ref slot.OwnerProcessId, 0);
                    Interlocked.CompareExchange(ref slot.Token, 0, token);
                    continue;
                }

                GetSlotSignalHandle(i).Set();
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

    private unsafe bool HasLiveListener()
    {
        byte* pointer = null;
        var handle = _notificationAccessor.SafeMemoryMappedViewHandle;
        try
        {
            handle.AcquirePointer(ref pointer);
            var slots = (NotificationSlot*)pointer;
            for (var i = 0; i < NotificationSlotCount; i++)
            {
                ref var slot = ref slots[i];
                var token = Volatile.Read(ref slot.Token);
                if (token == 0)
                {
                    continue;
                }

                var ownerProcessId = Volatile.Read(ref slot.OwnerProcessId);
                if (ownerProcessId == 0)
                {
                    if (IsTokenOwnedByLiveProcess(token))
                    {
                        return true;
                    }

                    Volatile.Write(ref slot.OwnerProcessStartTimeUtcTicks, 0);
                    Volatile.Write(ref slot.OwnerProcessId, 0);
                    Interlocked.CompareExchange(ref slot.Token, 0, token);
                    continue;
                }

                var ownerProcessStartTimeUtcTicks = Volatile.Read(
                    ref slot.OwnerProcessStartTimeUtcTicks
                );
                if (IsSameLiveProcess(ownerProcessId, ownerProcessStartTimeUtcTicks))
                {
                    return true;
                }

                Volatile.Write(ref slot.OwnerProcessStartTimeUtcTicks, 0);
                Volatile.Write(ref slot.OwnerProcessId, 0);
                Interlocked.CompareExchange(ref slot.Token, 0, token);
            }

            return false;
        }
        finally
        {
            if (pointer != null)
            {
                handle.ReleasePointer();
            }
        }
    }

    private EventWaitHandle GetSlotSignalHandle(int slotIndex)
    {
        lock (_slotSignalHandlesLock)
        {
            if (slotIndex == _notificationSlotIndex && _fileWaitHandle != null)
            {
                return _fileWaitHandle;
            }

            _slotSignalHandles[slotIndex] ??= CreateEventWaitHandle(
                _notificationEventScope,
                slotIndex,
                _options
            );
            return _slotSignalHandles[slotIndex]!;
        }
    }

    private void DisposeSlotSignalHandles()
    {
        lock (_slotSignalHandlesLock)
        {
            for (var i = 0; i < _slotSignalHandles.Length; i++)
            {
                _slotSignalHandles[i]?.Dispose();
                _slotSignalHandles[i] = null;
            }
        }
    }

    private void DisposeNotificationListener()
    {
        _disposeWaitHandle?.Set();

        try
        {
            _fileWatcherTask?.Wait(_waitTimeout);
        }
        catch
        {
            // Dispose should remain best-effort even if a watcher is faulted.
        }

        UnregisterNotificationSlot();
        try
        {
            SyncListenerReadyEvent(HasLiveListener());
        }
        catch
        {
            // Dispose should remain best-effort even if ready-event sync fails.
        }

        DisposeSlotSignalHandles();
        _fileWaitHandle?.Dispose();
        _disposeWaitHandle?.Dispose();
        _fileWaitHandle = null;
        _disposeWaitHandle = null;
        _fileWatcherTask = null;
        _notificationListenerInitialized = false;
    }

    private static string GetNotificationEventName(string eventScope, int slotIndex)
    {
        return $"{EventPrefix}{eventScope}{NotificationEventSuffix}{slotIndex}";
    }

    private static string GetListenerReadyEventName(string eventScope)
    {
        return $"{EventPrefix}{eventScope}{ListenerReadyEventSuffix}";
    }

    private static string CreateNotificationEventScope(
        MappingType mappingType,
        string notificationIdentity
    )
    {
        var bytes = Encoding.UTF8.GetBytes(notificationIdentity);
        var hash = WyHash.Hash(bytes);
        return $"{mappingType.ToString().ToLowerInvariant()}_{hash:x16}";
    }

    private static string NormalizeNotificationIdentity(string notificationIdentity)
    {
        var normalized = Path.GetFullPath(notificationIdentity);
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? normalized.ToUpperInvariant()
            : normalized;
    }

    private void SyncListenerReadyEvent(bool ready)
    {
        using var handle = new EventWaitHandle(
            ready,
            EventResetMode.ManualReset,
            GetListenerReadyEventName(_notificationEventScope)
        );

        if (ready)
        {
            handle.Set();
        }
        else
        {
            handle.Reset();
        }
    }

    private bool WaitForListenerReadyCore(
        EventWaitHandle readyHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        if (!cancellationToken.CanBeCanceled)
        {
            readyHandle.WaitOne(ToMillisecondsTimeout(timeout));
            return HasLiveListener();
        }

        var waitHandles = new WaitHandle[] { readyHandle, cancellationToken.WaitHandle };
        var result = WaitHandle.WaitAny(waitHandles, ToMillisecondsTimeout(timeout));
        if (result == 1)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return HasLiveListener();
    }

    private static int ToMillisecondsTimeout(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return Timeout.Infinite;
        }

        if (timeout <= TimeSpan.Zero)
        {
            return 0;
        }

        var milliseconds = timeout.TotalMilliseconds;
        if (milliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return (int)Math.Ceiling(milliseconds);
    }

    private static bool IsSameLiveProcess(int processId, long expectedStartTimeUtcTicks)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            if (expectedStartTimeUtcTicks == 0)
            {
                return true;
            }

            return process.StartTime.ToUniversalTime().Ticks == expectedStartTimeUtcTicks;
        }
        catch
        {
            return false;
        }
    }

    private static int GetCurrentProcessId()
    {
        using var process = Process.GetCurrentProcess();
        return process.Id;
    }

    private static long GetCurrentProcessStartTimeUtcTicks()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsTokenOwnedByLiveProcess(long token)
    {
        if (
            !TryGetNotificationSlotTokenIdentity(
                token,
                out var processId,
                out var processStartMarker
            )
        )
        {
            return false;
        }

        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            if (processStartMarker == 0)
            {
                return true;
            }

            return CreateNotificationProcessStartMarker(process.StartTime.ToUniversalTime().Ticks)
                == processStartMarker;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetNotificationSlotTokenIdentity(
        long token,
        out int processId,
        out uint processStartMarker
    )
    {
        if (token == 0)
        {
            processId = 0;
            processStartMarker = 0;
            return false;
        }

        var value = unchecked((ulong)token);
        processId = unchecked((int)(value >> 32));
        processStartMarker = unchecked((uint)value);
        return processId > 0;
    }

    private static uint CreateNotificationProcessStartMarker(long processStartTimeUtcTicks)
    {
        if (processStartTimeUtcTicks == 0)
        {
            return 0;
        }

        var value = unchecked((ulong)processStartTimeUtcTicks);
        return unchecked((uint)(value ^ (value >> 32)));
    }

    private static long CreateNotificationSlotToken(int processId, long processStartTimeUtcTicks)
    {
        var normalizedProcessId = processId > 0 ? processId : 1;
        var processStartMarker = CreateNotificationProcessStartMarker(processStartTimeUtcTicks);
        var token = ((long)(uint)normalizedProcessId << 32) | processStartMarker;
        return token == 0 ? 1 : token;
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
                    ref var readerCount = ref activeIndex == 0
                        ? ref state->ReaderCount0
                        : ref state->ReaderCount1;
                    Interlocked.Increment(ref readerCount);

                    var confirmValue = Volatile.Read(ref state->Version);
                    if (confirmValue == versionValue)
                    {
                        var switched =
                            Interlocked.Exchange(ref _lastReadVersion, versionValue)
                            != versionValue;
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
                ref var readerCount = ref bufferIndex == 0
                    ? ref state->ReaderCount0
                    : ref state->ReaderCount1;
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

    private int AcquireNextIndex(
        TimeSpan graceDuration,
        CancellationToken cancellationToken,
        out bool reset
    )
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

                ref var readerCount = ref nextIndex == 0
                    ? ref state->ReaderCount0
                    : ref state->ReaderCount1;

                var graceTicks =
                    graceDuration <= TimeSpan.Zero
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
            throw new NotSupportedException(
                "Custom checksum provider does not support payloads larger than 2GB."
            );
        }

        var hashSpan = _checksumProvider(new ReadOnlySpan<byte>(data, (int)length));
        return (uint)(hashSpan & ChecksumMask);
    }

    private async Task FileWatcher(
        TaskCompletionSource<bool> watcherReady,
        EventWaitHandle disposeWaitHandle,
        EventWaitHandle fileWaitHandle
    )
    {
        watcherReady.TrySetResult(true);

        var waitHandles = new[] { disposeWaitHandle, fileWaitHandle };

        while (!_disposed)
        {
            var result = WaitHandle.WaitAny(waitHandles);

            if (result == 0 || _disposed)
            {
                return;
            }

            if (result == 1)
            {
                EventHandler? handlers;
                lock (_fileUpdatedLock)
                {
                    handlers = _fileUpdated;
                }

                handlers?.Invoke(this, EventArgs.Empty);
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
            ReadSnapshot snapshot
        )
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
                throw new NotSupportedException(
                    "Payload size exceeds int.MaxValue for span access."
                );
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

        internal static ReadResult CreateEmpty(
            WaitFreeMemoryMappedFile owner,
            WaitFreeVersion version,
            bool switched
        )
        {
            return new ReadResult(owner, version, switched);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotificationSlot
    {
        public long Token;
        public long OwnerProcessStartTimeUtcTicks;
        public int OwnerProcessId;
        public int Reserved;
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
        public static readonly ReadSnapshot Empty = new ReadSnapshot(
            new WaitFreeVersion(0),
            -1,
            false
        );

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

            var value =
                ((long)index & 1)
                | (((long)size & MaxDataSize) << 1)
                | (((long)checksum & ChecksumMask) << (DataSizeBits + 1));
            return new WaitFreeVersion(value);
        }
    }
}
