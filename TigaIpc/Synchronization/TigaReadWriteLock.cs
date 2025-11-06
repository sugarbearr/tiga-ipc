using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TigaIpc.Synchronization;

/// <summary>
/// Implements a simple inter process read/write locking mechanism
/// Inspired by http://www.joecheng.com/blog/entries/Writinganinter-processRea.html
/// </summary>
public partial class TigaReadWriteLock : ITigaReadWriteLock
{
    private readonly Mutex _mutex;
    private readonly Semaphore _semaphore;
    private readonly SemaphoreSlim _synchronizationLock = new(1, 1);
    private readonly int _maxReaderCount;
    private readonly TimeSpan _waitTimeout;
    private readonly ILogger<TigaReadWriteLock>? _logger;

    private bool _disposed;
    private int _readLocks;

    public bool IsReaderLockHeld => _readLocks > 0;
    public bool IsWriterLockHeld { get; private set; }
    public string? Name { get; }

    /// <summary>
    /// Initializes a new instance of the TinyReadWriteLock class.
    /// </summary>
    /// <param name="options">Options from dependency injection or an OptionsWrapper containing options</param>
#if NET
    [SupportedOSPlatform("windows")]
#endif
    public TigaReadWriteLock(IOptions<TigaIpcOptions> options, ILogger<TigaReadWriteLock> logger)
        : this((options ?? throw new ArgumentNullException(nameof(options))).Value.Name, options.Value.MaxReaderCount,
            options.Value.WaitTimeout, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the TinyReadWriteLock class.
    /// </summary>
    /// <param name="name">A system wide unique name, the name will have a prefix appended before use</param>
#if NET
    [SupportedOSPlatform("windows")]
#endif
    public TigaReadWriteLock(string name, ILogger<TigaReadWriteLock>? logger = null)
        : this(name, TigaIpcOptions.DefaultMaxReaderCount, TigaIpcOptions.DefaultWaitTimeout, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the TinyReadWriteLock class.
    /// </summary>
    /// <param name="name">A system wide unique name, the name will have a prefix appended before use</param>
    /// <param name="maxReaderCount">Maxium simultaneous readers, default is 6</param>
#if NET
    [SupportedOSPlatform("windows")]
#endif
    public TigaReadWriteLock(string name, int maxReaderCount, ILogger<TigaReadWriteLock>? logger = null)
        : this(name, maxReaderCount, TigaIpcOptions.DefaultWaitTimeout, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the TinyReadWriteLock class.
    /// </summary>
    /// <param name="name">A system wide unique name, the name will have a prefix appended before use</param>
    /// <param name="maxReaderCount">Maxium simultaneous readers, default is 6</param>
    /// <param name="waitTimeout">How long to wait before giving up aquiring read and write locks</param>
#if NET
    [SupportedOSPlatform("windows")]
#endif
    public TigaReadWriteLock(string name, int maxReaderCount, TimeSpan waitTimeout,
        ILogger<TigaReadWriteLock>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Lock must be named", nameof(name));
        }

        if (maxReaderCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxReaderCount), "Need at least one reader");
        }

        this._maxReaderCount = maxReaderCount;
        this._waitTimeout = waitTimeout;
        this._logger = logger;

        _mutex = CreateMutex(name);
        _semaphore = CreateSemaphore(name, maxReaderCount);

        Name = name;
    }

    /// <summary>
    /// Initializes a new instance of the TinyReadWriteLock class.
    /// </summary>
    /// <param name="mutex">Should be a system wide Mutex that is used to control access to the semaphore</param>
    /// <param name="semaphore">Should be a system wide Semaphore with at least one max count, default is 6</param>
    /// <param name="maxReaderCount">Maxium simultaneous readers, must be the same as the Semaphore count, default is 6</param>
    /// <param name="waitTimeout">How long to wait before giving up aquiring read and write locks</param>
    public TigaReadWriteLock(Mutex mutex, Semaphore semaphore, int maxReaderCount, TimeSpan waitTimeout,
        ILogger<TigaReadWriteLock>? logger = null)
    {
        if (maxReaderCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxReaderCount), "Need at least one reader");
        }

        this._maxReaderCount = maxReaderCount;
        this._waitTimeout = waitTimeout;
        this._logger = logger;
        this._mutex = mutex ?? throw new ArgumentNullException(nameof(mutex));
        this._semaphore = semaphore ?? throw new ArgumentNullException(nameof(semaphore));
    }

    ~TigaReadWriteLock()
    {
        Dispose(false);
    }

    public void Dispose()
    {
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
            // The Mutex and Semaphore MUST NOT be disposed while they are being held,
            // it is better to throw and not dispose them at all rather than to dispose
            // them and prevent some other thread to release them or it might break other
            // processes as the locks may be held indefinitely.
            if (!_synchronizationLock.Wait(_waitTimeout))
            {
                throw new TimeoutException("Could not dispose of locks, timed out waiting for SemaphoreSlim");
            }
        }

        _disposed = true;

        // Always release held Mutex and Semaphore even when triggered by the finalizer
        _mutex?.Dispose();
        _semaphore?.Dispose();
        _synchronizationLock?.Dispose();
    }

    /// <summary>
    /// Acquire a read lock, only one read lock can be held by once instance
    /// but multiple read locks may be held at the same time by multiple instances
    /// </summary>
    /// <returns>A disposable that releases the read lock</returns>
    public IDisposable AcquireReadLock(CancellationToken cancellationToken = default)
    {
#if NET7_0_OR_GREATER
        ObjectDisposedException.ThrowIf(_disposed, this);
#else
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TigaReadWriteLock));
        }
#endif

        if (!_synchronizationLock.Wait(_waitTimeout, cancellationToken))
        {
            throw new TimeoutException("Did not acquire read lock, timed out while waiting for SemaphoreSlim.");
        }

        switch (WaitHandle.WaitAny([_mutex, cancellationToken.WaitHandle], _waitTimeout))
        {
            case 1:
                _synchronizationLock.Release();
                throw new OperationCanceledException("Did not acquire read lock, operation was cancelled.");

            case WaitHandle.WaitTimeout:
                _synchronizationLock.Release();
                throw new TimeoutException("Did not acquire read lock, timed out while waiting for Mutex.");
        }

        try
        {
            switch (WaitHandle.WaitAny([_semaphore, cancellationToken.WaitHandle], _waitTimeout))
            {
                case 1:
                    _synchronizationLock.Release();
                    throw new OperationCanceledException("Did not acquire read lock, operation was cancelled.");

                case WaitHandle.WaitTimeout:
                    _synchronizationLock.Release();
                    throw new TimeoutException("Did not acquire read lock, timed out while waiting for Semaphore.");
            }

            Interlocked.Increment(ref _readLocks);
        }
        finally
        {
            _mutex.ReleaseMutex();
        }

        if (_logger is not null)
        {
            LogAcquiredReadLock(_logger);
        }

        return new SynchronizationDisposable(() =>
        {
            _semaphore.Release();
            _synchronizationLock.Release();
            Interlocked.Decrement(ref _readLocks);

            if (_logger is not null)
            {
                LogReleasedReadLock(_logger);
            }
        });
    }

    /// <summary>
    /// Acquires exclusive write locking by consuming all read locks
    /// </summary>
    /// <returns>A disposable that releases the write lock</returns>
    public IDisposable AcquireWriteLock(CancellationToken cancellationToken = default)
    {
#if NET7_0_OR_GREATER
        ObjectDisposedException.ThrowIf(_disposed, this);
#else
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TigaReadWriteLock));
        }
#endif

        if (!_synchronizationLock.Wait(_waitTimeout, cancellationToken))
        {
            throw new TimeoutException("Did not acquire write lock, timed out while waiting for SemaphoreSlim.");
        }

        switch (WaitHandle.WaitAny([_mutex, cancellationToken.WaitHandle], _waitTimeout))
        {
            case 1:
                _synchronizationLock.Release();
                throw new OperationCanceledException("Did not acquire write lock, operation was cancelled.");

            case WaitHandle.WaitTimeout:
                _synchronizationLock.Release();
                throw new TimeoutException("Did not acquire write lock, timed out while waiting for Mutex.");
        }

        var readersAcquired = 0;
        try
        {
            for (var i = 0; i < _maxReaderCount; i++)
            {
                switch (WaitHandle.WaitAny([_semaphore, cancellationToken.WaitHandle], _waitTimeout))
                {
                    case 1:
                        if (readersAcquired > 0)
                        {
                            _semaphore.Release(readersAcquired);
                        }

                        _synchronizationLock.Release();
                        throw new OperationCanceledException("Could not acquire write lock, operation was cancelled.");

                    case WaitHandle.WaitTimeout:
                        if (readersAcquired > 0)
                        {
                            _semaphore.Release(readersAcquired);
                        }

                        _synchronizationLock.Release();
                        throw new TimeoutException(
                            "Could not acquire write lock, timed out while waiting for Semaphore.");
                }

                readersAcquired++;
            }

            IsWriterLockHeld = true;
        }
        finally
        {
            _mutex.ReleaseMutex();
        }

        if (_logger is not null)
        {
            LogAcquiredWriteLock(_logger);
        }

        return new SynchronizationDisposable(() =>
        {
            _semaphore.Release(_maxReaderCount);
            _synchronizationLock.Release();
            IsWriterLockHeld = false;

            if (_logger is not null)
            {
                LogReleasedWriteLock(_logger);
            }
        });
    }

    /// <summary>
    /// Create a system wide Mutex that can be used to construct a TinyReadWriteLock
    /// </summary>
    /// <param name="name">A system wide unique name, the name will have a prefix appended</param>
    /// <returns>A system wide Mutex</returns>
    public static Mutex CreateMutex(string name)
    {
        return new Mutex(false, "TinyReadWriteLock_Mutex_" + name);
    }

    /// <summary>
    /// Create a system wide Semaphore that can be used to construct a TinyReadWriteLock
    /// </summary>
    /// <param name="name">A system wide unique name, the name will have a prefix appended</param>
    /// <param name="maxReaderCount">Maximum number of simultaneous readers</param>
    /// <returns>A system wide Semaphore</returns>
#if NET
    [SupportedOSPlatform("windows")]
#endif
    public static Semaphore CreateSemaphore(string name, int maxReaderCount)
    {
        return new Semaphore(maxReaderCount, maxReaderCount, "TinyReadWriteLock_Semaphore_" + name);
    }

    [LoggerMessage(0, LogLevel.Trace, "Acquired read lock")]
    private static partial void LogAcquiredReadLock(ILogger logger);

    [LoggerMessage(1, LogLevel.Trace, "Released read lock")]
    private static partial void LogReleasedReadLock(ILogger logger);

    [LoggerMessage(2, LogLevel.Trace, "Acquired write lock")]
    private static partial void LogAcquiredWriteLock(ILogger logger);

    [LoggerMessage(3, LogLevel.Trace, "Released write lock")]
    private static partial void LogReleasedWriteLock(ILogger logger);

    private sealed class SynchronizationDisposable(Action action) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            action();
        }
    }
}