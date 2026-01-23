using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigaIpc.IO;

namespace TigaIpc.Messaging;

public sealed class TigaPerClientServer : IDisposable, IAsyncDisposable
{
    private readonly string _name;
    private readonly MappingType _mappingType;
    private readonly IOptions<TigaIpcOptions> _options;
    private readonly ILogger<TigaMessageBus>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, TigaMessageBus> _clients = new();
    private readonly List<Action<TigaMessageBus>> _registrations = new();
    private readonly object _registrationLock = new();
    private readonly object _clientLock = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task? _discoveryTask;
    private bool _disposed;

    public TigaPerClientServer(
        string name,
        MappingType type = MappingType.Memory,
        IOptions<TigaIpcOptions>? options = null,
        ILogger<TigaMessageBus>? logger = null,
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
            _discoveryTask = Task.Run(DiscoverClientsAsync, _cancellationTokenSource.Token);
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
            if (_clients.ContainsKey(clientId))
            {
                return false;
            }

            var bus = CreateClientBus(clientId);
            _clients[clientId] = bus;
            return true;
        }
    }

    public bool RemoveClient(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client id must be provided.", nameof(clientId));
        }

        if (_clients.TryRemove(clientId, out var bus))
        {
            bus.Dispose();
            return true;
        }

        return false;
    }

    public void Register(string method, Func<object?, string> func)
    {
        ApplyRegistration(bus => bus.Register(method, func));
    }

    public void RegisterAsync(string method, Func<Task> func)
    {
        ApplyRegistration(bus => bus.RegisterAsync(method, func));
    }

    public void RegisterAsync(string method, Func<Task<string>> func)
    {
        ApplyRegistration(bus => bus.RegisterAsync(method, func));
    }

    public void RegisterAsync(string method, Func<object?, Task<string>> func)
    {
        ApplyRegistration(bus => bus.RegisterAsync(method, func));
    }

    public void RegisterAsync<TIn>(string method, Func<TIn?, CancellationToken, Task<string>> func)
    {
        ApplyRegistration(bus => bus.RegisterAsync(method, func));
    }

    public void RegisterAsync(string method, Func<object?, CancellationToken, Task<string>> func)
    {
        ApplyRegistration(bus => bus.RegisterAsync(method, func));
    }

    public void RegisterAsync<TOut>(string method, Func<object?, CancellationToken, Task<TOut>> func)
    {
        ApplyRegistration(bus => bus.RegisterAsync(method, func));
    }

    public void RegisterAsync<TIn, TOut>(string method, Func<TIn?, CancellationToken, Task<TOut>> func)
    {
        ApplyRegistration(bus => bus.RegisterAsync(method, func));
    }

    public void RegisterAsync<T>(string method, Func<Task<T>> func)
    {
        ApplyRegistration(bus => bus.RegisterAsync(method, func));
    }

    public void RegisterAsync<T>(string method, Func<object?, Task<T>> func)
    {
        ApplyRegistration(bus => bus.RegisterAsync(method, func));
    }

    public Task PublishAsync(string message, CancellationToken cancellationToken = default)
    {
        return PublishToAllAsync(bus => bus.PublishAsync(message, cancellationToken));
    }

    public Task PublishAsync(BinaryData message, CancellationToken cancellationToken = default)
    {
        return PublishToAllAsync(bus => bus.PublishAsync(message, cancellationToken));
    }

    public Task PublishAsync(IReadOnlyList<BinaryData> messages, CancellationToken cancellationToken = default)
    {
        return PublishToAllAsync(bus => bus.PublishAsync(messages, cancellationToken));
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

    private void ApplyRegistration(Action<TigaMessageBus> registration)
    {
        if (registration == null)
        {
            throw new ArgumentNullException(nameof(registration));
        }

        lock (_registrationLock)
        {
            _registrations.Add(registration);
        }

        foreach (var bus in _clients.Values)
        {
            registration(bus);
        }
    }

    private TigaMessageBus CreateClientBus(string clientId)
    {
        var requestName = PerClientChannelNames.GetRequestChannelName(_name, clientId);
        var responseName = PerClientChannelNames.GetResponseChannelName(_name, clientId);

        var readFile = TigaMemoryMappedFileFactory.Create(requestName, _mappingType, _options, out _);
        var writeFile = TigaMemoryMappedFileFactory.Create(responseName, _mappingType, _options, out _);

        var bus = new TigaMessageBus(
            readFile,
            disposeReadFile: true,
            writeFile,
            disposeWriteFile: true,
            _timeProvider,
            _options,
            _logger);

        bus.MessageReceived += OnClientMessageReceived;

        lock (_registrationLock)
        {
            foreach (var registration in _registrations)
            {
                registration(bus);
            }
        }

        return bus;
    }

    private void OnClientMessageReceived(object? sender, MessageReceivedEventArgs e)
    {
        MessageReceived?.Invoke(this, e);
    }

    private async Task PublishToAllAsync(Func<TigaMessageBus, Task> publish)
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
        var baseDirectory = MappingDirectoryHelper.ResolveBaseDirectory(optionsValue);
        var prefix = $"{MappingDirectoryHelper.FilePrefix}{_name}.req.";
        var suffix = MappingDirectoryHelper.StateSuffix;
        var pollInterval = optionsValue.ClientDiscoveryInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : optionsValue.ClientDiscoveryInterval;

        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(baseDirectory, $"{prefix}*{suffix}"))
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

                    AddClient(clientId);
                }
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // ignore discovery failures to keep the loop alive
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
            if (_discoveryTask != null)
            {
                try
                {
                    _discoveryTask.Wait(_options.Value.WaitTimeout);
                }
                catch
                {
                }
            }

            foreach (var bus in _clients.Values)
            {
                bus.Dispose();
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
            if (_discoveryTask != null)
            {
                try
                {
                    await _discoveryTask.ConfigureAwait(false);
                }
                catch
                {
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
