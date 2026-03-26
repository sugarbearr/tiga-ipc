using Microsoft.Extensions.Options;
using System.Diagnostics;
using TigaIpc;
using TigaIpc.Messaging;
using TigaIpc.IO;

internal class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: <command> <name>");
            return 1;
        }

        var command = args[0];
        var name = args[1];

        switch (command)
        {
            case "hold-reader-lease":
                return HoldReaderLease(name);
            case "publish-burst":
                return PublishBurst(name, args.Length > 2 ? args[2] : null);
            case "hold-single-writer-lock":
                return HoldSingleWriterLock(name);
            case "crash-per-client-file":
                return CrashPerClientFile(name, args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null);
            default:
                Console.Error.WriteLine("Unknown command");
                return 2;
        }

        static int HoldReaderLease(string name)
        {
            var options = new TigaIpcOptions
            {
                ChannelName = name,
                WaitTimeout = TimeSpan.FromSeconds(2),
                ReaderGraceTimeout = TimeSpan.FromSeconds(2),
                MaxFileSize = 64 * 1024,
            };

            using var file = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
            using var lease = file.ReadLease(false);
            Console.WriteLine("ready");
            Console.Out.Flush();
            Thread.Sleep(5000);
            return 0;
        }

        static int PublishBurst(string name, string? countArg)
        {
            var count = 50;
            if (!string.IsNullOrWhiteSpace(countArg) && int.TryParse(countArg, out var parsed))
            {
                count = parsed;
            }

            var options = new TigaIpcOptions
            {
                ChannelName = name,
                WaitTimeout = TimeSpan.FromSeconds(2),
                InvokeTimeout = TimeSpan.FromSeconds(5),
                MinMessageAge = TimeSpan.FromMilliseconds(100),
            };

            using var channel = new TigaChannel(name, MappingType.Memory, new OptionsWrapper<TigaIpcOptions>(options));
            Console.WriteLine("ready");
            Console.Out.Flush();

            for (var i = 0; i < count; i++)
            {
                channel.PublishAsync($"burst-{i}").GetAwaiter().GetResult();
            }

            return 0;
        }

        static int HoldSingleWriterLock(string name)
        {
            var options = new TigaIpcOptions
            {
                ChannelName = name,
                UseSingleWriterLock = true,
                WaitTimeout = TimeSpan.FromSeconds(2),
                MinMessageAge = TimeSpan.FromMilliseconds(100),
            };

            using var file = new WaitFreeMemoryMappedFile(name, MappingType.File, options);
            Console.WriteLine("ready");
            Console.Out.Flush();
            Thread.Sleep(5000);
            return 0;
        }

        static int CrashPerClientFile(string channelName, string? clientId, string? ipcDirectory)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(ipcDirectory))
            {
                Console.Error.WriteLine("Usage: crash-per-client-file <channelName> <clientId> <ipcDirectory>");
                return 1;
            }

            var requestName = PerClientChannelNames.GetRequestChannelName(channelName, clientId);
            var responseName = PerClientChannelNames.GetResponseChannelName(channelName, clientId);
            var options = new TigaIpcOptions
            {
                ChannelName = channelName,
                IpcDirectory = ipcDirectory,
                WaitTimeout = TimeSpan.FromSeconds(2),
                InvokeTimeout = TimeSpan.FromSeconds(5),
                MinMessageAge = TimeSpan.FromMilliseconds(100),
            };

            _ = new TigaChannel(responseName, requestName, MappingType.File, new OptionsWrapper<TigaIpcOptions>(options));
            Console.WriteLine("ready");
            Console.Out.Flush();
            Thread.Sleep(500);
            Process.GetCurrentProcess().Kill(true);
            return 0;
        }
    }
}
