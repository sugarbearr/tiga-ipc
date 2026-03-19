using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using TigaIpc.IO;
using Xunit;

namespace TigaIpc.Tests;

public class SecurityFactoryTests
{
    [Fact]
    public void NamedMemoryMappedFileFactory_IsUsed()
    {
        var name = "mmf_factory_" + Guid.NewGuid().ToString("N");
        var invoked = false;

        var options = new TigaIpcOptions
        {
            Name = name,
            NamedMemoryMappedFileFactory = (mapName, capacity) =>
            {
                invoked = true;
                return MemoryMappedFile.CreateOrOpen(mapName, capacity);
            }
        };

        using var file = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
        Assert.True(invoked);
    }

    [Fact]
    public void FileStreamFactory_IsUsed()
    {
        var name = "file_factory_" + Guid.NewGuid().ToString("N");
        var baseDir = Path.Combine(Path.GetTempPath(), "tigaipc_" + Guid.NewGuid().ToString("N"));
        var invoked = false;

        var options = new TigaIpcOptions
        {
            Name = name,
            FileMappingDirectory = baseDir,
            FileStreamFactory = (path, capacity) =>
            {
                invoked = true;
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
        };

        using var file = new WaitFreeMemoryMappedFile(name, MappingType.File, options);
        Assert.True(invoked);
    }

    [Fact]
    public void EventWaitHandleFactory_IsUsed_OnSubscription()
    {
        var name = "event_factory_" + Guid.NewGuid().ToString("N");
        var invoked = false;

        var options = new TigaIpcOptions
        {
            Name = name,
            EventWaitHandleFactory = eventName =>
            {
                invoked = true;
                return new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            }
        };

        using var file = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
        Assert.False(invoked);

        file.FileUpdated += static (_, _) => { };
        Assert.True(invoked);
    }

    [Fact]
    public void EventWaitHandleFactory_Failure_DoesNotLeakNotificationSlots()
    {
        var name = "event_factory_fail_" + Guid.NewGuid().ToString("N");
        var badOptions = new TigaIpcOptions
        {
            Name = name,
            EventWaitHandleFactory = _ => throw new InvalidOperationException("boom"),
        };

        for (var i = 0; i < 140; i++)
        {
            using var badFile = new WaitFreeMemoryMappedFile(name, MappingType.Memory, badOptions);
            Assert.Throws<InvalidOperationException>(() => badFile.FileUpdated += static (_, _) => { });
        }

        var invoked = false;
        var goodOptions = new TigaIpcOptions
        {
            Name = name,
            EventWaitHandleFactory = eventName =>
            {
                invoked = true;
                return new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            },
        };

        using var goodFile = new WaitFreeMemoryMappedFile(name, MappingType.Memory, goodOptions);
        goodFile.FileUpdated += static (_, _) => { };
        Assert.True(invoked);
    }

    [Fact]
    public async Task DeadPartialNotificationSlots_AreReclaimed_OnSubscription()
    {
        var name = "event_partial_slot_" + Guid.NewGuid().ToString("N");
        SeedDeadPartialNotificationSlots(name);

        var options = new TigaIpcOptions
        {
            Name = name,
            WaitTimeout = TimeSpan.FromSeconds(30),
            MaxFileSize = 128 * 1024,
        };

        using var reader = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
        using var writer = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
        var updated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        reader.FileUpdated += (_, _) => updated.TrySetResult(true);

        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4 }, writable: false);
        writer.Write(stream);

        var completed = await Task.WhenAny(updated.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(updated.Task, completed);
        await updated.Task;
    }

    [Fact]
    public void FileBackedMappings_WithSameNameInDifferentDirectories_UseDistinctNotificationEvents()
    {
        var name = "event_scope_" + Guid.NewGuid().ToString("N");
        var baseDir1 = Path.Combine(Path.GetTempPath(), "tigaipc_" + Guid.NewGuid().ToString("N"));
        var baseDir2 = Path.Combine(Path.GetTempPath(), "tigaipc_" + Guid.NewGuid().ToString("N"));
        string? eventName1 = null;
        string? eventName2 = null;

        var options1 = new TigaIpcOptions
        {
            Name = name,
            FileMappingDirectory = baseDir1,
            FileStreamFactory = (path, _) => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite),
            EventWaitHandleFactory = eventName =>
            {
                eventName1 = eventName;
                return new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            },
        };

        var options2 = new TigaIpcOptions
        {
            Name = name,
            FileMappingDirectory = baseDir2,
            FileStreamFactory = (path, _) => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite),
            EventWaitHandleFactory = eventName =>
            {
                eventName2 = eventName;
                return new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            },
        };

        using var file1 = new WaitFreeMemoryMappedFile(name, MappingType.File, options1);
        using var file2 = new WaitFreeMemoryMappedFile(name, MappingType.File, options2);

        file1.FileUpdated += static (_, _) => { };
        file2.FileUpdated += static (_, _) => { };

        Assert.False(string.IsNullOrWhiteSpace(eventName1));
        Assert.False(string.IsNullOrWhiteSpace(eventName2));
        Assert.NotEqual(eventName1, eventName2);
    }

    private static void SeedDeadPartialNotificationSlots(string name)
    {
        var slotCount = GetPrivateConstant<int>("NotificationSlotCount");
        var notificationName = GetPrivateConstant<string>("MemoryPrefix") + name + GetPrivateConstant<string>("NotificationSuffix");
        var deadToken = CreateNotificationSlotToken(int.MaxValue, 0);
        var slotSize = Marshal.SizeOf<NotificationSlotShim>();
        var capacity = slotSize * slotCount;

        using var map = MemoryMappedFile.CreateOrOpen(notificationName, capacity);
        using var accessor = map.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);

        for (var i = 0; i < slotCount; i++)
        {
            var slot = new NotificationSlotShim
            {
                Token = deadToken,
                OwnerProcessStartTimeUtcTicks = 0,
                OwnerProcessId = 0,
                Reserved = 0,
            };

            accessor.Write(i * slotSize, ref slot);
        }

        accessor.Flush();
    }

    private static long CreateNotificationSlotToken(int processId, long processStartTimeUtcTicks)
    {
        var method = typeof(WaitFreeMemoryMappedFile).GetMethod(
            "CreateNotificationSlotToken",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(int), typeof(long) },
            modifiers: null);

        Assert.NotNull(method);
        return (long)method!.Invoke(null, new object[] { processId, processStartTimeUtcTicks })!;
    }

    private static T GetPrivateConstant<T>(string fieldName)
    {
        var field = typeof(WaitFreeMemoryMappedFile).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (T)field!.GetRawConstantValue()!;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotificationSlotShim
    {
        public long Token;
        public long OwnerProcessStartTimeUtcTicks;
        public int OwnerProcessId;
        public int Reserved;
    }
}
