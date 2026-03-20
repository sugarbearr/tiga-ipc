using Microsoft.Extensions.Options;
using TigaIpc.Messaging;
using Xunit;

namespace TigaIpc.Tests;

public class MessageBusConcurrencyTests
{
    [Fact(Timeout = 15000)]
    public async Task PublishReceive_MultiInstance_Concurrent()
    {
        var name = "ipc_test_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            ChannelName = name,
            WaitTimeout = TimeSpan.FromSeconds(2),
            InvokeTimeout = TimeSpan.FromSeconds(5),
            MinMessageAge = TimeSpan.FromMilliseconds(100),
            MaxFileSize = 1024 * 1024,
        };
        var optionsWrapper = new OptionsWrapper<TigaIpcOptions>(options);

        await using var publisher = new TigaMessageBus(name, MappingType.Memory, optionsWrapper);
        await using var subscriber = new TigaMessageBus(name, MappingType.Memory, optionsWrapper);

        const int expected = 200;
        var received = 0;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        subscriber.MessageReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref received) == expected)
            {
                tcs.TrySetResult(true);
            }
        };

        var publishTasks = Enumerable.Range(0, expected)
            .Select(i => publisher.PublishAsync($"msg-{i}"))
            .ToArray();

        await Task.WhenAll(publishTasks);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(tcs.Task, completed);
        Assert.Equal(expected, received);
    }
}
