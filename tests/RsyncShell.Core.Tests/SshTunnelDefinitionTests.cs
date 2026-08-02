using RsyncShell.Core.Models;

namespace RsyncShell.Core.Tests;

public sealed class SshTunnelDefinitionTests
{
    [Theory]
    [InlineData("1080", "127.0.0.1", 1080, "127.0.0.1:1080")]
    [InlineData("0.0.0.0:8080", "0.0.0.0", 8080, "0.0.0.0:8080")]
    [InlineData("[::1]:443", "::1", 443, "[::1]:443")]
    [InlineData("remote.internal:22", "remote.internal", 22, "remote.internal:22")]
    public void TunnelEndpointParsesSupportedForms(string text, string host, int port, string formatted)
    {
        Assert.True(TunnelEndpoint.TryParse(text, out var endpoint, out var error), error);
        Assert.Equal(host, endpoint!.Host);
        Assert.Equal(port, endpoint.Port);
        Assert.Equal(formatted, endpoint.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("::1:80")]
    [InlineData("[::1]80")]
    public void TunnelEndpointRejectsInvalidForms(string text)
    {
        Assert.False(TunnelEndpoint.TryParse(text, out _, out var error));
        Assert.NotEmpty(error);
    }
}
