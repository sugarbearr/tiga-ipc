using Microsoft.Extensions.Options;
using TigaIpc.Messaging;
using Xunit;

namespace TigaIpc.Tests;

public class IpcDirectoryTests
{
    [Fact]
    public void IpcDirectory_IsUsed()
    {
        var name = "file_dir_" + Guid.NewGuid().ToString("N");
        var ipcDirectory = Path.Combine(Path.GetTempPath(), "tigaipc_" + Guid.NewGuid().ToString("N"));
        var capturedPath = string.Empty;

        var options = new TigaIpcOptions
        {
            ChannelName = name,
            IpcDirectory = ipcDirectory,
            FileStreamFactory = (path, capacity) =>
            {
                capturedPath = path;
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            },
        };

        using var file = new WaitFreeMemoryMappedFile(name, MappingType.File, options);

        Assert.False(string.IsNullOrWhiteSpace(capturedPath));
        var resolvedIpcDirectory = Path.GetFullPath(ipcDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var resolvedPath = Path.GetFullPath(capturedPath);
        Assert.StartsWith(resolvedIpcDirectory, resolvedPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IpcDirectory_IsRequired_ForFileMappings()
    {
        var name = "file_dir_missing_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            ChannelName = name,
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => new WaitFreeMemoryMappedFile(name, MappingType.File, options));

        Assert.Contains(nameof(TigaIpcOptions.IpcDirectory), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PerClientChannelServer_FileMappingRequiresDirectory()
    {
        var options = Options.Create(new TigaIpcOptions());

        var ex = Assert.Throws<InvalidOperationException>(
            () => new TigaPerClientChannelServer("sample", MappingType.File, options));

        Assert.Contains(nameof(TigaIpcOptions.IpcDirectory), ex.Message, StringComparison.Ordinal);
    }
}
