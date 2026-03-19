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

    [Fact]
    public async Task FileUpdated_WakesAllReaders_WithoutTimeoutFallback()
    {
        var name = "mmf_notify_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            Name = name,
            WaitTimeout = TimeSpan.FromSeconds(30),
            MaxFileSize = 128 * 1024,
        };

        using var reader1 = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
        using var reader2 = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
        using var writer = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);

        var reader1Updated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader2Updated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        reader1.FileUpdated += static (_, _) => { };
        reader2.FileUpdated += static (_, _) => { };
        reader1.FileUpdated += (_, _) => reader1Updated.TrySetResult(true);
        reader2.FileUpdated += (_, _) => reader2Updated.TrySetResult(true);

        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4 }, writable: false);
        writer.Write(stream);

        var allReadersUpdated = Task.WhenAll(reader1Updated.Task, reader2Updated.Task);
        var completed = await Task.WhenAny(allReadersUpdated, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(allReadersUpdated, completed);
        await allReadersUpdated;
    }

    [Fact]
    public async Task WriterOnlyInstances_DoNotConsumeNotificationSlots()
    {
        var name = "mmf_writer_only_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            Name = name,
            WaitTimeout = TimeSpan.FromSeconds(30),
            MaxFileSize = 128 * 1024,
        };

        var writerOnlyFiles = new List<WaitFreeMemoryMappedFile>();
        try
        {
            for (var i = 0; i < 140; i++)
            {
                writerOnlyFiles.Add(new WaitFreeMemoryMappedFile(name, MappingType.Memory, options));
            }

            using var reader = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
            using var writer = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
            var updated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            reader.FileUpdated += (_, _) => updated.TrySetResult(true);

            using var stream = new MemoryStream(new byte[] { 5, 6, 7, 8 }, writable: false);
            writer.Write(stream);

            var completed = await Task.WhenAny(updated.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(updated.Task, completed);
            await updated.Task;
        }
        finally
        {
            foreach (var writerOnlyFile in writerOnlyFiles)
            {
                writerOnlyFile.Dispose();
            }
        }
    }

    [Fact]
    public async Task NotificationSlots_AreReused_AfterDispose()
    {
        var name = "mmf_slot_reuse_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            Name = name,
            WaitTimeout = TimeSpan.FromSeconds(30),
            MaxFileSize = 128 * 1024,
        };

        for (var i = 0; i < 140; i++)
        {
            using var reader = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
            reader.FileUpdated += static (_, _) => { };
        }

        using var activeReader = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
        using var writer = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
        var updated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        activeReader.FileUpdated += (_, _) => updated.TrySetResult(true);

        using var stream = new MemoryStream(new byte[] { 9, 10, 11, 12 }, writable: false);
        writer.Write(stream);

        var completed = await Task.WhenAny(updated.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(updated.Task, completed);
        await updated.Task;
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
