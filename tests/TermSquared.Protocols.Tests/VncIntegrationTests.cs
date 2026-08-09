using System.Net.Sockets;
using TermSquared.Protocols.Vnc;
using Xunit.Abstractions;

namespace TermSquared.Protocols.Tests;

public sealed class VncIntegrationTests(ITestOutputHelper output)
{
    [IntegrationFact]
    public async Task ReadsRawFramebufferFromConfiguredVncTarget()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("10.10.0.4", 5900, timeout.Token);
        await using var client = new RfbClient(tcpClient.GetStream(), leaveOpen: true);

        var serverInfo = await client.HandshakeAsync(timeout.Token);
        Assert.True(serverInfo.Width > 0);
        Assert.True(serverInfo.Height > 0);
        Assert.False(string.IsNullOrWhiteSpace(serverInfo.Name));

        await client.SetEncodingsAsync([0], timeout.Token);
        await client.RequestRawFramebufferUpdateAsync(
            incremental: false,
            x: 0,
            y: 0,
            serverInfo.Width,
            serverInfo.Height,
            timeout.Token);
        var update = await client.ReadFramebufferUpdateAsync(timeout.Token);

        Assert.NotEmpty(update.Rectangles);
        Assert.All(update.Rectangles, rectangle =>
        {
            Assert.Equal(0, rectangle.Encoding);
            Assert.NotEmpty(rectangle.Pixels);
        });
        output.WriteLine(
            $"VNC target=10.10.0.4:5900 outcome=success size={serverInfo.Width}x{serverInfo.Height} encoding=Raw");
    }
}
