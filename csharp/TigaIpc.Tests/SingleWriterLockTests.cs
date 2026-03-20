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
            ChannelName = name,
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
        var preferredConfiguration = ResolvePreferredConfiguration(AppContext.BaseDirectory);
        var fallbackConfiguration = string.Equals(preferredConfiguration, "Release", StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "Release";
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && baseDir != null; i++)
        {
            var preferredCandidate = Path.Combine(baseDir.FullName, "TigaIpc.TestHost", "bin", preferredConfiguration, "net6.0", "TigaIpc.TestHost.dll");
            if (File.Exists(preferredCandidate))
            {
                return preferredCandidate;
            }

            var fallbackCandidate = Path.Combine(baseDir.FullName, "TigaIpc.TestHost", "bin", fallbackConfiguration, "net6.0", "TigaIpc.TestHost.dll");
            if (File.Exists(fallbackCandidate))
            {
                return fallbackCandidate;
            }

            baseDir = baseDir.Parent;
        }

        throw new FileNotFoundException("Could not locate TigaIpc.TestHost.dll");
    }

    private static string ResolvePreferredConfiguration(string baseDirectory)
    {
        var releaseMarker = $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}";
        if (baseDirectory.Contains(releaseMarker, StringComparison.OrdinalIgnoreCase))
        {
            return "Release";
        }

        var debugMarker = $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}";
        if (baseDirectory.Contains(debugMarker, StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }

        return "Release";
    }
}
