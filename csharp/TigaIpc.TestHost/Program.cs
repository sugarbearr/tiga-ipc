using System.Threading;
using Microsoft.Extensions.Options;
using TigaIpc;
using TigaIpc.Messaging;
using TigaIpc.IO;

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
    default:
        Console.Error.WriteLine("Unknown command");
        return 2;
}

static int HoldReaderLease(string name)
{
    var options = new TigaIpcOptions
    {
        Name = name,
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
        Name = name,
        WaitTimeout = TimeSpan.FromSeconds(2),
        InvokeTimeout = TimeSpan.FromSeconds(5),
        MinMessageAge = TimeSpan.FromMilliseconds(100),
    };

    using var bus = new TigaMessageBus(name, MappingType.Memory, new OptionsWrapper<TigaIpcOptions>(options));
    Console.WriteLine("ready");
    Console.Out.Flush();

    for (var i = 0; i < count; i++)
    {
        bus.PublishAsync($"burst-{i}").GetAwaiter().GetResult();
    }

    return 0;
}

static int HoldSingleWriterLock(string name)
{
    var options = new TigaIpcOptions
    {
        Name = name,
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
