using System.Diagnostics;
using TigaIpc.IO;
using Xunit;

namespace TigaIpc.Tests;

public class ProcessCrashInjectionTests
{
    [Fact(Timeout = 15000)]
    public void WaitFree_ReaderGraceReset_Counts()
    {
        var name = "reader_reset_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            Name = name,
            WaitTimeout = TimeSpan.FromSeconds(2),
            ReaderGraceTimeout = TimeSpan.FromMilliseconds(200),
            WriterSleepDuration = TimeSpan.FromMilliseconds(5),
            MaxFileSize = 64 * 1024,
        };

        using var file = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
        using var seedStream = new MemoryStream(new byte[128], writable: false);
        file.Write(seedStream);

        using var process = StartTestHost("hold-reader-lease", name);
        WaitForChildReady(process);

        using var updateStream = new MemoryStream(new byte[128], writable: false);
        file.Write(updateStream);
        file.Write(updateStream);

        var metrics = file.GetSynchronizationMetrics();
        Assert.True(metrics.ReaderGraceResets > 0);

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

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start test host");
        return process;
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
