using System.Runtime.InteropServices;

namespace TigaIpc.IO;

internal static class MappingDirectoryHelper
{
    internal const string FilePrefix = "DmCommunication_";
    internal const string StateSuffix = "_state";

    internal static string ResolveBaseDirectory(TigaIpcOptions options)
    {
        var baseDirectory = options.FileMappingDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            if ((RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) &&
                Directory.Exists("/dev/shm"))
            {
                baseDirectory = "/dev/shm";
            }
            else
            {
                var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                baseDirectory = Path.Combine(localAppDataPath, "Innodealing", ".cache");
            }
        }

        if (!Directory.Exists(baseDirectory))
        {
            Directory.CreateDirectory(baseDirectory);
        }

        return baseDirectory;
    }
}
