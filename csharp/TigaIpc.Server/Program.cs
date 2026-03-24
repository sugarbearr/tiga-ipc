using MessagePack;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Runtime.Versioning;
using TigaIpc.Core;
using TigaIpc.Messaging;

namespace TigaIpc.Server;

[SupportedOSPlatform("windows")]
class Program
{
    private const string DefaultChannelName = "sample";
    private const string EchoMethodName = "echo";
    private const string FetchProfileMethodName = "fetchProfile";
    private const string BackgroundJobMethodName = "backgroundJob";
    private const string NotifyOnlyMethodName = "notifyOnly";

    private static async Task Main(string[] args)
    {
        var channelName = ResolveChannelName();
        var ipcDirectory = ResolveIpcDirectory(args);
        if (ipcDirectory == null)
        {
            return;
        }

        Console.WriteLine("=== TigaIpc Server Example ===");
        Console.WriteLine($"Channel Name: {channelName}");
        Console.WriteLine($"IPC Directory: {ipcDirectory}");

        // 1. Configure IPC Options
        var ipcOptions = new TigaIpcOptions
        {
            ChannelName = channelName,
            IpcDirectory = ipcDirectory,
            LogBookSchemaVersion = 1,
            AllowLegacyLogBook = true,
        }.WithRobustConfiguration();

        // 2. Initialize Server
        // TigaPerClientChannelServer manages client connections automatically.
        // It listens on the main channel for discovery and creates per-client channels.
        await using var server = new TigaPerClientChannelServer(
            channelName,
            MappingType.File,
            Options.Create(ipcOptions));

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Console.WriteLine("Stopping server...");
            cts.Cancel();
        };

        // 3. Register Message Handlers

        // Handle fire-and-forget messages (Publish)
        server.MessageReceived += (_, e) =>
        {
            Console.WriteLine($"[Received] Publish message: {e.Message}");
        };

        // Handle 'invoke' method (Request/Response)
        // Simple string -> string
        server.Register(EchoMethodName, payload =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Invoke] {EchoMethodName} received: '{payload}'");
            return $"Echo response: {payload}";
        });

        // Handle a typed request/response example.
        // ProfileRequest -> OperationResult
        server.RegisterAsync<ProfileRequest, OperationResult>(FetchProfileMethodName, async (payload, token) =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Invoke] {FetchProfileMethodName}: {JsonConvert.SerializeObject(payload)}");

            // Simulate work
            await Task.Delay(100, token);

            return new OperationResult
            {
                Result = $"Profile '{payload?.Name ?? "unknown"}' prepared for {payload?.RequestUrl}"
            };
        });

        // Handle an async request with a typed result.
        server.RegisterAsync<OperationResult>(BackgroundJobMethodName, async () =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Invoke] {BackgroundJobMethodName} started...");
            await Task.Delay(500); // Simulate longer work
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Invoke] {BackgroundJobMethodName} completed.");
            return new OperationResult { Result = "Background work complete" };
        });

        // Handle an async notification with no payload/result contract.
        server.RegisterAsync(NotifyOnlyMethodName, async () =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Invoke] {NotifyOnlyMethodName} started...");
            await Task.Delay(50);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Invoke] {NotifyOnlyMethodName} completed.");
        });

        // 4. Background Server Tasks (e.g. Heartbeat)
        var heartbeat = Task.Run(async () =>
        {
            Console.WriteLine("Heartbeat service started.");
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    // This publishes to all connected clients managed by the per-client channel server
                    // or just to the logbook if it's a broadcast.
                    await server.PublishAsync($"Server heartbeat {DateTime.UtcNow:O}", cts.Token);
                    await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"Heartbeat error: {ex.Message}");
                }
            }
            Console.WriteLine("Heartbeat service stopped.");
        }, cts.Token);

        Console.WriteLine("Server ready. Press Ctrl+C to exit.");

        // Keep running until cancelled
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await heartbeat;
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("Server stopped.");
    }

    private static string? ResolveIpcDirectory(string[] args)
    {
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            return args[0];
        }

        var ipcDirectory = Environment.GetEnvironmentVariable("TIGA_IPC_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(ipcDirectory))
        {
            return ipcDirectory;
        }

        Console.Error.WriteLine(
            "IPC directory is required. Pass it as the first argument or set TIGA_IPC_DIRECTORY.");
        return null;
    }

    private static string ResolveChannelName()
    {
        var channelName = Environment.GetEnvironmentVariable("TIGA_CHANNEL_NAME");
        return string.IsNullOrWhiteSpace(channelName) ? DefaultChannelName : channelName.Trim();
    }
}

[MessagePackObject]
public class ProfileRequest
{
    [Key(0)] public string? Name { get; set; }

    [Key(1)] public string? RequestUrl { get; set; }

    [Key(2)] public int? Timeout { get; set; }
}

public class OperationResult
{
    public string Result { get; set; } = string.Empty;
}
