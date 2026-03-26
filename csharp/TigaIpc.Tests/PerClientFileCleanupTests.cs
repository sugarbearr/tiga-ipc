using System.Diagnostics;
using Microsoft.Extensions.Options;
using TigaIpc.IO;
using TigaIpc.Messaging;
using Xunit;

namespace TigaIpc.Tests;

public class PerClientFileCleanupTests
{
    private static readonly string[] ArtifactSuffixes =
    {
        "_state",
        "_notify",
        "_data_0",
        "_data_1",
    };

    [Fact]
    public async Task StartupScavenge_PreservesFreshUntrackedFileArtifacts_UntilTheyAgeOut()
    {
        var name = $"per-client-cleanup-{Guid.NewGuid():N}";
        var clientId = $"client-{Guid.NewGuid():N}";
        var ipcDirectory = CreateTempDirectory();

        var options = new TigaIpcOptions
        {
            ChannelName = name,
            IpcDirectory = ipcDirectory,
            ClientDiscoveryInterval = TimeSpan.FromSeconds(5),
            WaitTimeout = TimeSpan.FromSeconds(1),
        };
        var optionsWrapper = new OptionsWrapper<TigaIpcOptions>(options);
        var requestName = PerClientChannelNames.GetRequestChannelName(name, clientId);
        var responseName = PerClientChannelNames.GetResponseChannelName(name, clientId);

        try
        {
            CreateFileArtifacts(requestName, options);
            CreateFileArtifacts(responseName, options);
            SetArtifactsLastWriteTimeUtc(requestName, options, DateTime.UtcNow);
            SetArtifactsLastWriteTimeUtc(responseName, options, DateTime.UtcNow);
            Assert.True(AnyArtifactsExist(requestName, options));
            Assert.True(AnyArtifactsExist(responseName, options));

            await using var server = new TigaPerClientChannelServer(name, MappingType.File, optionsWrapper);

            var preserved = await WaitForConditionAsync(
                () => AnyArtifactsExist(requestName, options) &&
                      AnyArtifactsExist(responseName, options),
                TimeSpan.FromSeconds(1));

            Assert.True(preserved);
            Assert.False(server.IsClientTracked(clientId));

            SetArtifactsLastWriteTimeUtc(requestName, options, DateTime.UtcNow.AddMinutes(-10));
            SetArtifactsLastWriteTimeUtc(responseName, options, DateTime.UtcNow.AddMinutes(-10));

            server.RunScavengeOnce();

            var cleaned = await WaitForConditionAsync(
                () => !AnyArtifactsExist(requestName, options) &&
                      !AnyArtifactsExist(responseName, options),
                TimeSpan.FromSeconds(1));

            Assert.True(cleaned);
        }
        finally
        {
            TryDeleteDirectory(ipcDirectory);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task StartupScavenge_RemovesDeadChildProcessFileArtifacts_AfterTheyAgeOut()
    {
        var name = $"per-client-child-cleanup-{Guid.NewGuid():N}";
        var clientId = $"client-{Guid.NewGuid():N}";
        var ipcDirectory = CreateTempDirectory();

        var options = new TigaIpcOptions
        {
            ChannelName = name,
            IpcDirectory = ipcDirectory,
            ClientDiscoveryInterval = TimeSpan.FromMilliseconds(50),
            WaitTimeout = TimeSpan.FromSeconds(1),
            InvokeTimeout = TimeSpan.FromSeconds(2),
        };
        var optionsWrapper = new OptionsWrapper<TigaIpcOptions>(options);
        var requestName = PerClientChannelNames.GetRequestChannelName(name, clientId);
        var responseName = PerClientChannelNames.GetResponseChannelName(name, clientId);

        try
        {
            using var process = StartTestHost("crash-per-client-file", name, clientId, ipcDirectory);
            WaitForChildReady(process);
            Assert.True(process.WaitForExit(5000), "Crash test host did not exit in time.");

            var artifactsCreated = await WaitForConditionAsync(
                () => AnyArtifactsExist(requestName, options) &&
                      AnyArtifactsExist(responseName, options),
                TimeSpan.FromSeconds(2));

            Assert.True(artifactsCreated);
            SetArtifactsLastWriteTimeUtc(requestName, options, DateTime.UtcNow.AddMinutes(-10));
            SetArtifactsLastWriteTimeUtc(responseName, options, DateTime.UtcNow.AddMinutes(-10));

            await using var server = new TigaPerClientChannelServer(name, MappingType.File, optionsWrapper);

            var cleaned = await WaitForConditionAsync(
                () => !AnyArtifactsExist(requestName, options) &&
                      !AnyArtifactsExist(responseName, options),
                TimeSpan.FromSeconds(1));

            Assert.True(cleaned);
            Assert.False(server.IsClientTracked(clientId));
        }
        finally
        {
            TryDeleteDirectory(ipcDirectory);
        }
    }

    [Fact]
    public async Task StartupScavenge_PreservesLiveClientArtifacts_AndDiscoveryStillConnects()
    {
        var name = $"per-client-active-{Guid.NewGuid():N}";
        var clientId = $"client-{Guid.NewGuid():N}";
        var ipcDirectory = CreateTempDirectory();

        var options = new TigaIpcOptions
        {
            ChannelName = name,
            IpcDirectory = ipcDirectory,
            ClientDiscoveryInterval = TimeSpan.FromMilliseconds(50),
            WaitTimeout = TimeSpan.FromSeconds(1),
            InvokeTimeout = TimeSpan.FromSeconds(2),
        };
        var optionsWrapper = new OptionsWrapper<TigaIpcOptions>(options);
        var requestName = PerClientChannelNames.GetRequestChannelName(name, clientId);
        var responseName = PerClientChannelNames.GetResponseChannelName(name, clientId);

        try
        {
            await using var client = new TigaChannel(
                responseName,
                requestName,
                MappingType.File,
                optionsWrapper);

            SetArtifactsLastWriteTimeUtc(requestName, options, DateTime.UtcNow.AddMinutes(-10));
            SetArtifactsLastWriteTimeUtc(responseName, options, DateTime.UtcNow.AddMinutes(-10));

            await using var server = new TigaPerClientChannelServer(name, MappingType.File, optionsWrapper);
            server.Register("method", payload => $"Echo: {payload}");

            var result = await client.InvokeAsync("method", "ping", TimeSpan.FromSeconds(2));

            Assert.Equal("Echo: ping", result);
            Assert.True(AllArtifactsExist(requestName, options));
            Assert.True(AllArtifactsExist(responseName, options));
            Assert.True(server.IsClientTracked(clientId));
        }
        finally
        {
            TryDeleteDirectory(ipcDirectory);
        }
    }

    [Fact]
    public async Task ScavengeOnce_RemovesStaleTrackedClientArtifacts_AfterResponseListenerDisappears()
    {
        var name = $"per-client-disconnect-{Guid.NewGuid():N}";
        var clientId = $"client-{Guid.NewGuid():N}";
        var ipcDirectory = CreateTempDirectory();

        var options = new TigaIpcOptions
        {
            ChannelName = name,
            IpcDirectory = ipcDirectory,
            ClientDiscoveryInterval = TimeSpan.FromMilliseconds(50),
            WaitTimeout = TimeSpan.FromSeconds(1),
            InvokeTimeout = TimeSpan.FromSeconds(2),
        };
        var optionsWrapper = new OptionsWrapper<TigaIpcOptions>(options);
        var requestName = PerClientChannelNames.GetRequestChannelName(name, clientId);
        var responseName = PerClientChannelNames.GetResponseChannelName(name, clientId);

        try
        {
            await using var server = new TigaPerClientChannelServer(name, MappingType.File, optionsWrapper);
            server.Register("method", payload => $"Echo: {payload}");
            server.AddClient(clientId);

            await using (var client = new TigaChannel(
                responseName,
                requestName,
                MappingType.File,
                optionsWrapper))
            {
                var result = await client.InvokeAsync("method", "disconnect", TimeSpan.FromSeconds(2));
                Assert.Equal("Echo: disconnect", result);
            }

            SetArtifactsLastWriteTimeUtc(requestName, options, DateTime.UtcNow.AddMinutes(-10));
            SetArtifactsLastWriteTimeUtc(responseName, options, DateTime.UtcNow.AddMinutes(-10));

            server.RunScavengeOnce();

            var cleaned = await WaitForConditionAsync(
                () => !AnyArtifactsExist(requestName, options) &&
                      !AnyArtifactsExist(responseName, options),
                TimeSpan.FromSeconds(1));

            Assert.True(cleaned);
            Assert.False(server.IsClientTracked(clientId));
        }
        finally
        {
            TryDeleteDirectory(ipcDirectory);
        }
    }

    [Fact]
    public async Task ScavengeOnce_PreservesTrackedClient_WhenArtifactsAlreadyDisappear()
    {
        var name = $"per-client-missing-{Guid.NewGuid():N}";
        var clientId = $"client-{Guid.NewGuid():N}";
        var ipcDirectory = CreateTempDirectory();

        var options = new TigaIpcOptions
        {
            ChannelName = name,
            IpcDirectory = ipcDirectory,
            ClientDiscoveryInterval = TimeSpan.FromMilliseconds(50),
            WaitTimeout = TimeSpan.FromSeconds(1),
            FileStreamFactory = CreateDeleteSharingFileStream,
        };
        var optionsWrapper = new OptionsWrapper<TigaIpcOptions>(options);
        var requestName = PerClientChannelNames.GetRequestChannelName(name, clientId);
        var responseName = PerClientChannelNames.GetResponseChannelName(name, clientId);

        try
        {
            await using var server = new TigaPerClientChannelServer(name, MappingType.File, optionsWrapper);
            Assert.True(server.AddClient(clientId));

            WaitFreeMemoryMappedFile.DeleteFileArtifacts(requestName, options);
            WaitFreeMemoryMappedFile.DeleteFileArtifacts(responseName, options);

            var deleted = await WaitForConditionAsync(
                () => !AnyArtifactsExist(requestName, options) &&
                      !AnyArtifactsExist(responseName, options),
                TimeSpan.FromSeconds(1));

            Assert.True(deleted);

            server.RunScavengeOnce();

            Assert.True(server.IsClientTracked(clientId));
        }
        finally
        {
            TryDeleteDirectory(ipcDirectory);
        }
    }

    private static string CreateTempDirectory()
    {
        var ipcDirectory = Path.Combine(Path.GetTempPath(), $"tiga-ipc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ipcDirectory);
        return ipcDirectory;
    }

    private static void CreateFileArtifacts(string channelName, TigaIpcOptions options)
    {
        using var _ = new WaitFreeMemoryMappedFile(channelName, MappingType.File, options);
    }

    private static FileStream CreateDeleteSharingFileStream(string path, long _)
    {
        return new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
    }

    private static void SetArtifactsLastWriteTimeUtc(
        string channelName,
        TigaIpcOptions options,
        DateTime lastWriteTimeUtc)
    {
        foreach (var artifactPath in GetArtifactPaths(channelName, options))
        {
            if (File.Exists(artifactPath))
            {
                File.SetLastWriteTimeUtc(artifactPath, lastWriteTimeUtc);
            }
        }
    }

    private static bool AnyArtifactsExist(string channelName, TigaIpcOptions options)
    {
        return GetArtifactPaths(channelName, options).Any(File.Exists);
    }

    private static bool AllArtifactsExist(string channelName, TigaIpcOptions options)
    {
        return GetArtifactPaths(channelName, options).All(File.Exists);
    }

    private static IEnumerable<string> GetArtifactPaths(string channelName, TigaIpcOptions options)
    {
        var ipcDirectory = options.IpcDirectory!;
        return ArtifactSuffixes.Select(
            suffix => Path.Combine(ipcDirectory, $"tiga_{channelName}{suffix}"));
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp IPC artifacts.
        }
    }

    private static Process StartTestHost(string command, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add(FindTestHostPath());
        startInfo.ArgumentList.Add(command);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start test host.");
    }

    private static void WaitForChildReady(Process process)
    {
        var line = process.StandardOutput.ReadLine();
        if (!string.Equals(line, "ready", StringComparison.OrdinalIgnoreCase))
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"Test host not ready. Output: {line} Error: {error}");
        }
    }

    private static string FindTestHostPath()
    {
        var preferredConfiguration = ResolvePreferredConfiguration(AppContext.BaseDirectory);
        var fallbackConfiguration = string.Equals(
            preferredConfiguration,
            "Release",
            StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "Release";
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && baseDir != null; i++)
        {
            var preferredCandidate = Path.Combine(
                baseDir.FullName,
                "TigaIpc.TestHost",
                "bin",
                preferredConfiguration,
                "net6.0",
                "TigaIpc.TestHost.dll");
            if (File.Exists(preferredCandidate))
            {
                return preferredCandidate;
            }

            var fallbackCandidate = Path.Combine(
                baseDir.FullName,
                "TigaIpc.TestHost",
                "bin",
                fallbackConfiguration,
                "net6.0",
                "TigaIpc.TestHost.dll");
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
