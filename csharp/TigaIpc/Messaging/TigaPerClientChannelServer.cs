using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigaIpc.IO;

namespace TigaIpc.Messaging;

public sealed class TigaPerClientChannelServer : IDisposable, IAsyncDisposable
{
    // Artifacts older than this with no live listener slot are considered orphans.
    private static readonly TimeSpan StaleClientArtifactAge = TimeSpan.FromMinutes(5);

    // Scavenger runs much less frequently than discovery – it is only for crash-residue cleanup.
    private static readonly TimeSpan ScavengeInterval = TimeSpan.FromSeconds(30);

    private readonly string _name;
    private readonly MappingType _mappingType;
    private readonly IOptions<TigaIpcOptions> _options;
    private readonly ILogger<TigaChannel>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, TigaChannel> _clients = new();
    private readonly HashSet<string> _scavengeClaims = new(StringComparer.Ordinal);
    private readonly List<Action<TigaChannel>> _registrations = new();
    private readonly object _registrationLock = new();
    private readonly object _clientLock = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task? _discoveryTask;
    private readonly Task? _scavengeTask;
    private bool _disposed;

    public TigaPerClientChannelServer(
        string name,
        MappingType type = MappingType.Memory,
        IOptions<TigaIpcOptions>? options = null,
        ILogger<TigaChannel>? logger = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Server name must be provided.", nameof(name));
        }

        _name = name;
        _mappingType = type;
        _options = options ?? new OptionsWrapper<TigaIpcOptions>(new TigaIpcOptions());
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_mappingType == MappingType.File)
        {
            var optionsValue = _options.Value ?? new TigaIpcOptions();
            var ipcDirectory = IpcDirectoryHelper.ResolveIpcDirectory(optionsValue);
            var prefix = $"{IpcDirectoryHelper.FilePrefix}{_name}.req.";
            var suffix = IpcDirectoryHelper.StateSuffix;

            try
            {
                // Startup pre-scavenge only removes aged residue. Fresh artifacts may belong
                // to a client that is still initializing and has not published its listener yet.
                ScavengeOnce(
                    ipcDirectory,
                    prefix,
                    suffix,
                    optionsValue,
                    aggressiveUntrackedCleanup: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PerClientScavenge] startup pass failed: {ex.Message}");
            }

            _discoveryTask = Task.Run(DiscoverClientsAsync, _cancellationTokenSource.Token);
            _scavengeTask = Task.Run(ScavengeOrphanArtifactsAsync, _cancellationTokenSource.Token);
        }
    }

    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

    public bool AddClient(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client id must be provided.", nameof(clientId));
        }

        lock (_clientLock)
        {
            if (_clients.ContainsKey(clientId) || _scavengeClaims.Contains(clientId))
            {
                return false;
            }

            var channel = CreateClientChannel(clientId);
            _clients[clientId] = channel;
            return true;
        }
    }

    public bool RemoveClient(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client id must be provided.", nameof(clientId));
        }

        TigaChannel? channel;
        lock (_clientLock)
        {
            if (!_clients.TryRemove(clientId, out channel))
            {
                return false;
            }
        }

        if (channel != null)
        {
            channel.Dispose();
            return true;
        }

        return false;
    }

    internal bool IsClientTracked(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client id must be provided.", nameof(clientId));
        }

        return _clients.ContainsKey(clientId);
    }

    internal void RunScavengeOnce(bool aggressiveUntrackedCleanup = false)
    {
        if (_mappingType != MappingType.File)
        {
            return;
        }

        var optionsValue = _options.Value ?? new TigaIpcOptions();
        var ipcDirectory = IpcDirectoryHelper.ResolveIpcDirectory(optionsValue);
        var prefix = $"{IpcDirectoryHelper.FilePrefix}{_name}.req.";
        var suffix = IpcDirectoryHelper.StateSuffix;

        ScavengeOnce(
            ipcDirectory,
            prefix,
            suffix,
            optionsValue,
            aggressiveUntrackedCleanup);
    }

    public void Register(string method, Func<object?, string> func)
    {
        ApplyRegistration(channel => channel.Register(method, func));
    }

    public void RegisterAsync(string method, Func<Task> func)
    {
        ApplyRegistration(channel => channel.RegisterAsync(method, func));
    }

    public void RegisterAsync(string method, Func<Task<string>> func)
    {
        ApplyRegistration(channel => channel.RegisterAsync(method, func));
    }

    public void RegisterAsync(string method, Func<object?, Task<string>> func)
    {
        ApplyRegistration(channel => channel.RegisterAsync(method, func));
    }

    public void RegisterAsync<TIn>(string method, Func<TIn?, CancellationToken, Task<string>> func)
    {
        ApplyRegistration(channel => channel.RegisterAsync(method, func));
    }

    public void RegisterAsync(string method, Func<object?, CancellationToken, Task<string>> func)
    {
        ApplyRegistration(channel => channel.RegisterAsync(method, func));
    }

    public void RegisterAsync<TOut>(string method, Func<object?, CancellationToken, Task<TOut>> func)
    {
        ApplyRegistration(channel => channel.RegisterAsync(method, func));
    }

    public void RegisterAsync<TIn, TOut>(string method, Func<TIn?, CancellationToken, Task<TOut>> func)
    {
        ApplyRegistration(channel => channel.RegisterAsync(method, func));
    }

    public void RegisterAsync<T>(string method, Func<Task<T>> func)
    {
        ApplyRegistration(channel => channel.RegisterAsync(method, func));
    }

    public void RegisterAsync<T>(string method, Func<object?, Task<T>> func)
    {
        ApplyRegistration(channel => channel.RegisterAsync(method, func));
    }

    public Task PublishAsync(string message, CancellationToken cancellationToken = default)
    {
        return PublishToAllAsync(channel => channel.PublishAsync(message, cancellationToken));
    }

    public Task PublishAsync(BinaryData message, CancellationToken cancellationToken = default)
    {
        return PublishToAllAsync(channel => channel.PublishAsync(message, cancellationToken));
    }

    public Task PublishAsync(IReadOnlyList<BinaryData> messages, CancellationToken cancellationToken = default)
    {
        return PublishToAllAsync(channel => channel.PublishAsync(messages, cancellationToken));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsync(true).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void ApplyRegistration(Action<TigaChannel> registration)
    {
        if (registration == null)
        {
            throw new ArgumentNullException(nameof(registration));
        }

        TigaChannel[] channels;
        lock (_registrationLock)
        {
            _registrations.Add(registration);
        }

        channels = _clients.Values.ToArray();

        foreach (var channel in channels)
        {
            try
            {
                registration(channel);
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                Console.WriteLine("[PerClientRegistration] skipped disposed channel during registration replay");
            }
        }
    }

    private TigaChannel CreateClientChannel(string clientId)
    {
        var requestName = PerClientChannelNames.GetRequestChannelName(_name, clientId);
        var responseName = PerClientChannelNames.GetResponseChannelName(_name, clientId);

        var readFile = TigaMemoryMappedFileFactory.Create(requestName, _mappingType, _options, out _);
        var writeFile = TigaMemoryMappedFileFactory.Create(responseName, _mappingType, _options, out _);

        var channel = new TigaChannel(
            readFile,
            disposeReadFile: true,
            writeFile,
            disposeWriteFile: true,
            _timeProvider,
            _options,
            _logger);

        channel.MessageReceived += OnClientMessageReceived;

        lock (_registrationLock)
        {
            foreach (var registration in _registrations)
            {
                registration(channel);
            }
        }

        return channel;
    }

    private void OnClientMessageReceived(object? sender, MessageReceivedEventArgs e)
    {
        MessageReceived?.Invoke(this, e);
    }

    private async Task PublishToAllAsync(Func<TigaChannel, Task> publish)
    {
        var clients = _clients.Values.ToArray();
        if (clients.Length == 0)
        {
            return;
        }

        var tasks = new Task[clients.Length];
        for (var i = 0; i < clients.Length; i++)
        {
            tasks[i] = publish(clients[i]);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task DiscoverClientsAsync()
    {
        var optionsValue = _options.Value ?? new TigaIpcOptions();
        var ipcDirectory = IpcDirectoryHelper.ResolveIpcDirectory(optionsValue);
        var prefix = $"{IpcDirectoryHelper.FilePrefix}{_name}.req.";
        var suffix = IpcDirectoryHelper.StateSuffix;
        var pollInterval = optionsValue.ClientDiscoveryInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : optionsValue.ClientDiscoveryInterval;
        Console.WriteLine(
            $"[PerClientDiscovery] ipcDirectory={ipcDirectory} prefix={prefix} suffix={suffix} interval={pollInterval.TotalMilliseconds}ms");

        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(ipcDirectory, $"{prefix}*{suffix}"))
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName == null)
                    {
                        continue;
                    }

                    if (!TryExtractClientId(fileName, prefix, suffix, out var clientId))
                    {
                        continue;
                    }

                    try
                    {
                        var added = AddClient(clientId);
                        Console.WriteLine($"[PerClientDiscovery] found={fileName} clientId={clientId} added={added}");
                        if (added && _clients.TryGetValue(clientId, out var channel))
                        {
                            await channel.ReadAsync().ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[PerClientDiscovery] add failed clientId={clientId} error={ex}");
                    }
                }
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // ignore discovery failures to keep the loop alive
                Console.WriteLine($"[PerClientDiscovery] discovery loop error: {ex}");
            }

            try
            {
                await Task.Delay(pollInterval, _cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static bool TryExtractClientId(string fileName, string prefix, string suffix, out string clientId)
    {
        clientId = string.Empty;
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var start = prefix.Length;
        var length = fileName.Length - prefix.Length - suffix.Length;
        if (length <= 0)
        {
            return false;
        }

        clientId = fileName.Substring(start, length);
        return !string.IsNullOrWhiteSpace(clientId);
    }

    // ---------------------------------------------------------------------------
    // Low-frequency orphan scavenger
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Runs every <see cref="ScavengeInterval"/> seconds. For each req.* state file
    /// found on disk, removes stale artifacts for untracked clients and also evicts
    /// tracked clients whose response listener is gone.
    /// </summary>
    private async Task ScavengeOrphanArtifactsAsync()
    {
        var optionsValue = _options.Value ?? new TigaIpcOptions();
        var ipcDirectory = IpcDirectoryHelper.ResolveIpcDirectory(optionsValue);
        var prefix = $"{IpcDirectoryHelper.FilePrefix}{_name}.req.";
        var suffix = IpcDirectoryHelper.StateSuffix;

        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ScavengeInterval, _cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                break;
            }

            try
            {
                ScavengeOnce(
                    ipcDirectory,
                    prefix,
                    suffix,
                    optionsValue,
                    aggressiveUntrackedCleanup: false);
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PerClientScavenge] error: {ex.Message}");
            }
        }
    }

    private void ScavengeOnce(
        string ipcDirectory,
        string prefix,
        string suffix,
        TigaIpcOptions optionsValue,
        bool aggressiveUntrackedCleanup)
    {
        var trackedClientIds = _clients.Keys.ToArray();
        var initialTrackedClientIds = new HashSet<string>(trackedClientIds, StringComparer.Ordinal);
        foreach (var clientId in trackedClientIds)
        {
            TryScavengeTracked(clientId, optionsValue);
        }

        foreach (var file in Directory.EnumerateFiles(ipcDirectory, $"{prefix}*{suffix}"))
        {
            var fileName = Path.GetFileName(file);
            if (fileName == null || !TryExtractClientId(fileName, prefix, suffix, out var clientId))
            {
                continue;
            }

            if (initialTrackedClientIds.Contains(clientId))
            {
                continue;
            }

            if (_clients.ContainsKey(clientId))
            {
                TryScavengeTracked(clientId, optionsValue);
                continue;
            }

            TryScavengeUntracked(clientId, optionsValue, aggressiveUntrackedCleanup);
        }
    }

    private void TryScavengeTracked(string clientId, TigaIpcOptions optionsValue)
    {
        var requestName = PerClientChannelNames.GetRequestChannelName(_name, clientId);
        var responseName = PerClientChannelNames.GetResponseChannelName(_name, clientId);

        if (!ShouldScavengeTrackedClient(requestName, responseName, optionsValue))
        {
            return;
        }

        // The server itself owns the request listener for tracked clients, so the
        // response channel is the signal that the remote client is still alive.
        if (WaitFreeMemoryMappedFile.HasLiveListenerRaw(responseName, optionsValue))
        {
            return;
        }

        if (!TryBeginScavengeClaim(clientId, requireTrackedClient: true))
        {
            return;
        }

        try
        {
            RemoveClient(clientId);
            WaitFreeMemoryMappedFile.DeleteFileArtifacts(requestName, optionsValue);
            WaitFreeMemoryMappedFile.DeleteFileArtifacts(responseName, optionsValue);
            Console.WriteLine($"[PerClientScavenge] removed stale tracked clientId={clientId}");
        }
        finally
        {
            EndScavengeClaim(clientId);
        }
    }

    private void TryScavengeUntracked(
        string clientId,
        TigaIpcOptions optionsValue,
        bool aggressiveUntrackedCleanup)
    {
        var requestName = PerClientChannelNames.GetRequestChannelName(_name, clientId);
        var responseName = PerClientChannelNames.GetResponseChannelName(_name, clientId);

        // Fresh artifact groups may belong to a process that is still starting up and has
        // not published its listener slot yet. Only scavenge aged residue.
        if (!IsArtifactGroupStale(requestName, responseName, optionsValue))
        {
            return;
        }

        if (!TryBeginScavengeClaim(clientId, requireTrackedClient: false))
        {
            return;
        }

        try
        {
            // Only now do the slightly more expensive raw-byte slot scan (one FileStream open,
            // no MemoryMappedFile kernel object).  If any live slot exists, skip.
            if (WaitFreeMemoryMappedFile.HasLiveListenerRaw(responseName, optionsValue) ||
                WaitFreeMemoryMappedFile.HasLiveListenerRaw(requestName, optionsValue))
            {
                return;
            }

            WaitFreeMemoryMappedFile.DeleteFileArtifacts(requestName, optionsValue);
            WaitFreeMemoryMappedFile.DeleteFileArtifacts(responseName, optionsValue);
            Console.WriteLine($"[PerClientScavenge] deleted orphan clientId={clientId}");
        }
        finally
        {
            EndScavengeClaim(clientId);
        }
    }

    private bool ShouldScavengeTrackedClient(
        string requestName,
        string responseName,
        TigaIpcOptions optionsValue)
    {
        if (!AnyArtifactExists(requestName, responseName, optionsValue))
        {
            Console.WriteLine(
                $"[PerClientScavenge] tracked client artifacts missing for request={requestName} response={responseName}; preserving channel");
            return false;
        }

        return IsArtifactGroupStale(requestName, responseName, optionsValue);
    }

    private static bool AnyArtifactExists(
        string requestName,
        string responseName,
        TigaIpcOptions optionsValue)
    {
        foreach (var name in new[] { requestName, responseName })
        {
            foreach (var path in WaitFreeMemoryMappedFile.GetFileArtifactPaths(name, optionsValue))
            {
                try
                {
                    if (File.Exists(path))
                    {
                        return true;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return false;
    }

    private bool TryBeginScavengeClaim(string clientId, bool requireTrackedClient)
    {
        lock (_clientLock)
        {
            if (_scavengeClaims.Contains(clientId))
            {
                return false;
            }

            var isTracked = _clients.ContainsKey(clientId);
            if (requireTrackedClient ? !isTracked : isTracked)
            {
                return false;
            }

            _scavengeClaims.Add(clientId);
            return true;
        }
    }

    private void EndScavengeClaim(string clientId)
    {
        lock (_clientLock)
        {
            _scavengeClaims.Remove(clientId);
        }
    }

    private bool IsArtifactGroupStale(string requestName, string responseName, TigaIpcOptions optionsValue)
    {
        DateTimeOffset? latest = null;
        foreach (var name in new[] { requestName, responseName })
        {
            foreach (var path in WaitFreeMemoryMappedFile.GetFileArtifactPaths(name, optionsValue))
            {
                try
                {
                    var t = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                    if (!latest.HasValue || t > latest.Value)
                    {
                        latest = t;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        if (!latest.HasValue)
        {
            return false;
        }

        return _timeProvider.GetUtcNow() - latest.Value >= StaleClientArtifactAge;
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellationTokenSource.Cancel();

        if (disposing)
        {
            foreach (var task in new[] { _discoveryTask, _scavengeTask })
            {
                if (task != null)
                {
                    try
                    {
                        task.Wait(_options.Value.WaitTimeout);
                    }
                    catch
                    {
                    }
                }
            }

            foreach (var channel in _clients.Values)
            {
                channel.Dispose();
            }

            _clients.Clear();
            _cancellationTokenSource.Dispose();
        }
    }

    private async ValueTask DisposeAsync(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellationTokenSource.Cancel();

        if (disposing)
        {
            foreach (var task in new[] { _discoveryTask, _scavengeTask })
            {
                if (task != null)
                {
                    try
                    {
                        await task.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }

            var buses = _clients.Values.ToArray();
            for (var i = 0; i < buses.Length; i++)
            {
                await buses[i].DisposeAsync().ConfigureAwait(false);
            }

            _clients.Clear();
            _cancellationTokenSource.Dispose();
        }
    }
}
