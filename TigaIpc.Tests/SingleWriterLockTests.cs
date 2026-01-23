using System.Diagnostics;
using System.Runtime.InteropServices;
using TigaIpc.IO;
using Xunit;

namespace TigaIpc.Tests;

public class SingleWriterLockTests
{
    [Fact(Timeout = 15000)]
    public void SingleWriterLock_Conflict_Throws_OnUnix()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }

        var name = "single_writer_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            Name = name,
            UseSingleWriterLock = true,
            WaitTimeout = TimeSpan.FromSeconds(2),
        };

        using var process = StartTestHost("hold-single-writer-lock", name);
        WaitForChildReady(process);

        Assert.Throws<InvalidOperationException>(() =>
        {
            using var file = new WaitFreeMemoryMappedFile(name, MappingType.File, options);
        });

        process.WaitForExit(6000);
    }

    private static Process StartTestHost(string command, string name)
    {
        var testHostPath = FindTestHostPath();
        var startInfo = new ProcessStartInfo("dotnet", $"\"{testHostPath}\" {command} {name}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start test host");
    }

    private static void WaitForChildReady(Process process)
    {
        var line = process.StandardOutput.ReadLine();
        if (!string.Equals(line, "ready", StringComparison.OrdinalIgnoreCase))
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"Test host not ready. Output: {line} Error: {error}");
        }
    }

    private static string FindTestHostPath()
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && baseDir != null; i++)
        {
            var candidateDebug = Path.Combine(baseDir.FullName, "TigaIpc.TestHost", "bin", "Debug", "net6.0", "TigaIpc.TestHost.dll");
            if (File.Exists(candidateDebug))
            {
                return candidateDebug;
            }

            var candidateRelease = Path.Combine(baseDir.FullName, "TigaIpc.TestHost", "bin", "Release", "net6.0", "TigaIpc.TestHost.dll");
            if (File.Exists(candidateRelease))
            {
                return candidateRelease;
            }

            baseDir = baseDir.Parent;
        }

        throw new FileNotFoundException("Could not locate TigaIpc.TestHost.dll");
    }
}
