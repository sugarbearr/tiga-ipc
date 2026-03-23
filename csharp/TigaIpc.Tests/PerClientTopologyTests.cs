using Microsoft.Extensions.Options;
using TigaIpc.IO;
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
}
