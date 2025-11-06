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
    private const string ChannelName = "Excel";

    private static async Task Main(string[] args)
    {
        Console.WriteLine("TigaIpc server starting...");

        var ipcOptions = new TigaIpcOptions { Name = ChannelName }.WithRobustConfiguration();
        await using var messageBus = new TigaMessageBus(
            ChannelName,
            MappingType.Memory,
            Options.Create(ipcOptions));

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        messageBus.MessageReceived += (_, e) =>
        {
            Console.WriteLine($"[Publish] {e.Message}");
        };

        messageBus.Register("method", payload =>
        {
            Console.WriteLine($"[Invoke] method => {payload}");
            return $"Echo: {payload}";
        });

        messageBus.RegisterAsync<CookieParams, EventResult>("GetAllCookie", async (payload, token) =>
        {
            Console.WriteLine($"[Invoke] GetAllCookie => {JsonConvert.SerializeObject(payload)}");
            await Task.Delay(100, token);
            return new EventResult { Result = $"Cookie {payload?.Name ?? "unknown"} received" };
        });

        messageBus.RegisterAsync<EventResult>("method2", async () =>
        {
            Console.WriteLine("[Invoke] method2");
            await Task.Delay(50);
            return new EventResult { Result = "Background work complete" };
        });

        messageBus.RegisterAsync("method3", async () =>
        {
            Console.WriteLine("[Invoke] method3");
            await Task.Delay(50);
        });

        var heartbeat = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await messageBus.PublishAsync($"Server heartbeat {DateTime.UtcNow:O}", cts.Token);
                await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
            }
        }, cts.Token);

        Console.WriteLine("Server ready. Press Ctrl+C to exit.");

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