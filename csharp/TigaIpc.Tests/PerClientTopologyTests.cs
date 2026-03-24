using Microsoft.Extensions.Options;
using TigaIpc.Messaging;
using Xunit;

namespace TigaIpc.Tests;

public class PerClientTopologyTests
{
    [Fact]
    public async Task Invoke_FromMultipleClients_ReturnsResponses()
    {
        var name = $"per-client-{Guid.NewGuid():N}";
        var options = new TigaIpcOptions { ChannelName = name };
        var optionsWrapper = new OptionsWrapper<TigaIpcOptions>(options);

        await using var server = new TigaPerClientChannelServer(name, MappingType.Memory, optionsWrapper);
        server.AddClient("client-a");
        server.AddClient("client-b");
        server.Register("method", payload => $"Echo: {payload}");

        await using var clientA = new TigaChannel(
            PerClientChannelNames.GetResponseChannelName(name, "client-a"),
            PerClientChannelNames.GetRequestChannelName(name, "client-a"),
            MappingType.Memory,
            optionsWrapper);

        await using var clientB = new TigaChannel(
            PerClientChannelNames.GetResponseChannelName(name, "client-b"),
            PerClientChannelNames.GetRequestChannelName(name, "client-b"),
            MappingType.Memory,
            optionsWrapper);

        var results = await Task.WhenAll(
            clientA.InvokeAsync("method", "one"),
            clientB.InvokeAsync("method", "two"));

        Assert.Equal("Echo: one", results[0]);
        Assert.Equal("Echo: two", results[1]);
    }

    [Fact]
    public async Task Invoke_FileBackedClient_WaitsForListenerReadyBeforeFirstRequest()
    {
        var name = $"per-client-file-{Guid.NewGuid():N}";
        var clientId = $"client-{Guid.NewGuid():N}";
        var ipcDirectory = Path.Combine(Path.GetTempPath(), $"tiga-ipc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ipcDirectory);

        var options = new TigaIpcOptions
        {
            ChannelName = name,
            IpcDirectory = ipcDirectory,
            WaitTimeout = TimeSpan.FromSeconds(1),
            InvokeTimeout = TimeSpan.FromSeconds(2),
        };
        var optionsWrapper = new OptionsWrapper<TigaIpcOptions>(options);

        try
        {
            await using var server = new TigaPerClientChannelServer(
                name,
                MappingType.File,
                optionsWrapper);
            server.Register("method", payload => $"Echo: {payload}");

            await using var client = new TigaChannel(
                PerClientChannelNames.GetResponseChannelName(name, clientId),
                PerClientChannelNames.GetRequestChannelName(name, clientId),
                MappingType.File,
                optionsWrapper);

            var addClientTask = Task.Run(async () =>
            {
                await Task.Delay(300);
                server.AddClient(clientId);
            });

            var result = await client.InvokeAsync("method", "first-request", TimeSpan.FromSeconds(2));
            await addClientTask;

            Assert.Equal("Echo: first-request", result);
        }
        finally
        {
            try
            {
                Directory.Delete(ipcDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp IPC artifacts.
            }
        }
    }
}
