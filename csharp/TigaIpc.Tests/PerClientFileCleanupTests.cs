using Microsoft.Extensions.Options;
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
    public async Task StaleUntrackedFileArtifacts_AreRemovedDuringDiscovery()
    {
        var name = $"per-client-cleanup-{Guid.NewGuid():N}";
        var clientId = $"client-{Guid.NewGuid():N}";
        var ipcDirectory = Path.Combine(Path.GetTempPath(), $"tiga-ipc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ipcDirectory);

        var options = new TigaIpcOptions
        {
            ChannelName = name,
            IpcDirectory = ipcDirectory,
            ClientDiscoveryInterval = TimeSpan.FromMilliseconds(50),
            WaitTimeout = TimeSpan.FromSeconds(1),
        };
        var optionsWrapper = new OptionsWrapper<TigaIpcOptions>(options);
        var requestName = PerClientChannelNames.GetRequestChannelName(name, clientId);
        var responseName = PerClientChannelNames.GetResponseChannelName(name, clientId);

        try
        {
            CreateFileArtifacts(requestName, options);
            CreateFileArtifacts(responseName, options);
            SetArtifactsLastWriteTimeUtc(ipcDirectory, requestName, DateTime.UtcNow.AddMinutes(-10));
            SetArtifactsLastWriteTimeUtc(ipcDirectory, responseName, DateTime.UtcNow.AddMinutes(-10));

            await using var server = new TigaPerClientChannelServer(name, MappingType.File, optionsWrapper);

            var cleaned = await WaitForConditionAsync(
                () => !AnyArtifactsExist(ipcDirectory, requestName) &&
                      !AnyArtifactsExist(ipcDirectory, responseName),
                TimeSpan.FromSeconds(5));

            Assert.True(cleaned);
        }
        finally
        {
            TryDeleteDirectory(ipcDirectory);
        }
    }

    [Fact]
    public async Task OldActiveClientArtifacts_ArePreservedDuringDiscovery()
    {
        var name = $"per-client-active-{Guid.NewGuid():N}";
        var clientId = $"client-{Guid.NewGuid():N}";
        var ipcDirectory = Path.Combine(Path.GetTempPath(), $"tiga-ipc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ipcDirectory);

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

            SetArtifactsLastWriteTimeUtc(ipcDirectory, requestName, DateTime.UtcNow.AddMinutes(-10));
            SetArtifactsLastWriteTimeUtc(ipcDirectory, responseName, DateTime.UtcNow.AddMinutes(-10));

            await using var server = new TigaPerClientChannelServer(name, MappingType.File, optionsWrapper);
            server.Register("method", payload => $"Echo: {payload}");

            var result = await client.InvokeAsync("method", "ping", TimeSpan.FromSeconds(2));
            var preserved = await WaitForConditionAsync(
                () => AllArtifactsExist(ipcDirectory, requestName) &&
                      AllArtifactsExist(ipcDirectory, responseName),
                TimeSpan.FromSeconds(1));

            Assert.Equal("Echo: ping", result);
            Assert.True(preserved);
        }
        finally
        {
            TryDeleteDirectory(ipcDirectory);
        }
    }

    [Fact]
    public async Task StaleTrackedClientArtifacts_AreRemovedAfterClientDisconnect()
    {
        var name = $"per-client-disconnect-{Guid.NewGuid():N}";
        var clientId = $"client-{Guid.NewGuid():N}";
        var ipcDirectory = Path.Combine(Path.GetTempPath(), $"tiga-ipc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ipcDirectory);

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

            var client = new TigaChannel(
                responseName,
                requestName,
                MappingType.File,
                optionsWrapper);

            try
            {
                var result = await client.InvokeAsync("method", "disconnect", TimeSpan.FromSeconds(2));
                Assert.Equal("Echo: disconnect", result);
            }
            finally
            {
                await client.DisposeAsync();
            }

            SetArtifactsLastWriteTimeUtc(ipcDirectory, requestName, DateTime.UtcNow.AddMinutes(-10));
            SetArtifactsLastWriteTimeUtc(ipcDirectory, responseName, DateTime.UtcNow.AddMinutes(-10));

            var cleaned = await WaitForConditionAsync(
                () => !AnyArtifactsExist(ipcDirectory, requestName) &&
                      !AnyArtifactsExist(ipcDirectory, responseName),
                TimeSpan.FromSeconds(5));

            Assert.True(cleaned);
        }
        finally
        {
            TryDeleteDirectory(ipcDirectory);
        }
    }

    private static void CreateFileArtifacts(string channelName, TigaIpcOptions options)
    {
        using var _ = new WaitFreeMemoryMappedFile(channelName, MappingType.File, options);
    }

    private static void SetArtifactsLastWriteTimeUtc(
        string ipcDirectory,
        string channelName,
        DateTime lastWriteTimeUtc)
    {
        foreach (var artifactPath in GetArtifactPaths(ipcDirectory, channelName))
        {
            if (File.Exists(artifactPath))
            {
                File.SetLastWriteTimeUtc(artifactPath, lastWriteTimeUtc);
            }
        }
    }

    private static bool AnyArtifactsExist(string ipcDirectory, string channelName)
    {
        return GetArtifactPaths(ipcDirectory, channelName).Any(File.Exists);
    }

    private static bool AllArtifactsExist(string ipcDirectory, string channelName)
    {
        return GetArtifactPaths(ipcDirectory, channelName).All(File.Exists);
    }

    private static IEnumerable<string> GetArtifactPaths(string ipcDirectory, string channelName)
    {
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
}
