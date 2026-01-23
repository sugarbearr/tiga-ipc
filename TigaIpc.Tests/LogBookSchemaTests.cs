using MessagePack;
using Microsoft.Extensions.Options;
using TigaIpc.IO;
using TigaIpc.Messaging;
using Xunit;

namespace TigaIpc.Tests;

public class LogBookSchemaTests
{
    [Fact(Timeout = 15000)]
    public async Task LogBookSchemaVersion_EnvelopeRoundTrip()
    {
        var name = "schema_v2_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            Name = name,
            LogBookSchemaVersion = 2,
            AllowLegacyLogBook = true,
            WaitTimeout = TimeSpan.FromSeconds(2),
            InvokeTimeout = TimeSpan.FromSeconds(5),
            MinMessageAge = TimeSpan.FromMilliseconds(100),
        };

        var optionsWrapper = new OptionsWrapper<TigaIpcOptions>(options);
        await using var publisher = new TigaMessageBus(name, MappingType.Memory, optionsWrapper);
        await using var subscriber = new TigaMessageBus(name, MappingType.Memory, optionsWrapper);

        var received = 0;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.MessageReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref received) == 1)
            {
                tcs.TrySetResult(true);
            }
        };

        await publisher.PublishAsync("schema-v2");
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(tcs.Task, completed);
    }

    [Fact]
    public void LogBookSchemaVersion_StrictRejectsLegacy()
    {
        var name = "schema_strict_" + Guid.NewGuid().ToString("N");

        var legacyOptions = new TigaIpcOptions
        {
            Name = name,
            LogBookSchemaVersion = 1,
        };

        using (var legacyFile = new WaitFreeMemoryMappedFile(name, MappingType.Memory, legacyOptions))
        {
            var logBook = new LogBook(1, []);
            using var stream = new MemoryStream();
            MessagePackSerializer.Serialize(stream, logBook);
            stream.Seek(0, SeekOrigin.Begin);
            legacyFile.Write(stream);
        }

        var strictOptions = new TigaIpcOptions
        {
            Name = name,
            LogBookSchemaVersion = 2,
            AllowLegacyLogBook = false,
        };

        Assert.Throws<InvalidOperationException>(() =>
        {
            using var bus = new TigaMessageBus(name, MappingType.Memory, new OptionsWrapper<TigaIpcOptions>(strictOptions));
        });
    }
}
