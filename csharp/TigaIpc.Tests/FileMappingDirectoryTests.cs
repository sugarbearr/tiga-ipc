using Microsoft.Extensions.Options;
using TigaIpc.Messaging;
using TigaIpc.IO;
using Xunit;

namespace TigaIpc.Tests;

public class FileMappingDirectoryTests
{
    [Fact]
    public void FileMappingDirectory_IsUsed()
    {
        var name = "file_dir_" + Guid.NewGuid().ToString("N");
        var baseDir = Path.Combine(Path.GetTempPath(), "tigaipc_" + Guid.NewGuid().ToString("N"));
        var capturedPath = string.Empty;

        var options = new TigaIpcOptions
        {
            Name = name,
            FileMappingDirectory = baseDir,
            FileStreamFactory = (path, capacity) =>
            {
                capturedPath = path;
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            },
        };

        using var file = new WaitFreeMemoryMappedFile(name, MappingType.File, options);

        Assert.False(string.IsNullOrWhiteSpace(capturedPath));
        var resolvedBase = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar);
        var resolvedPath = Path.GetFullPath(capturedPath);
        Assert.StartsWith(resolvedBase, resolvedPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileMappingDirectory_IsRequired_ForFileMappings()
    {
        var name = "file_dir_missing_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            Name = name,
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => new WaitFreeMemoryMappedFile(name, MappingType.File, options));

        Assert.Contains(nameof(TigaIpcOptions.FileMappingDirectory), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PerClientServer_FileMappingRequiresDirectory()
    {
        var options = Options.Create(new TigaIpcOptions());

        var ex = Assert.Throws<InvalidOperationException>(
            () => new TigaPerClientServer("sample", MappingType.File, options));

        Assert.Contains(nameof(TigaIpcOptions.FileMappingDirectory), ex.Message, StringComparison.Ordinal);
    }
}
