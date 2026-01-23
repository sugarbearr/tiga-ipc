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
        var mappingDir = Path.Combine(Path.GetTempPath(), "tiga-ipc");
        var ipcOptions = new TigaIpcOptions
        {
            Name = ChannelName,
            FileMappingDirectory = mappingDir,
        }.WithRobustConfiguration();
        var requestName = PerClientChannelNames.GetRequestChannelName(ChannelName, clientId);
        var responseName = PerClientChannelNames.GetResponseChannelName(ChannelName, clientId);
        await using var messageBus = new TigaMessageBus(
            responseName,
            requestName,
            MappingType.File,
            Options.Create(ipcOptions));

        Console.WriteLine($"TigaIpc client ready (wait-free mode, clientId={clientId}).");
        PrintHelp();

        while (true)
        {
            var processId = Environment.ProcessId;
            Thread.Sleep(1000);
            var line = $"invoke Halo{processId}!";
            if (line is null)
            {
                continue;
            }

            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0].ToLowerInvariant())
            {
                case "invoke":
                    {
                        var payload = parts.Length > 1 ? parts[1] : "hello";
                        var response = await messageBus.InvokeAsync("method", payload);
                        Console.WriteLine($"[{DateTime.Now:yyyy/MM/dd HH:mm:ss:fff}] Response: {response}");
                        break;
                    }
                case "cookie":
                    {
                        var name = parts.Length > 1 ? parts[1] : "guest";
                        var cookie = new CookieParams
                        {
                            Name = name,
                            RequestUrl = "https://example.com",
                            Timeout = 5000,
                        };

                        var response = await messageBus.PublishAsync<EventResult>(
                            "GetAllCookie",
                            JsonConvert.SerializeObject(cookie));

                        Console.WriteLine($"Cookie response: {response?.Result ?? "<null>"}");
                        break;
                    }
                case "publish":
                    {
                        var message = parts.Length > 1 ? parts[1] : "ping";
                        await messageBus.PublishAsync($"Client message: {message}");
                        Console.WriteLine("Message published.");
                        break;
                    }
                case "help":
                    PrintHelp();
                    break;
                case "exit":
                    Console.WriteLine("Bye!");
                    return;
                default:
                    Console.WriteLine("Unknown command. Type 'help' for options.");
                    break;
            }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Commands: invoke <text>, cookie <name>, publish <text>, help, exit");
    }
}

public class EventResult
{
    public string Result { get; set; } = string.Empty;
}
