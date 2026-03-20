namespace TigaIpc.IO;

internal static class IpcDirectoryHelper
{
    internal const string FilePrefix = "tiga_";
    internal const string StateSuffix = "_state";

    internal static string ResolveIpcDirectory(TigaIpcOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var ipcDirectory = options.IpcDirectory;
        if (string.IsNullOrWhiteSpace(ipcDirectory))
        {
            throw new InvalidOperationException(
                "File-backed mappings require TigaIpcOptions.IpcDirectory to be configured."
            );
        }

        var resolvedIpcDirectory = Path.GetFullPath(ipcDirectory);
        Directory.CreateDirectory(resolvedIpcDirectory);
        return resolvedIpcDirectory;
    }
}
