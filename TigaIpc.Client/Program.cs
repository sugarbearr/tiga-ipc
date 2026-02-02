using MessagePack;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Runtime.Versioning;
using TigaIpc;
using TigaIpc.Core;
using TigaIpc.Messaging;

namespace TigaIpc.Client;

[MessagePackObject]
public class CookieParams
{
    [Key(0)] public string? Name { get; set; }

    [Key(1)] public string? RequestUrl { get; set; }

    [Key(2)] public int? Timeout { get; set; }
}

[SupportedOSPlatform("windows")]
class Program
{
    private const string ChannelName = "Excel";

    static async Task Main(string[] args)
    {
        var clientId = Environment.GetEnvironmentVariable("TIGA_IPC_CLIENT_ID") ??
                       $"{Environment.ProcessId}-{Guid.NewGuid():N}";
        var mappingDir = Environment.GetEnvironmentVariable("TIGA_IPC_DIR")
                         ?? Path.Combine(Path.GetTempPath(), "tiga-ipc");
        var ipcOptions = new TigaIpcOptions
        {
            Name = ChannelName,
            FileMappingDirectory = mappingDir,
        }.WithRobustConfiguration();

        var requestName = PerClientChannelNames.GetRequestChannelName(ChannelName, clientId);
        var responseName = PerClientChannelNames.GetResponseChannelName(ChannelName, clientId);

        Console.WriteLine($"Initializing TigaIpc Client...");
        Console.WriteLine($"  Client ID: {clientId}");
        Console.WriteLine($"  Request Channel: {requestName}");
        Console.WriteLine($"  Response Channel: {responseName}");
        Console.WriteLine($"  Mapping Dir: {mappingDir}");

        await using var messageBus = new TigaMessageBus(
            responseName,
            requestName,
            MappingType.File,
            Options.Create(ipcOptions));

        Console.WriteLine("Client ready.");
        PrintHelp();

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Start a background listener for server messages if needed, 
        // but TigaMessageBus handles subscriptions via callbacks usually.
        // If we want to receive broadcasts from server, we should subscribe.
        // The server example sends "Server heartbeat...", let's subscribe to it.
        // However, TigaMessageBus is generally for P2P or Client-Server.
        // If this client acts as a "server" for the "server" to call back, it needs to register handlers.
        // But here TigaMessageBus is initialized with (responseName, requestName).
        // It listens on 'responseName' and writes to 'requestName'.
        // The server listens on 'ChannelName' (which is the main entry) or manages per-client.
        // Wait, TigaPerClientServer manages per-client channels.

        // Let's assume the server publishes heartbeats to the client's response channel?
        // Or does the server publish to a global channel?
        // TigaPerClientServer usually handles requests from clients.
        // The server code: `await messageBus.PublishAsync($"Server heartbeat ...")`
        // If TigaPerClientServer publishes, does it go to all clients?
        // We'll see. For now let's just implement the interactive loop.

        try
        {
            while (!cts.IsCancellationRequested)
            {
                Console.Write("> ");
                var line = await Task.Run(() => Console.ReadLine(), cts.Token);

                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var command = parts[0].ToLowerInvariant();
                var argument = parts.Length > 1 ? parts[1] : null;

                try
                {
                    switch (command)
                    {
                        case "invoke":
                            {
                                var payload = argument ?? "hello";
                                Console.WriteLine($"Sending invoke: {payload}...");
                                var sw = Stopwatch.StartNew();
                                var response = await messageBus.InvokeAsync("method", payload, cancellationToken: cts.Token);
                                sw.Stop();
                                Console.WriteLine($"Response ({sw.ElapsedMilliseconds}ms): {response}");
                                break;
                            }
                        case "cookie":
                            {
                                var name = argument ?? "guest";
                                var cookie = new CookieParams
                                {
                                    Name = name,
                                    RequestUrl = "https://example.com",
                                    Timeout = 5000,
                                };
                                Console.WriteLine($"Requesting cookie for: {name}...");
                                var sw = Stopwatch.StartNew();
                                // Note: Server expects "GetAllCookie" with CookieParams and returns EventResult
                                var response = await messageBus.InvokeAsync<EventResult>(
                                    "GetAllCookie",
                                    cookie,
                                    cancellationToken: cts.Token);
                                sw.Stop();
                                Console.WriteLine($"Response ({sw.ElapsedMilliseconds}ms): {response?.Result ?? "<null>"}");
                                break;
                            }
                        case "publish":
                            {
                                var message = argument ?? "ping";
                                Console.WriteLine($"Publishing: {message}...");
                                await messageBus.PublishAsync($"Client message: {message}", cancellationToken: cts.Token);
                                Console.WriteLine("Message published.");
                                break;
                            }
                        case "bg": // Test method2 which is async on server
                            {
                                Console.WriteLine("Calling method2 (background task)...");
                                var sw = Stopwatch.StartNew();
                                var response = await messageBus.InvokeAsync<EventResult>("method2", cancellationToken: cts.Token);
                                sw.Stop();
                                Console.WriteLine($"Response ({sw.ElapsedMilliseconds}ms): {response?.Result}");
                                break;
                            }
                        case "void": // Test method3 which returns void (Task)
                            {
                                Console.WriteLine("Calling method3 (void)...");
                                var sw = Stopwatch.StartNew();
                                await messageBus.InvokeAsync("method3", cancellationToken: cts.Token);
                                sw.Stop();
                                Console.WriteLine($"Completed ({sw.ElapsedMilliseconds}ms).");
                                break;
                            }
                        case "stress":
                            {
                                int count = 100;
                                if (int.TryParse(argument, out int n)) count = n;
                                Console.WriteLine($"Starting stress test with {count} requests...");
                                var sw = Stopwatch.StartNew();
                                for (int i = 0; i < count; i++)
                                {
                                    if (cts.IsCancellationRequested) break;
                                    await messageBus.InvokeAsync("method", $"stress-{i}", cancellationToken: cts.Token);
                                    if (i % 10 == 0) Console.Write(".");
                                }
                                sw.Stop();
                                Console.WriteLine();
                                Console.WriteLine($"Stress test completed in {sw.ElapsedMilliseconds}ms. Avg: {sw.ElapsedMilliseconds / (double)count}ms/req");
                                break;
                            }
                        case "cls":
                        case "clear":
                            Console.Clear();
                            PrintHelp();
                            break;
                        case "help":
                            PrintHelp();
                            break;
                        case "exit":
                        case "quit":
                            cts.Cancel();
                            break;
                        default:
                            Console.WriteLine($"Unknown command: {command}");
                            PrintHelp();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error executing command '{command}': {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal exit
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex}");
        }

        Console.WriteLine("Bye!");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  invoke <text>   - Call 'method' with string payload");
        Console.WriteLine("  cookie <name>   - Call 'GetAllCookie' with structured data");
        Console.WriteLine("  publish <text>  - Send a fire-and-forget message");
        Console.WriteLine("  bg              - Call 'method2' (simulates background work)");
        Console.WriteLine("  void            - Call 'method3' (returns void)");
        Console.WriteLine("  stress <count>  - Run <count> invocations sequentially");
        Console.WriteLine("  cls             - Clear screen");
        Console.WriteLine("  help            - Show this help");
        Console.WriteLine("  exit            - Exit the client");
    }
}

public class EventResult
{
    public string Result { get; set; } = string.Empty;
}
