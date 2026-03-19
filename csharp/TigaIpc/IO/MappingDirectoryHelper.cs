namespace TigaIpc.IO;

internal static class MappingDirectoryHelper
{
    internal const string FilePrefix = "tiga_";
    internal const string StateSuffix = "_state";

    internal static string ResolveBaseDirectory(TigaIpcOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var baseDirectory = options.FileMappingDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new InvalidOperationException(
                "File-backed mappings require TigaIpcOptions.FileMappingDirectory to be configured.");
        }

        var resolvedBaseDirectory = Path.GetFullPath(baseDirectory);
        Directory.CreateDirectory(resolvedBaseDirectory);
        return resolvedBaseDirectory;
    }
}
