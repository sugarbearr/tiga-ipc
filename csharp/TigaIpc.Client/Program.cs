using System.Diagnostics;
using System.Runtime.Versioning;
using MessagePack;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using TigaIpc;
using TigaIpc.Core;
using TigaIpc.Messaging;

namespace TigaIpc.Client;

[MessagePackObject]
public class ProfileRequest
{
    [Key(0)]
    public string? Name { get; set; }

    [Key(1)]
    public string? RequestUrl { get; set; }

    [Key(2)]
    public int? Timeout { get; set; }
}

[SupportedOSPlatform("windows")]
class Program
{
    private const string DefaultChannelName = "sample";
    private const string EchoMethodName = "echo";
    private const string FetchProfileMethodName = "fetchProfile";
    private const string BackgroundJobMethodName = "backgroundJob";
    private const string NotifyOnlyMethodName = "notifyOnly";

    static async Task Main(string[] args)
    {
        var channelName = ResolveChannelName();
        var ipcDirectory = ResolveIpcDirectory(args);
        if (ipcDirectory == null)
        {
            return;
        }

        var clientId =
            Environment.GetEnvironmentVariable("TIGA_IPC_CLIENT_ID")
            ?? $"{Environment.ProcessId}-{Guid.NewGuid():N}";
        var ipcOptions = new TigaIpcOptions
        {
            ChannelName = channelName,
            IpcDirectory = ipcDirectory,
        }.WithRobustConfiguration();

        var requestName = PerClientChannelNames.GetRequestChannelName(channelName, clientId);
        var responseName = PerClientChannelNames.GetResponseChannelName(channelName, clientId);

        Console.WriteLine($"Initializing TigaIpc Client...");
        Console.WriteLine($"  Channel Name: {channelName}");
        Console.WriteLine($"  Client ID: {clientId}");
        Console.WriteLine($"  Request Channel: {requestName}");
        Console.WriteLine($"  Response Channel: {responseName}");
        Console.WriteLine($"  IPC Directory: {ipcDirectory}");

        await using var channel = new TigaChannel(
            responseName,
            requestName,
            MappingType.File,
            Options.Create(ipcOptions)
        );

        Console.WriteLine("Client ready.");
        PrintHelp();

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // This client reads from its per-client response channel and writes to its request channel.
        // The server-side per-client channel server binds the matching pair for each discovered client.

        try
        {
            while (!cts.IsCancellationRequested)
            {
                Console.Write("> ");
                var line = await Task.Run(() => Console.ReadLine(), cts.Token);

                if (string.IsNullOrWhiteSpace(line))
                    continue;

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
                                var response = await channel.InvokeAsync(
                                    EchoMethodName,
                                    payload,
                                    cancellationToken: cts.Token
                                );
                                sw.Stop();
                                Console.WriteLine($"Response ({sw.ElapsedMilliseconds}ms): {response}");
                                break;
                            }
                        case "profile":
                            {
                                var name = argument ?? "guest";
                                var profileRequest = new ProfileRequest
                                {
                                    Name = name,
                                    RequestUrl = "https://example.com",
                                    Timeout = 5000,
                                };
                                Console.WriteLine($"Requesting profile for: {name}...");
                                var sw = Stopwatch.StartNew();
                                // Note: Server expects fetchProfile with ProfileRequest and returns OperationResult.
                                var response = await channel.InvokeAsync<OperationResult>(
                                    FetchProfileMethodName,
                                    profileRequest,
                                    cancellationToken: cts.Token
                                );
                                sw.Stop();
                                Console.WriteLine(
                                    $"Response ({sw.ElapsedMilliseconds}ms): {response?.Result ?? "<null>"}"
                                );
                                break;
                            }
                        case "publish":
                            {
                                var message = argument ?? "ping";
                                Console.WriteLine($"Publishing: {message}...");
                                await channel.PublishAsync(
                                    $"Client message: {message}",
                                    cancellationToken: cts.Token
                                );
                                Console.WriteLine("Message published.");
                                break;
                            }
                        case "bg": // Test the async background job example.
                            {
                                Console.WriteLine($"Calling {BackgroundJobMethodName}...");
                                var sw = Stopwatch.StartNew();
                                var response = await channel.InvokeAsync<OperationResult>(
                                    BackgroundJobMethodName,
                                    cancellationToken: cts.Token
                                );
                                sw.Stop();
                                Console.WriteLine(
                                    $"Response ({sw.ElapsedMilliseconds}ms): {response?.Result}"
                                );
                                break;
                            }
                        case "void": // Test the async notification example.
                            {
                                Console.WriteLine($"Calling {NotifyOnlyMethodName}...");
                                var sw = Stopwatch.StartNew();
                                await channel.InvokeAsync(
                                    NotifyOnlyMethodName,
                                    cancellationToken: cts.Token
                                );
                                sw.Stop();
                                Console.WriteLine($"Completed ({sw.ElapsedMilliseconds}ms).");
                                break;
                            }
                        case "stress":
                            {
                                int count = 100;
                                if (int.TryParse(argument, out int n))
                                    count = n;
                                Console.WriteLine($"Starting stress test with {count} requests...");
                                var sw = Stopwatch.StartNew();
                                for (int i = 0; i < count; i++)
                                {
                                    if (cts.IsCancellationRequested)
                                        break;
                                    await channel.InvokeAsync(
                                        EchoMethodName,
                                        $"stress-{i}",
                                        cancellationToken: cts.Token
                                    );
                                    if (i % 10 == 0)
                                        Console.Write(".");
                                }
                                sw.Stop();
                                Console.WriteLine();
                                Console.WriteLine(
                                    $"Stress test completed in {sw.ElapsedMilliseconds}ms. Avg: {sw.ElapsedMilliseconds / (double)count}ms/req"
                                );
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
            "IPC directory is required. Pass it as the first argument or set TIGA_IPC_DIRECTORY."
        );
        return null;
    }

    private static string ResolveChannelName()
    {
        var channelName = Environment.GetEnvironmentVariable("TIGA_CHANNEL_NAME");
        return string.IsNullOrWhiteSpace(channelName) ? DefaultChannelName : channelName.Trim();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine($"  invoke <text>   - Call '{EchoMethodName}' with string payload");
        Console.WriteLine(
            $"  profile <name>  - Call '{FetchProfileMethodName}' with structured data"
        );
        Console.WriteLine("  publish <text>  - Send a fire-and-forget message");
        Console.WriteLine(
            $"  bg              - Call '{BackgroundJobMethodName}' (simulates background work)"
        );
        Console.WriteLine($"  void            - Call '{NotifyOnlyMethodName}' (returns void)");
        Console.WriteLine("  stress <count>  - Run <count> invocations sequentially");
        Console.WriteLine("  cls             - Clear screen");
        Console.WriteLine("  help            - Show this help");
        Console.WriteLine("  exit            - Exit the client");
    }
}

public class OperationResult
{
    public string Result { get; set; } = string.Empty;
}
