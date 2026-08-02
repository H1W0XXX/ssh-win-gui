using System.Text;
using Renci.SshNet;
using RsyncShell.Core.Models;

namespace RsyncShell.Core.Services;

public sealed class SshClientSession : IDisposable
{
    private readonly AuthenticationMethod[] _authenticationMethods;
    internal SshClientSession(SshClient client, AuthenticationMethod[] authenticationMethods)
    {
        Client = client;
        _authenticationMethods = authenticationMethods;
    }
    public SshClient Client { get; }
    public void Dispose()
    {
        Client.Dispose();
        foreach (var method in _authenticationMethods) method.Dispose();
    }
}

public sealed class SshClientFactory
{
    public async Task<SshClientSession> ConnectAsync(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        IReadOnlyList<ConnectionProfile>? route = null,
        CancellationToken cancellationToken = default)
    {
        authentication.Validate();
        if (profile.ProxyKind == SshProxyKind.JumpHost)
            throw new InvalidOperationException("SSH jump sessions use the native direct-tcpip client path.");
        var methods = CreateAuthenticationMethods(profile.Username, authentication);
        ConnectionInfo info = profile.ProxyKind == SshProxyKind.Socks5
            ? new ConnectionInfo(profile.Host, profile.Port, profile.Username, ProxyTypes.Socks5,
                profile.ProxyHost!, profile.ProxyPort, string.Empty, string.Empty, methods)
            : new ConnectionInfo(profile.Host, profile.Port, profile.Username, methods);
        info.Encoding = Encoding.UTF8;
        info.Timeout = TimeSpan.FromSeconds(20);
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(25) };
        client.HostKeyReceived += (_, e) =>
        {
            try
            {
                e.CanTrust = verifyHostKey(new SshHostKeyInfo(
                    profile.Host, profile.Port, e.HostKeyName, e.KeyLength, e.FingerPrintSHA256));
            }
            catch { e.CanTrust = false; }
        };
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new SshClientSession(client, methods);
        }
        catch
        {
            client.Dispose();
            foreach (var method in methods) method.Dispose();
            throw;
        }
    }

    private static AuthenticationMethod[] CreateAuthenticationMethods(string username, SshAuthenticationOptions auth)
    {
        if (auth.Kind == SshAuthenticationKind.PrivateKey)
        {
            var key = string.IsNullOrEmpty(auth.PrivateKeyPassphrase)
                ? new PrivateKeyFile(auth.PrivateKeyPath!)
                : new PrivateKeyFile(auth.PrivateKeyPath!, auth.PrivateKeyPassphrase);
            return [new PrivateKeyAuthenticationMethod(username, key)];
        }
        var password = auth.Password ?? string.Empty;
        var keyboard = new KeyboardInteractiveAuthenticationMethod(username);
        keyboard.AuthenticationPrompt += (_, e) => { foreach (var prompt in e.Prompts) prompt.Response = password; };
        return [new PasswordAuthenticationMethod(username, password), keyboard];
    }
}
