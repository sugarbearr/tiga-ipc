using MessagePack;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Runtime.Versioning;
using TigaIpc;
using TigaIpc.Core;
using TigaIpc.Messaging;

namespace TigaIpc.Server;

[SupportedOSPlatform("windows")]
class Program
{
    private const string ChannelName = "Excel";

    private static async Task Main(string[] args)
    {
        Console.WriteLine("=== TigaIpc Server Example ===");
        Console.WriteLine($"Channel Name: {ChannelName}");

        var mappingDir = Path.Combine(Path.GetTempPath(), "tiga-ipc");
        Console.WriteLine($"Mapping Directory: {mappingDir}");

        // 1. Configure IPC Options
        var ipcOptions = new TigaIpcOptions
        {
            Name = ChannelName,
            FileMappingDirectory = mappingDir,
        }.WithRobustConfiguration();

        // 2. Initialize Server
        // TigaPerClientServer manages client connections automatically.
        // It listens on the main channel for discovery and creates per-client channels.
        await using var messageBus = new TigaPerClientServer(
            ChannelName,
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
        messageBus.MessageReceived += (_, e) =>
        {
            Console.WriteLine($"[Received] Publish message: {e.Message}");
        };

        // Handle 'invoke' method (Request/Response)
        // Simple string -> string
        messageBus.Register("method", payload =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Invoke] method received: '{payload}'");
            return $"Echo from Server: {payload}";
        });

        // Handle 'GetAllCookie' method (Typed Request/Response)
        // CookieParams -> EventResult
        messageBus.RegisterAsync<CookieParams, EventResult>("GetAllCookie", async (payload, token) =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Invoke] GetAllCookie: {JsonConvert.SerializeObject(payload)}");

            // Simulate work
            await Task.Delay(100, token);

            return new EventResult
            {
                Result = $"Cookie '{payload?.Name ?? "unknown"}' validated for {payload?.RequestUrl}"
            };
        });

        // Handle 'method2' (Async with result)
        messageBus.RegisterAsync<EventResult>("method2", async () =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Invoke] method2 (background task) started...");
            await Task.Delay(500); // Simulate longer work
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Invoke] method2 completed.");
            return new EventResult { Result = "Background work complete" };
        });

        // Handle 'method3' (Async void - no return value expected by caller, but we return Task)
        messageBus.RegisterAsync("method3", async () =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Invoke] method3 (void) started...");
            await Task.Delay(50);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [Invoke] method3 completed.");
        });

        // 4. Background Server Tasks (e.g. Heartbeat)
        var heartbeat = Task.Run(async () =>
        {
            Console.WriteLine("Heartbeat service started.");
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    // This publishes to all connected clients (if supported by TigaPerClientServer implementation)
                    // or just to the logbook if it's a broadcast.
                    await messageBus.PublishAsync($"Server heartbeat {DateTime.UtcNow:O}", cts.Token);
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
}

[MessagePackObject]
public class CookieParams
{
    [Key(0)] public string? Name { get; set; }

    [Key(1)] public string? RequestUrl { get; set; }

    [Key(2)] public int? Timeout { get; set; }
}

public class EventResult
{
    public string Result { get; set; } = string.Empty;
}
