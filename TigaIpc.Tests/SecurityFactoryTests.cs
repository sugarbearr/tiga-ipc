using System.IO.MemoryMappedFiles;
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
        var invoked = false;

        var options = new TigaIpcOptions
        {
            Name = name,
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
    public void EventWaitHandleFactory_IsUsed()
    {
        var name = "event_factory_" + Guid.NewGuid().ToString("N");
        var invoked = false;

        var options = new TigaIpcOptions
        {
            Name = name,
            EventWaitHandleFactory = eventName =>
            {
                invoked = true;
                return new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            }
        };

        using var file = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
        Assert.True(invoked);
    }
}
