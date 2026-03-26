using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.Threading;
using TigaIpc.IO;
using IAsyncDisposable = Microsoft.VisualStudio.Threading.IAsyncDisposable;

namespace TigaIpc.Messaging
{
    /// <summary>
    /// TigaChannel is a bidirectional IPC channel that can be used to communicate between processes.
    /// </summary>
    public partial class TigaChannel : ITigaChannel
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly Guid _instanceId = Guid.NewGuid();
        private readonly ITigaMemoryMappedFile _readFile;
        private readonly ITigaMemoryMappedFile _writeFile;
        private readonly bool _disposeReadFile;
        private readonly bool _disposeWriteFile;
        private readonly TimeProvider _timeProvider;
        private readonly IOptions<TigaIpcOptions> _options;
        private readonly ConcurrentDictionary<Guid, Channel<LogEntry>> _receiverChannels = new();
        private readonly Task _receiverTask;
        private readonly TaskCompletionSource<bool> _receiverTaskCompletionSource = new();
        private readonly ConcurrentDictionary<string, Func<object?, string>> _eventAggregator = new();
        private readonly ConcurrentDictionary<string, Func<string, Task<string>>> _asyncEventAggregator = new();
        private readonly ConcurrentDictionary<string, Func<Task>> _asyncTaskEventAggregator = new();
        private readonly ConcurrentDictionary<string, object> _asyncGenericEventAggregator = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ResponseMessage>> _pendingResponses = new();

        private readonly ILogger<TigaChannel>? _logger;
        private readonly bool _enableCompression;
        private readonly int _compressionThreshold;
        private readonly int _logBookSchemaVersion;
        private readonly bool _allowLegacyLogBook;

        private readonly SemaphoreSlim _messageReaderSemaphore = new(1, 1);
        private readonly SemaphoreSlim _invokeLock = new(1, 1);
        private readonly SemaphoreSlim _publishMessageSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _invokeMessageLock = new(1, 1);

        private readonly ConcurrentQueue<BinaryData> _pendingInvokeMessages = new();

        private bool _disposed;
        private long _lastEntryId = -1;
        private long _messagesPublished;
        private long _messagesReceived;

        private readonly ObjectPool<StringBuilder> _stringBuilderPool;

        private JoinableTaskFactory? _joinableTaskFactory;
        private JoinableTaskContext? _joinableTaskContext;

        private long _invokeCount;
        private long _invokeFailureCount;
        private long _deserializationFailureCount;

        private bool HasInvokeHandlers =>
            !_eventAggregator.IsEmpty ||
            !_asyncEventAggregator.IsEmpty ||
            !_asyncTaskEventAggregator.IsEmpty ||
            !_asyncGenericEventAggregator.IsEmpty;

        private JoinableTaskFactory JoinableTaskFactory
        {
            get
            {
                if (_joinableTaskFactory == null)
                {
                    _joinableTaskContext = new JoinableTaskContext();
                    _joinableTaskFactory = new JoinableTaskFactory(_joinableTaskContext);
                }

                return _joinableTaskFactory;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TigaChannel"/> class.
        /// </summary>
        /// <param name="name">A unique system wide name of this channel. Internal primitives will be prefixed before use.</param>
        /// <param name="type">type.</param>
        /// <param name="options">Options from dependency injection or an OptionsWrapper containing options.</param>
        /// <param name="logger">logger.</param>
#if NET
        [SupportedOSPlatform("windows")]
#endif
        public TigaChannel(
            string name,
            MappingType type = MappingType.Memory,
            IOptions<TigaIpcOptions>? options = null,
            ILogger<TigaChannel>? logger = null)
            : this(
                TigaMemoryMappedFileFactory.Create(
                    name,
                    type,
                    options ?? new OptionsWrapper<TigaIpcOptions>(new TigaIpcOptions()),
                    out var resolvedOptions),
                disposeReadFile: true,
                TigaMemoryMappedFileFactory.Create(
                    name,
                    type,
                    resolvedOptions,
                    out _),
                disposeWriteFile: true,
                TimeProvider.System,
                resolvedOptions,
                logger)
        {
            Name = name;
            Println("Wait-free synchronization enabled for TigaChannel instance");
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TigaChannel"/> class with separate read/write channels.
        /// </summary>
        /// <param name="readName">Name of the read channel.</param>
        /// <param name="writeName">Name of the write channel.</param>
        /// <param name="type">type.</param>
        /// <param name="options">Options from dependency injection or an OptionsWrapper containing options.</param>
        /// <param name="logger">logger.</param>
#if NET
        [SupportedOSPlatform("windows")]
#endif
        public TigaChannel(
            string readName,
            string writeName,
            MappingType type = MappingType.Memory,
            IOptions<TigaIpcOptions>? options = null,
            ILogger<TigaChannel>? logger = null)
            : this(
                TigaMemoryMappedFileFactory.Create(
                    readName,
                    type,
                    options ?? new OptionsWrapper<TigaIpcOptions>(new TigaIpcOptions()),
                    out var resolvedOptions),
                disposeReadFile: true,
                TigaMemoryMappedFileFactory.Create(
                    writeName,
                    type,
                    resolvedOptions,
                    out _),
                disposeWriteFile: true,
                TimeProvider.System,
                resolvedOptions,
                logger)
        {
            Name = $"{readName}|{writeName}";
            Println("Wait-free synchronization enabled for TigaChannel instance");
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TigaChannel"/> class.
        /// </summary>
        /// <param name="memoryMappedFile">
        /// An instance of a ITigaMemoryMappedFile that will be used to transmit messages.
        /// The file should be larger than the size of all messages that can be expected to be transmitted, including message overhead, per MinMessageAge in the options.
        /// </param>
        /// <param name="options">Options from dependency injection or an OptionsWrapper containing options.</param>
        /// <param name="logger">logger.</param>
        public TigaChannel(
            ITigaMemoryMappedFile memoryMappedFile,
            IOptions<TigaIpcOptions>? options = null,
            ILogger<TigaChannel>? logger = null)
            : this(
                memoryMappedFile,
                disposeReadFile: false,
                memoryMappedFile,
                disposeWriteFile: false,
                TimeProvider.System,
                options ?? new OptionsWrapper<TigaIpcOptions>(new TigaIpcOptions()),
                logger)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TigaChannel"/> class.
        /// </summary>
        /// <param name="memoryMappedFile">
        /// An instance of a ITigaMemoryMappedFile that will be used to transmit messages.
        /// The file should be larger than the size of all messages that can be expected to be transmitted, including message overhead, per MinMessageAge in the options.
        /// </param>
        /// <param name="disposeFile">Set to true if the file is to be disposed when this instance is disposed.</param>
        /// <param name="options">Options from dependency injection or an OptionsWrapper containing options.</param>
        /// <param name="logger">logger.</param>
        public TigaChannel(
            ITigaMemoryMappedFile memoryMappedFile,
            bool disposeFile,
            IOptions<TigaIpcOptions>? options = null,
            ILogger<TigaChannel>? logger = null)
            : this(
                memoryMappedFile,
                disposeReadFile: disposeFile,
                memoryMappedFile,
                disposeWriteFile: disposeFile,
                TimeProvider.System,
                options ?? new OptionsWrapper<TigaIpcOptions>(new TigaIpcOptions()),
                logger)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TigaChannel"/> class.
        /// </summary>
        /// <param name="memoryMappedFile">
        /// An instance of a ITigaMemoryMappedFile that will be used to transmit messages.
        /// The file should be larger than the size of all messages that can be expected to be transmitted, including message overhead, per MinMessageAge in the options.
        /// </param>
        /// <param name="disposeFile">Set to true if the file is to be disposed when this instance is disposed.</param>
        /// <param name="timeProvider">Set the time provider to use.</param>
        /// <param name="options">Options from dependency injection or an OptionsWrapper containing options.</param>
        /// <param name="logger">Set the logger to use.</param>
        public TigaChannel(
            ITigaMemoryMappedFile memoryMappedFile,
            bool disposeFile,
            TimeProvider timeProvider,
            IOptions<TigaIpcOptions> options,
            ILogger<TigaChannel>? logger = null)
            : this(
                memoryMappedFile,
                disposeReadFile: disposeFile,
                memoryMappedFile,
                disposeWriteFile: disposeFile,
                timeProvider,
                options,
                logger)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TigaChannel"/> class with separate read/write channels.
        /// </summary>
        public TigaChannel(
            ITigaMemoryMappedFile readFile,
            bool disposeReadFile,
            ITigaMemoryMappedFile writeFile,
            bool disposeWriteFile,
            TimeProvider timeProvider,
            IOptions<TigaIpcOptions> options,
            ILogger<TigaChannel>? logger = null)
        {
            _readFile = readFile ?? throw new ArgumentNullException(nameof(readFile));
            _writeFile = writeFile ?? throw new ArgumentNullException(nameof(writeFile));
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _disposeReadFile = disposeReadFile;
            _disposeWriteFile = disposeWriteFile;
            _logger = logger;
            var objectPoolProvider = new DefaultObjectPoolProvider();
            _stringBuilderPool = objectPoolProvider.CreateStringBuilderPool();

            // Cache common configuration values
            var waitTimeout = options.Value.WaitTimeout;
            var maxPublishRetries = options.Value.MaxPublishRetries;
            var minMessageAge = options.Value.MinMessageAge;
            _enableCompression = options.Value.EnableCompression;
            _compressionThreshold = options.Value.CompressionThreshold;
            _logBookSchemaVersion = Math.Max(1, options.Value.LogBookSchemaVersion);
            _allowLegacyLogBook = options.Value.AllowLegacyLogBook;
            var (receiverChannelId, receiverChannel) = CreateRegisteredReceiverChannel();
            _receiverTaskCompletionSource.TrySetResult(true);

            try
            {
                _readFile.FileUpdated += WhenFileUpdated;
                _lastEntryId = _readFile.Read(stream => DeserializeLogBook(stream).LastId);
                _receiverTask = Task.Run(
                    () => ReceiveWorkAsync(receiverChannelId, receiverChannel),
                    _cancellationTokenSource.Token);
                _ = ReceiveMessagesAsync();

                Println(
                    $"TigaChannel initialized (InstanceId={_instanceId}, WaitTimeout={waitTimeout}, MaxPublishRetries={maxPublishRetries}, MinMessageAge={minMessageAge}, Compression={_enableCompression}/{_compressionThreshold})");
            }
            catch
            {
                CleanupReceiverChannel(receiverChannelId, receiverChannel);
                _readFile.FileUpdated -= WhenFileUpdated;

                if (_disposeReadFile)
                {
                    try
                    {
                        _readFile.Dispose();
                    }
                    catch
                    {
                        // Ignore cleanup failures from a failed constructor path.
                    }
                }

                if (_disposeWriteFile && !ReferenceEquals(_readFile, _writeFile))
                {
                    try
                    {
                        _writeFile.Dispose();
                    }
                    catch
                    {
                        // Ignore cleanup failures from a failed constructor path.
                    }
                }

                throw;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TigaChannel"/> class with custom tasks.
        /// This constructor is primarily used for testing purposes.
        /// </summary>
        /// <param name="receiverTask">The task that will be used for receiving messages.</param>
        /// <param name="responseTask">The task that will be used for handling responses.</param>
        public TigaChannel(Task receiverTask, Task responseTask)
        {
            _receiverTask = receiverTask ?? throw new ArgumentNullException(nameof(receiverTask));
            _instanceId = Guid.NewGuid();
            _cancellationTokenSource = new CancellationTokenSource();
            _messageReaderSemaphore = new SemaphoreSlim(1, 1);
            _receiverChannels = new ConcurrentDictionary<Guid, Channel<LogEntry>>();
            _receiverTaskCompletionSource = new TaskCompletionSource<bool>();
            _timeProvider = TimeProvider.System;
            _options = new OptionsWrapper<TigaIpcOptions>(new TigaIpcOptions());
            _readFile = new NullMemoryMappedFile();
            _writeFile = _readFile;
            _disposeReadFile = false;
            _disposeWriteFile = false;
            var objectPoolProvider = new DefaultObjectPoolProvider();
            _stringBuilderPool = objectPoolProvider.CreateStringBuilderPool();
        }

        /// <summary>
        /// Called when a new message is received
        /// </summary>
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

        /// <summary>
        /// Called when a new message is received
        /// </summary>
        private event EventHandler<MessageResponseEventArgs>? MessageResponse;

        /// <summary>
        ///  Gets called when a new message is received
        /// </summary>
        public string? Name { get; }

        /// <summary>
        /// Gets the number of messages published by this instance.
        /// </summary>
        public long MessagesPublished => Interlocked.Read(ref _messagesPublished);

        /// <summary>
        /// Gets the number of messages received by this instance.
        /// </summary>
        public long MessagesReceived => Interlocked.Read(ref _messagesReceived);

        /// <summary>
        /// Gets the number of method invocations by this instance.
        /// </summary>
        public long InvokeCount => Interlocked.Read(ref _invokeCount);

        /// <summary>
        /// Gets the number of method invocations that failed by this instance.
        /// </summary>
        public long InvokeFailureCount => Interlocked.Read(ref _invokeFailureCount);

        /// <summary>
        /// Gets the number of deserialization failures by this instance.
        /// </summary>
        public long DeserializationFailureCount => Interlocked.Read(ref _deserializationFailureCount);

        public SynchronizationMetrics? GetSynchronizationMetrics()
        {
            var readMetrics = (_readFile as ISynchronizationMetricsProvider)?.GetSynchronizationMetrics();
            var writeMetrics = (_writeFile as ISynchronizationMetricsProvider)?.GetSynchronizationMetrics();

            if (ReferenceEquals(_readFile, _writeFile))
            {
                return readMetrics ?? writeMetrics;
            }

            if (readMetrics == null)
            {
                return writeMetrics;
            }

            if (writeMetrics == null)
            {
                return readMetrics;
            }

            return new SynchronizationMetrics(
                readMetrics.LockTimeouts + writeMetrics.LockTimeouts,
                readMetrics.LockAbandoned + writeMetrics.LockAbandoned,
                readMetrics.ReaderGraceResets + writeMetrics.ReaderGraceResets);
        }

        public void Dispose()
        {
            Println("method invocation: Dispose");
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                try
                {
                    var waitTimeout = _options?.Value?.WaitTimeout ?? TimeSpan.FromSeconds(5);

                    // cancel event
                    _readFile.FileUpdated -= WhenFileUpdated;

                    // cancel all operations
                    _cancellationTokenSource.Cancel();

                    _disposed = true;
#if NET
                    _pendingInvokeMessages.Clear();
#endif

                    // Complete all receive channels
                    foreach (var receiverChannel in _receiverChannels)
                    {
                        receiverChannel.Value.Writer.TryComplete();
                    }

                    // Waiting to receive task completion
                    try
                    {
                        if (_receiverTask != null)
                        {
                            // Use timeout waits to avoid blocking indefinitely
                            if (!_receiverTask.Wait(waitTimeout))
                            {
                                Println("Receiver task did not complete within timeout during disposal");
                            }
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        // Expected exceptions that can be ignored
                    }
                    catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is TaskCanceledException))
                    {
                        // Expected exceptions that can be ignored
                    }

                    // Handling memory-mapped files
                    var sameFile = ReferenceEquals(_readFile, _writeFile);
                    var readerSemaphoreHeld = false;

                    if (_disposeReadFile)
                    {
                        if (!TryAcquireSemaphoreForDisposal(_messageReaderSemaphore, waitTimeout, nameof(_messageReaderSemaphore)))
                        {
                            Println("Could not acquire message reader semaphore for disposal");
                        }
                        else
                        {
                            readerSemaphoreHeld = true;
                            try
                            {
                                _readFile.Dispose();
                            }
                            catch (Exception ex)
                            {
                                PrintFailed(ex, "Error disposing read memory mapped file");
                            }
                        }
                    }

                    if (_disposeWriteFile && !sameFile)
                    {
                        try
                        {
                            _writeFile.Dispose();
                        }
                        catch (Exception ex)
                        {
                            PrintFailed(ex, "Error disposing write memory mapped file");
                        }
                    }

                    DisposeSemaphoreBestEffort(_messageReaderSemaphore, nameof(_messageReaderSemaphore), waitTimeout, readerSemaphoreHeld);
                    DisposeSemaphoreBestEffort(_publishMessageSemaphore, nameof(_publishMessageSemaphore), waitTimeout);
                    DisposeSemaphoreBestEffort(_invokeMessageLock, nameof(_invokeMessageLock), waitTimeout);
                    DisposeSemaphoreBestEffort(_invokeLock, nameof(_invokeLock), waitTimeout);

                    // Release of other resources
                    _cancellationTokenSource?.Dispose();

                    // release JoinableTaskContext resources
                    if (_joinableTaskContext is IDisposable disposableContext)
                    {
                        try
                        {
                            disposableContext.Dispose();
                        }
                        catch (Exception ex)
                        {
                            PrintFailed(ex, "Error disposing JoinableTaskContext");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // only record exceptions, do not rethrow, to ensure the resource release process is complete
                    PrintFailed(ex, "Unexpected error during TigaChannel disposal");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAsync(true);
            GC.SuppressFinalize(this);
        }

        protected virtual async ValueTask DisposeAsync(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                try
                {
                    var waitTimeout = _options?.Value?.WaitTimeout ?? TimeSpan.FromSeconds(5);

                    // Unsubscribe event
                    _readFile.FileUpdated -= WhenFileUpdated;

                    // Cancel all operations
#if NET7_0_OR_GREATER
                    if (_cancellationTokenSource != null)
                    {
                        await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);
                    }
#else
                    _cancellationTokenSource?.Cancel();
#endif

                    _eventAggregator.Clear();
                    _disposed = true;

                    // Complete all receive channels
                    foreach (var receiverChannel in _receiverChannels)
                    {
                        receiverChannel.Value.Writer.TryComplete();
                    }

                    // Waiting to receive task completion
                    try
                    {
                        if (_receiverTask != null)
                        {
                            // Use timeout waits to avoid blocking indefinitely
                            var timeoutTask = Task.Delay(waitTimeout);
                            var completedTask = await Task.WhenAny(_receiverTask, timeoutTask).ConfigureAwait(false);

                            if (completedTask == timeoutTask)
                            {
                                PrintWarn("Receiver task did not complete within timeout during async disposal");
                            }
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        // Expected exceptions that can be ignored
                    }
                    catch (Exception ex) when (ex is AggregateException aggregateEx &&
                                               aggregateEx.InnerExceptions.All(e => e is TaskCanceledException))
                    {
                        // Expected exceptions that can be ignored
                    }

                    // Handling memory-mapped files
                    var sameFile = ReferenceEquals(_readFile, _writeFile);
                    var readerSemaphoreHeld = false;

                    if (_disposeReadFile)
                    {
                        if (!await TryAcquireSemaphoreForDisposalAsync(_messageReaderSemaphore, waitTimeout, nameof(_messageReaderSemaphore))
                                .ConfigureAwait(false))
                        {
                            PrintWarn("Could not acquire message reader semaphore for async disposal");
                        }
                        else
                        {
                            readerSemaphoreHeld = true;
                            try
                            {
                                if (_readFile is IAsyncDisposable asyncDisposable)
                                {
                                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                                }
                                else
                                {
                                    _readFile.Dispose();
                                }
                            }
                            catch (Exception ex)
                            {
                                PrintFailed(ex, "Error disposing read memory mapped file asynchronously");
                            }
                        }
                    }

                    if (_disposeWriteFile && !sameFile)
                    {
                        try
                        {
                            if (_writeFile is IAsyncDisposable asyncDisposable)
                            {
                                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                            }
                            else
                            {
                                _writeFile.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            PrintFailed(ex, "Error disposing write memory mapped file asynchronously");
                        }
                    }

                    DisposeSemaphoreBestEffort(_messageReaderSemaphore, nameof(_messageReaderSemaphore), waitTimeout, readerSemaphoreHeld);
                    await DisposeSemaphoreBestEffortAsync(_publishMessageSemaphore, nameof(_publishMessageSemaphore), waitTimeout)
                        .ConfigureAwait(false);
                    await DisposeSemaphoreBestEffortAsync(_invokeMessageLock, nameof(_invokeMessageLock), waitTimeout)
                        .ConfigureAwait(false);
                    await DisposeSemaphoreBestEffortAsync(_invokeLock, nameof(_invokeLock), waitTimeout)
                        .ConfigureAwait(false);

                    // Release of other resources
                    _cancellationTokenSource?.Dispose();

                    // 释放 JoinableTaskContext 资源
                    if (_joinableTaskContext is IAsyncDisposable asyncDisposableContext)
                    {
                        try
                        {
                            await asyncDisposableContext.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            PrintFailed(ex, "Error disposing JoinableTaskContext asynchronously");
                        }
                    }
                    else if (_joinableTaskContext is IDisposable disposableContext)
                    {
                        try
                        {
                            disposableContext.Dispose();
                        }
                        catch (Exception ex)
                        {
                            PrintFailed(ex, "Error disposing JoinableTaskContext");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // only record exceptions, do not rethrow, to ensure the resource release process is complete
                    PrintFailed(ex, "Unexpected error during TigaChannel async disposal");
                }
            }
        }

        private static bool TryAcquireSemaphoreForDisposal(SemaphoreSlim semaphore, TimeSpan timeout, string semaphoreName)
        {
            try
            {
                return semaphore.Wait(timeout);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        private static async Task<bool> TryAcquireSemaphoreForDisposalAsync(
            SemaphoreSlim semaphore,
            TimeSpan timeout,
            string semaphoreName)
        {
            try
            {
                return await semaphore.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        private void DisposeSemaphoreBestEffort(
            SemaphoreSlim semaphore,
            string semaphoreName,
            TimeSpan timeout,
            bool alreadyHeld = false)
        {
            var acquired = alreadyHeld;
            try
            {
                if (!acquired)
                {
                    acquired = TryAcquireSemaphoreForDisposal(semaphore, timeout, semaphoreName);
                }

                if (!acquired)
                {
                    PrintWarn("Could not acquire {SemaphoreName} for disposal", semaphoreName);
                    return;
                }

                semaphore.Dispose();
            }
            catch (Exception ex)
            {
                PrintFailed(ex, "Error disposing semaphore {SemaphoreName}", semaphoreName);
            }
        }

        private async Task DisposeSemaphoreBestEffortAsync(
            SemaphoreSlim semaphore,
            string semaphoreName,
            TimeSpan timeout,
            bool alreadyHeld = false)
        {
            var acquired = alreadyHeld;
            try
            {
                if (!acquired)
                {
                    acquired = await TryAcquireSemaphoreForDisposalAsync(semaphore, timeout, semaphoreName)
                        .ConfigureAwait(false);
                }

                if (!acquired)
                {
                    PrintWarn("Could not acquire {SemaphoreName} for async disposal", semaphoreName);
                    return;
                }

                semaphore.Dispose();
            }
            catch (Exception ex)
            {
                PrintFailed(ex, "Error disposing semaphore {SemaphoreName}", semaphoreName);
            }
        }

        /// <summary>
        /// Resets MessagesSent and MessagesReceived counters
        /// </summary>
        public void ResetMetrics()
        {
#if NET7_0_OR_GREATER
            ObjectDisposedException.ThrowIf(_disposed, this);
#else
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TigaChannel));
            }
#endif

            Interlocked.Exchange(ref _messagesPublished, 0);
            Interlocked.Exchange(ref _messagesReceived, 0);
        }

        private int PublishMessages(Stream readStream, Stream writeStream, Queue<BinaryData> publishQueue,
            TimeSpan timeout)
        {
            try
            {
                Println("[PublishMessages] deserialize log book");
                // Deserialization logbook
                var logBook = DeserializeLogBook(readStream);
                var lastId = logBook.LastId;

                // Calculate the number of entries to be pruned
                var minMessageAge = _options?.Value?.MinMessageAge ?? TimeSpan.FromMinutes(1);
                var entriesToTrim = logBook.CountEntriesToTrim(_timeProvider, minMessageAge);

                // Calculate log size and get valid entries
                var logSize = logBook.CalculateLogSize(entriesToTrim);
                var entries = entriesToTrim == 0
                    ? logBook.Entries
                    : logBook.Entries.Skip(entriesToTrim).ToImmutableList();

                // Starts the timer after the deserialization log so that the deserialization doesn't take up too many time slices
                var slotTimer = Stopwatch.StartNew();
                var batchTime = _timeProvider.GetTimestamp();
                var publishCount = 0;
                var maxFileSize = _writeFile.MaxFileSize;
                var instanceId = _instanceId;

                // Performance optimization: pre-allocating sufficient capacity
                var estimatedCapacity = Math.Min(publishQueue.Count, 100); // 限制最大预分配数量
                var newEntries = new List<LogEntry>(entries.Count + estimatedCapacity);
                newEntries.AddRange(entries);

                // Try to process all messages in the publishing queue, but don't hold the write lock indefinitely
                while (publishQueue.Count > 0 && slotTimer.Elapsed < timeout)
                {
                    // Check if the next message can be put into the log
                    var nextMessage = publishQueue.Peek();
                    var nextMessageSize = LogEntry.Overhead + nextMessage.Length + (nextMessage.MediaType?.Length ?? 0);

                    if (logSize + nextMessageSize > maxFileSize)
                    {
                        Println(
                            $"Message size exceeds available space in memory mapped file. Message size: {nextMessageSize}, Available space: {maxFileSize - logSize}");
                        break;
                    }

                    // Retrieve message from queue
                    var message = publishQueue.Dequeue();

                    // Skip empty messages, they are also skipped on the receiving end
                    if (message.Length == 0)
                    {
                        Println("message length is 0");
                        continue;
                    }

                    // Creating a new log entry
                    var newEntry = new LogEntry
                    {
                        Id = ++lastId,
                        Instance = instanceId,
                        Message = message,
                        MediaType = message.MediaType,
                        Timestamp = batchTime
                    };

                    // Add to Entry List
                    newEntries.Add(newEntry);

                    // Update log size and post count
                    logSize += nextMessageSize;
                    publishCount++;

                    // Performance optimization: check the time once for every certain number of messages processed to avoid frequent checks
                    if (publishCount % 10 == 0 && slotTimer.Elapsed >= timeout)
                    {
                        Println("Publish Messages break");
                        break;
                    }
                }

                // If a message is posted, the updated log is written to a memory-mapped file
                if (publishCount > 0)
                {
                    // 创建不可变列表并序列化
                    var immutableEntries = newEntries.ToImmutableList();
                    WriteLogBook(writeStream, new LogBook(lastId, immutableEntries));
                }

                return publishCount;
            }
            catch (Exception ex)
            {
                PrintFailed(ex, "Error publishing messages");
                throw;
            }
        }

        internal Task ReadAsync() => ReadMessagesSafelyAsync();

        private void WhenFileUpdated(object? sender, EventArgs args)
        {
            _ = ReadAsync();
        }

        private async Task ReadMessagesSafelyAsync()
        {
            try
            {
                await ReceiveMessagesAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested || _disposed)
            {
                // Disposal and receiver cancellation are expected shutdown paths.
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                // In-flight fire-and-forget reads may observe disposal during shutdown.
            }
            catch (Exception ex) when (!_disposed)
            {
                PrintFailed(ex, "Unhandled error in background channel read");
            }
        }

        /// <summary>
        /// add a method for publishing invoke message, avoid conflict with normal publish
        /// </summary>
        /// <param name="message">binary data</param>
        /// <param name="cancellationToken">cancellation token</param>
        /// <returns>async task</returns>
        /// <exception cref="ObjectDisposedException">object has been disposed</exception>
        private async Task PublishInvokeMessageAsync(BinaryData message, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TigaChannel));
            }

            if (message == null || message.Length == 0)
            {
                return;
            }

            try
            {
                Println($"Publishing invoke message with size {message.Length}");

                // Try to parse the message for more information
                if (message.TryToObject<InvokeMessage>(out var invokeMsg))
                {
                    Println($"Publishing invoke message for method {invokeMsg.Method} with ID {invokeMsg.Id}");
                }

                _writeFile.WaitForListenerReady(Timeout.InfiniteTimeSpan, cancellationToken);
                Println("Remote listener is ready for invoke publish");

                // Waiting to receive mission readiness
                var timeoutTask = Task.Delay(
                    _options?.Value?.WaitTimeout ?? TimeSpan.FromSeconds(5),
                    cancellationToken);
                var completedTask = await Task.WhenAny(_receiverTaskCompletionSource.Task, timeoutTask)
                    .ConfigureAwait(false);
                if (completedTask == timeoutTask)
                {
                    PrintWarn($"Timed out waiting for receiver task to be ready");
                    return;
                }

                // Record of posted messages
                Println($"Publishing invoke message with size {message.Length}");

                // Creating a Publishing Queue
                var publishQueue = new Queue<BinaryData>();
                publishQueue.Enqueue(message);
                var retryCount = 0;
                var maxRetries = _options?.Value?.MaxPublishRetries ?? 3;

                // Process all messages until the queue is empty or cancel the operation
                while (publishQueue.Count > 0 && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Use a smaller scope lock only when accessing the memory-mapped file
                        // This allows multiple requests to be processed in parallel until they need to access the shared resource
                        await _invokeMessageLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            // Read and write memory-mapped files
                            _writeFile.ReadWrite(
                                (readStream, writeStream) =>
                                {
                                    var publishCount = PublishMessages(
                                        readStream,
                                        writeStream,
                                        publishQueue,
                                        TimeSpan.FromMilliseconds(100));
                                    if (publishCount > 0)
                                    {
                                        Interlocked.Add(ref _messagesPublished, publishCount);
                                        Println($"Published {publishCount} messages");

                                        // Reset Retry Count
                                        retryCount = 0;
                                    }
                                    else if (publishQueue.Count > 0)
                                    {
                                        // If no message has been posted but the queue is not empty, increase the retry count
                                        retryCount++;
                                        Println($"Failed to publish message, retry count: {retryCount}");
                                    }
                                }, cancellationToken);
                        }
                        finally
                        {
                            _invokeMessageLock.Release();
                        }

                        // If there are still messages in the queue, wait for some time and retry
                        if (publishQueue.Count > 0)
                        {
                            // Using an Index Exit Strategy
                            var delayMs = Math.Min(50 * Math.Pow(2, retryCount), 1000);
                            Println($"Queue not empty, waiting {delayMs}ms before retry");
                            await Task.Delay((int)delayMs, cancellationToken).ConfigureAwait(false);

                            // Log a warning if there are too many retries
                            if (retryCount >= maxRetries)
                            {
                                PrintWarn($"Failed after {retryCount} retries");
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        Println($"PublishInvokeMessageAsync canceled");
                        break;
                    }
                    catch (Exception ex) when (!_disposed)
                    {
                        PrintFailed(ex, $"Error publishing invoke message");
                        retryCount++;

                        if (retryCount >= maxRetries)
                        {
                            PrintWarn($"Aborting invoke publish operation after {retryCount} retries");
                            break;
                        }

                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (publishQueue.Count > 0)
                {
                    PrintWarn($"Failed to publish {publishQueue.Count} messages");
                }
                else
                {
                    Println("Successfully published all messages");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Anticipated canceled operations
                Println("Operation was canceled");
            }
            catch (Exception ex) when (!_disposed)
            {
                PrintFailed(ex, "Unexpected error in PublishInvokeMessageAsync");
                throw;
            }
        }

        /// <summary>
        /// Receives messages from the memory mapped file and forwards them to the registered receiver channels.
        /// This method is optimized for performance and includes enhanced error handling.
        /// </summary>
        private async Task ReceiveMessagesAsync()
        {
            if (_disposed)
            {
                return;
            }

            // Use local variables to avoid multiple access to fields
            var semaphoreTimeout = _options?.Value?.WaitTimeout ?? TimeSpan.FromSeconds(5);
            var instanceId = _instanceId;
            var logger = _logger;

            // Try to get a semaphore, use a timeout to avoid waiting indefinitely
            bool semaphoreAcquired = false;
            try
            {
                semaphoreAcquired = await _messageReaderSemaphore
                    .WaitAsync(semaphoreTimeout, _cancellationTokenSource.Token)
                    .ConfigureAwait(false);
                if (!semaphoreAcquired)
                {
                    PrintWarn("Could not acquire message reader semaphore within timeout period");
                    return;
                }

                // Double-check if the object has been released to avoid the object being released during the acquisition of the semaphore
                if (_disposed)
                {
                    return;
                }

                // Reading the logbook
                LogBook logBook;
                try
                {
                    logBook = _readFile.Read(stream => DeserializeLogBook(stream));
                }
                catch (Exception ex)
                {
                    PrintFailed(ex, "Error reading from memory mapped file");
                    Console.WriteLine($"[ReceiveMessagesAsync] Read failed: {ex}");
                    return;
                }

                // Get the ID of the last read and update it
                long readFrom = _lastEntryId;

                // If there are no new entries, it returns directly
                if (logBook.LastId <= readFrom || logBook.Entries.Count == 0)
                {
                    if (logBook.LastId > readFrom)
                    {
                        _lastEntryId = logBook.LastId;
                    }

                    return;
                }

                // Performance optimization: pre-calculate the number of entries to be processed
                var entriesToProcess = new List<LogEntry>(logBook.Entries.Count);
                for (var i = 0; i < logBook.Entries.Count; i++)
                {
                    var entry = logBook.Entries[i];
                    if (entry.Id <= readFrom || entry.Instance == instanceId || entry.Message.Length == 0)
                    {
                        continue;
                    }

                    entriesToProcess.Add(entry);
                }

                // If there are no entries that need to be processed, it returns directly to the
                if (entriesToProcess.Count == 0)
                {
                    _lastEntryId = logBook.LastId;
                    return;
                }

                // Record the number of messages received
                Interlocked.Add(ref _messagesReceived, entriesToProcess.Count);

                // Get a snapshot of the current receive channel to avoid the collection being modified during iteration
                var receiverChannels = _receiverChannels.ToArray();
                if (receiverChannels.Length == 0)
                {
                    return;
                }

                // Batch processing of all entries
                foreach (var entry in entriesToProcess)
                {
                    // log
                    if (logger is not null)
                    {
                        LogReceivedMessage(logger, entry.Message.Length, entry.MediaType);
                    }

                    // Forward messages to all receiving channels
                    foreach (var kvp in receiverChannels)
                    {
                        try
                        {
                            // Use TryWrite to optimize performance and avoid unnecessary asynchronous operations
                            if (!kvp.Value.Writer.TryWrite(entry))
                            {
                                // If TryWrite fails, fallback to asynchronous write
                                await kvp.Value.Writer.WriteAsync(entry).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex) when (!_disposed)
                        {
                            // Only log exceptions in non-disposal states
                            PrintFailed(ex, "Error forwarding message to receiver channel");
                        }
                    }
                }

                _lastEntryId = logBook.LastId;
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                // expected cancellation operation, no need to log
            }
            catch (Exception ex) when (!_disposed)
            {
                // Only log exceptions in non-disposal states
                PrintFailed(ex, "Unexpected error in ReceiveMessagesAsync");
                Console.WriteLine($"[ReceiveMessagesAsync] Unexpected error: {ex}");
            }
            finally
            {
                // Ensure the semaphore is released
                if (semaphoreAcquired)
                {
                    _messageReaderSemaphore.Release();
                }
            }
        }

        /// <summary>
        /// Worker task that processes messages from the receiver channels and invokes the MessageReceived event.
        /// This method handles message routing based on protocol type and manages error handling.
        /// </summary>
        private async Task ReceiveWorkAsync(Guid id, Channel<LogEntry> receiverChannel)
        {
            try
            {
                await foreach (var entry in StreamEntriesAsync(receiverChannel.Reader, _cancellationTokenSource.Token)
                                   .ConfigureAwait(false))
                {
                    if (_disposed)
                    {
                        break;
                    }

                    try
                    {
                        // Record detailed information when processing messages
                        Println($"Processing message with ID: {entry.Id}");

                        // Performance optimization: avoid unnecessary memory allocation
                        if (entry.Message.Length == 0)
                        {
                            continue;
                        }

                        var postData = BinaryData.FromBytes(entry.Message, entry.MediaType);

                        // Read the message type EventProtocol to determine if it is Publisher or Invoke
                        if (!postData.TryToObject<MessageBase>(out var message))
                        {
                            PrintWarn(
                                "Received invalid message format that could not be deserialized as MessageBase");
                            continue;
                        }

                        // Use specialized methods to handle different types of messages, improving code readability and maintainability
                        Println($"Processing message with Protocol: {message.Protocol}");
                        switch (message.Protocol)
                        {
                            case EventProtocol.Invoke:
                                // Handle Invoke messages
                                if (!HasInvokeHandlers)
                                {
                                    break;
                                }

                                Println($"Invoke Message: {postData}");
                                await HandleInvokeMessageAsync(postData).ConfigureAwait(false);
                                break;

                            case EventProtocol.Publisher:
                                // Handle Publisher messages
                                Println("Publisher Message: " + postData);
                                HandlePublisherMessage(postData);
                                break;

                            case EventProtocol.Response:
                                // Handle Response messages
                                Println($"Response Message with data: {postData}");
                                if (postData.TryToObject<ResponseMessage>(out var responseMsg))
                                {
                                    Println(
                                        $"Response message details - ID: {responseMsg.Id}, Protocol: {responseMsg.Protocol}, Code: {responseMsg.Code}");
                                }

                                HandleResponseMessage(postData);
                                break;

                            default:
                                PrintWarn($"Received message with unknown protocol: {message.Protocol}");
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
                    {
                        // Expected cancellation operation, no need to log
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (_logger is not null)
                        {
                            LogReceiveError(_logger, ex, entry.Id, entry.MediaType);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                // Expected cancellation operation, no need to log
            }
            catch (Exception ex) when (!_disposed)
            {
                // Only log exceptions in non-disposal states
                PrintFailed(ex, "Unexpected error in message receiver task");
            }
            finally
            {
                CleanupReceiverChannel(id, receiverChannel);
            }
        }

        private (Guid Id, Channel<LogEntry> Channel) CreateRegisteredReceiverChannel()
        {
            var id = Guid.NewGuid();
            var receiverChannel = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

            _receiverChannels[id] = receiverChannel;
            return (id, receiverChannel);
        }

        private void CleanupReceiverChannel(Guid id, Channel<LogEntry> receiverChannel)
        {
            receiverChannel.Writer.TryComplete();
            _receiverChannels.TryRemove(id, out _);
        }

        private async Task HandleInvokeMessageAsync(BinaryData postData)
        {
            Println($"Starting to handle invoke message");
            _pendingInvokeMessages.Enqueue(postData);
            Println($"Message enqueued, queue size: {_pendingInvokeMessages.Count}");
            await _invokeMessageLock.WaitAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
            Println($"Acquired semaphore, processing queue");
            try
            {
                while (_pendingInvokeMessages.TryDequeue(out var pendingMessage))
                {
                    Println($"Dequeued message for processing");
                    if (!pendingMessage.TryToObject<InvokeMessage>(out var invokeMessage) || invokeMessage == null)
                    {
                        PrintWarn("Failed to deserialize invoke message");
                        continue;
                    }

                    var messageId = invokeMessage.Id;
                    var method = invokeMessage.Method;
                    Println($"Processing message ID: {messageId}, Method: {method}, Data: {invokeMessage.Data}");

                    if (!_eventAggregator.TryGetValue(method, out var func))
                    {
                        PrintWarn($"No handler registered for method: {method}");
                        await SendErrorResponseAsync(messageId, method, $"No handler registered for method: {method}")
                            .ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        Println($"Invoking handler for method: {method}");
                        var result = func.Invoke(invokeMessage.Data);
                        Println($"Handler execution completed, result: {result}");
                        await SendResponseAsync(messageId, result).ConfigureAwait(false);
                        Println($"Response sent for message ID: {messageId}");
                    }
                    catch (Exception ex)
                    {
                        PrintFailed(ex, "Error processing invoke message for method {Method}", invokeMessage.Method);
                        await SendErrorResponseAsync(messageId, invokeMessage.Method, ex.Message).ConfigureAwait(false);
                        Println($"Error response sent for message ID: {messageId}");
                    }
                }

                Println($"Queue processing completed");
            }
            finally
            {
                _invokeMessageLock.Release();
                Println($"Released semaphore");
            }
        }

        private Task SendErrorResponseAsync(string responseId, string method, string errorMessage)
        {
            // Add error response
            var errorResponse = new ResponseMessage
            {
                Id = responseId,
                Protocol = EventProtocol.Response,
                Code = ResponseCode.Failed,
                Data = errorMessage
            };

            var errorBinary = BinaryDataExtensions.FromObjectAsMessagePack(
                errorResponse,
                compress: _options.Value.EnableCompression,
                compressionThreshold: _options.Value.CompressionThreshold);

            return PublishAsync(errorBinary, _cancellationTokenSource.Token);
        }

        private Task SendResponseAsync(string responseId, string result)
        {
            Println($"Starting to send response for message ID: {responseId}");
            var responseMessage = new ResponseMessage
            {
                Id = responseId,
                Protocol = EventProtocol.Response,
                Code = ResponseCode.Successful,
                Data = result
            };
            Println(
                $"Created response message with ID: {responseId}, Code: {ResponseCode.Successful}, Protocol: {responseMessage.Protocol}");
            var responseBinary = BinaryDataExtensions.FromObjectAsMessagePack(
                responseMessage,
                compress: _enableCompression,
                compressionThreshold: _compressionThreshold);
            Println(
                $"Serialized response message, size: {responseBinary.Length} bytes, Protocol: {responseMessage.Protocol}");

            // use independent publish method, avoid conflict with normal publish operation
            return PublishAsync(responseBinary, _cancellationTokenSource.Token);
        }

        /// <summary>
        /// Handle publisher type messages
        /// </summary>
        private void HandlePublisherMessage(BinaryData postData)
        {
            try
            {
                // parse message
                if (!postData.TryToObject<PublisherMessage>(out var publisherMessage))
                {
                    throw new Exception("Failed to deserialize publisher message");
                }

                var messageEventArgs = new MessageReceivedEventArgs(publisherMessage.Data);
                MessageReceived?.Invoke(this, messageEventArgs);
            }
            catch (Exception ex)
            {
                PrintFailed(ex, "Error raising MessageReceived event");
            }
        }

        /// <summary>
        /// 处理响应类型的消息
        /// </summary>
        private void HandleResponseMessage(BinaryData postData)
        {
            try
            {
                // Try to parse the message as a response message to record more detailed information
                if (postData.TryToObject<ResponseMessage>(out var responseMsg))
                {
                    Println(
                        $"Received response with ID: {responseMsg.Id}, Code: {responseMsg.Code}, Data: {responseMsg.Data}");

                    // check if there is a TaskCompletionSource waiting for this response
                    if (_pendingResponses.TryRemove(responseMsg.Id, out var tcs))
                    {
                        Println($"Found pending response handler for ID: {responseMsg.Id}");
                        // set the result of the TaskCompletionSource
                        tcs.TrySetResult(responseMsg);
                        Println($"Set result for pending response with ID: {responseMsg.Id}");
                    }
                    else
                    {
                        // if no corresponding TaskCompletionSource is found, trigger the event
                        Println($"No pending response handler found for ID: {responseMsg.Id}, triggering event");
                        var eventArgs = new MessageResponseEventArgs(postData);
                        MessageResponse?.Invoke(this, eventArgs);
                        Println($"MessageResponse event triggered for ID: {responseMsg.Id}");
                    }
                }
                else
                {
                    Println("Failed to parse response message");

                    // Even if parsing fails, try to trigger the event so that subscribers can handle the original message
                    Println("Attempting to trigger MessageResponse event with unparsed message");
                    var eventArgs = new MessageResponseEventArgs(postData);
                    MessageResponse?.Invoke(this, eventArgs);
                }
            }
            catch (Exception ex)
            {
                PrintFailed(ex, "Error raising MessageResponse event");
            }
        }

        private static async IAsyncEnumerable<LogEntry> StreamEntriesAsync(
            ChannelReader<LogEntry> reader,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                while (reader.TryRead(out var entry))
                {
                    yield return entry;
                }
            }
        }

        private void Println(string? message, params object?[] args)
        {
            /*
            #if DEBUG
                        Console.WriteLine(message ?? string.Empty, args);
            #endif
            */
            _logger?.LogInformation(message, args);
        }

        private void PrintFailed(string? message, params object?[] args)
        {
            _logger?.LogError(message, args);
        }

        private void PrintFailed(Exception? exception, string? message, params object?[] args)
        {
            _logger?.LogError(exception, message, args);

            // also consider outputting to the console in debug mode
#if DEBUG
            Console.Error.WriteLine($"ERROR: {string.Format(message ?? string.Empty, args)}");
            Console.Error.WriteLine($"Exception: {exception}");
#endif
        }

        private void PrintWarn(string? message, params object?[] args)
        {
            _logger?.LogWarning(message, args);
        }

        private LogBook DeserializeLogBook(Stream stream)
        {
            if (stream.Length == 0)
            {
                return new LogBook(0, []);
            }

            var payload = GetStreamPayload(stream);
            if (LooksLikeEnvelope(payload))
            {
                var envelope = MessagePackSerializer.Deserialize<LogBookEnvelope>(
                    payload,
                    MessagePackOptions.Instance
                );

                if (envelope.SchemaVersion != _logBookSchemaVersion)
                {
                    throw new InvalidOperationException(
                        $"Unsupported log book schema version {envelope.SchemaVersion} (expected {_logBookSchemaVersion}).");
                }

                return envelope.LogBook;
            }

            if (!_allowLegacyLogBook)
            {
                throw new InvalidOperationException("Legacy log book payloads are not allowed.");
            }

            return MessagePackSerializer.Deserialize<LogBook>(payload, MessagePackOptions.Instance);
        }

        private void WriteLogBook(Stream stream, LogBook logBook)
        {
            if (_logBookSchemaVersion <= 1)
            {
                MessagePackSerializer.Serialize(stream, logBook, MessagePackOptions.Instance);
                return;
            }

            var envelope = new LogBookEnvelope(_logBookSchemaVersion, logBook);
            MessagePackSerializer.Serialize(stream, envelope, MessagePackOptions.Instance);
        }

        private static ReadOnlyMemory<byte> GetStreamPayload(Stream stream)
        {
            if (stream is MemoryStream memoryStream)
            {
                if (memoryStream.TryGetBuffer(out var buffer))
                {
                    return buffer.AsMemory(0, checked((int)memoryStream.Length));
                }

                return memoryStream.ToArray();
            }

            stream.Seek(0, SeekOrigin.Begin);
            using var payloadStream = new MemoryStream();
            stream.CopyTo(payloadStream);
            return payloadStream.ToArray();
        }

        private static bool LooksLikeEnvelope(ReadOnlyMemory<byte> payload)
        {
            try
            {
                var reader = new MessagePackReader(new ReadOnlySequence<byte>(payload));
                if (reader.ReadArrayHeader() != 2)
                {
                    return false;
                }

                reader.Skip();
                if (reader.NextMessagePackType != MessagePackType.Array)
                {
                    return false;
                }

                if (reader.ReadArrayHeader() != 2)
                {
                    return false;
                }

                if (reader.NextMessagePackType != MessagePackType.Integer)
                {
                    return false;
                }

                reader.Skip();
                return reader.NextMessagePackType == MessagePackType.Array;
            }
            catch (MessagePackSerializationException)
            {
                return false;
            }
        }

        private sealed class NullMemoryMappedFile : ITigaMemoryMappedFile
        {
            public event EventHandler? FileUpdated;

            public long MaxFileSize => 0;

            public string? Name => "null";

            public int GetFileSize(CancellationToken cancellationToken = default)
            {
                return 0;
            }

            public T Read<T>(Func<MemoryStream, T> readData, CancellationToken cancellationToken = default)
            {
                return readData(new MemoryStream());
            }

            public void Write(MemoryStream data, CancellationToken cancellationToken = default)
            {
            }

            public void ReadWrite(Action<MemoryStream, MemoryStream> updateFunc, CancellationToken cancellationToken = default)
            {
                updateFunc(new MemoryStream(), new MemoryStream());
            }

            public bool WaitForListenerReady(TimeSpan timeout, CancellationToken cancellationToken = default)
            {
                return true;
            }

            public void Dispose()
            {
            }
        }

        [LoggerMessage(0, LogLevel.Debug, "Publishing {message_length} byte message, media type {media_type}")]
        private static partial void LogPublishingMessage(ILogger logger, int message_length, string? media_type);

        [LoggerMessage(1, LogLevel.Debug, "Received {message_length} byte message, media type {media_type}")]
        private static partial void LogReceivedMessage(ILogger logger, int message_length, string? media_type);

        [LoggerMessage(2, LogLevel.Error,
            "Event handler failed handling message with id {id}, media type {media_type}")]
        private static partial void LogReceiveError(ILogger logger, Exception exception, long id, string? media_type);

        [LoggerMessage(3, LogLevel.Information, "Method {method} invoked with request ID {requestId}")]
        private static partial void LogMethodInvoked(ILogger logger, string method, string requestId);

        private string FormatMessage(string format, params object[] args)
        {
            var sb = _stringBuilderPool.Get();
            try
            {
                sb.AppendFormat(format, args);
                return sb.ToString();
            }
            finally
            {
                _stringBuilderPool.Return(sb);
            }
        }
    }
}
