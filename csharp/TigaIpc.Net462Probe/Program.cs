using Microsoft.Extensions.Options;
using TigaIpc;
using TigaIpc.Core;
using TigaIpc.Messaging;

namespace TigaIpc.Net462Probe;

internal static class Program
{
    private const string DefaultChannelName = "excel";
    private const string DefaultClientId = "probe-client";
    private const string MethodName = "echo";

    private static int Main(string[] args)
    {
        try
        {
            var mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "server";
            var ipcDirectory = args.Length > 1
                ? args[1]
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Innodealing", ".ipc-net462-probe");
            var channelName = args.Length > 2 ? args[2] : DefaultChannelName;
            var clientId = args.Length > 3 ? args[3] : DefaultClientId;

            Directory.CreateDirectory(ipcDirectory);

            Console.WriteLine($"mode={mode}");
            Console.WriteLine($"ipcDirectory={ipcDirectory}");
            Console.WriteLine($"channelName={channelName}");
            Console.WriteLine($"clientId={clientId}");

            switch (mode)
            {
                case "server":
                    RunServer(ipcDirectory, channelName);
                    return 0;
                case "client":
                    RunClient(ipcDirectory, channelName, clientId);
                    return 0;
                case "list":
                    DumpDirectory(ipcDirectory);
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown mode: {mode}");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RunServer(string ipcDirectory, string channelName)
    {
        var options = CreateOptions(ipcDirectory, channelName);
        using var server = new TigaPerClientChannelServer(channelName, MappingType.File, options);
        server.Register(MethodName, payload =>
        {
            Console.WriteLine($"server received: {payload}");
            return $"Echo: {payload}";
        });

        Console.WriteLine("server ready");
        DumpDirectory(ipcDirectory);
        Thread.Sleep(TimeSpan.FromSeconds(20));
        Console.WriteLine("server exiting");
    }

    private static void RunClient(string ipcDirectory, string channelName, string clientId)
    {
        var options = CreateOptions(ipcDirectory, channelName);
        var requestName = PerClientChannelNames.GetRequestChannelName(channelName, clientId);
        var responseName = PerClientChannelNames.GetResponseChannelName(channelName, clientId);

        Console.WriteLine($"requestName={requestName}");
        Console.WriteLine($"responseName={responseName}");

        using var channel = new TigaChannel(responseName, requestName, MappingType.File, options);
        Console.WriteLine("client created channel");
        DumpDirectory(ipcDirectory);

        var result = channel.InvokeAsync(MethodName, "ping", TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Console.WriteLine($"client response={result}");
        DumpDirectory(ipcDirectory);
    }

    private static IOptions<TigaIpcOptions> CreateOptions(string ipcDirectory, string channelName)
    {
        var options = new TigaIpcOptions
        {
            ChannelName = channelName,
            IpcDirectory = ipcDirectory,
            WaitTimeout = TimeSpan.FromSeconds(1),
            InvokeTimeout = TimeSpan.FromSeconds(5),
            ClientDiscoveryInterval = TimeSpan.FromMilliseconds(100),
        }.WithRobustConfiguration();

        return Options.Create(options);
    }

    private static void DumpDirectory(string ipcDirectory)
    {
        Console.WriteLine("directory snapshot:");
        foreach (var file in new DirectoryInfo(ipcDirectory).GetFiles().OrderBy(f => f.Name))
        {
            Console.WriteLine($"  {file.Name} {file.Length} {file.LastWriteTime:HH:mm:ss.fff}");
        }
    }
}
