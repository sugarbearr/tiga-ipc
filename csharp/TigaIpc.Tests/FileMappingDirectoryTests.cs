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
}
