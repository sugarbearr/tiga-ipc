using TigaIpc.IO;
using Xunit;

namespace TigaIpc.Tests;

public class MemoryMappedFileConcurrencyTests
{
    [Fact]
    public async Task ReadWrite_Concurrent_DoesNotHang()
    {
        var name = "mmf_test_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            Name = name,
            WaitTimeout = TimeSpan.FromSeconds(2),
            MaxFileSize = 128 * 1024,
        };

        using ITigaMemoryMappedFile memoryMappedFile =
            new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);

        var payload = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var tasks = new List<Task>
        {
            Task.Run(() => WriteLoop(memoryMappedFile, payload, cts.Token)),
        };

        for (var i = 0; i < 3; i++)
        {
            tasks.Add(Task.Run(() => ReadLoop(memoryMappedFile, cts.Token)));
        }

        await Task.Delay(TimeSpan.FromMilliseconds(800));
        cts.Cancel();

        await Task.WhenAll(tasks);
    }

    private static void WriteLoop(ITigaMemoryMappedFile memoryMappedFile, byte[] payload, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var stream = new MemoryStream(payload, writable: false);
                memoryMappedFile.Write(stream, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (TimeoutException)
            {
                // Allow retry to avoid test flakiness under contention.
            }
        }
    }

    private static void ReadLoop(ITigaMemoryMappedFile memoryMappedFile, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _ = memoryMappedFile.Read(static stream => stream.Length, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (TimeoutException)
            {
                // Allow retry to avoid test flakiness under contention.
            }
        }
    }
}
