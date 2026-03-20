using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TigaIpc.IO;

internal static class SingleWriterFileLock
{
    private const int LockEx = 2;
    private const int LockNb = 4;

    public static bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static bool TryAcquire(FileStream stream)
    {
        if (stream == null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Single-writer lock is supported only on Unix via flock."
            );
        }

        SafeFileHandle handle = stream.SafeFileHandle;
        if (handle.IsInvalid)
        {
            return false;
        }

        var fd = handle.DangerousGetHandle();
        var result = flock(fd, LockEx | LockNb);
        return result == 0;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(IntPtr fd, int operation);
}
