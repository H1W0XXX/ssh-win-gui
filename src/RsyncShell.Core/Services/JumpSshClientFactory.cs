using RsyncShell.Core.Models;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Tmds.Ssh;

namespace RsyncShell.Core.Services;

public static class JumpSshClientFactory
{
    public static SshClient CreateForRoute(
        ConnectionProfile target,
        SshAuthenticationOptions authentication,
        IReadOnlyList<ConnectionProfile> route,
        Func<SshHostKeyInfo, bool> verifyHostKey)
    {
        if (target.ProxyKind == SshProxyKind.Socks5)
            throw new InvalidOperationException("This native SSH route cannot use a SOCKS5 upstream proxy.");
        if (target.ProxyKind == SshProxyKind.JumpHost)
            return Create(target, authentication, route, verifyHostKey);
        if (route.Count != 1 || route[0] != target)
            throw new InvalidOperationException("The direct SSH route is invalid.");
        return new SshClient(CreateSettings(target, authentication, verifyHostKey));
    }

    public static SshClient Create(
        ConnectionProfile target,
        SshAuthenticationOptions authentication,
        IReadOnlyList<ConnectionProfile> route,
        Func<SshHostKeyInfo, bool> verifyHostKey)
    {
        if (route.Count < 2 || !ReferenceEquals(route[0], target) && route[0] != target)
            throw new InvalidOperationException("The SSH jump route is invalid.");

        var proxies = route.Skip(1).Reverse().Select(profile =>
        {
            if (profile.ProxyKind == SshProxyKind.Socks5)
                throw new InvalidOperationException("A SOCKS5 session cannot currently be nested inside an SSH jump chain.");
            if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
                throw new InvalidOperationException($"Jump session '{profile.Name}' must have a saved private key.");
            return (Proxy)new SshProxy(CreateSettings(
                profile,
                new SshAuthenticationOptions
                {
                    Kind = SshAuthenticationKind.PrivateKey,
                    PrivateKeyPath = Environment.ExpandEnvironmentVariables(profile.PrivateKeyPath),
                },
                verifyHostKey));
        }).ToArray();

        var settings = CreateSettings(target, authentication, verifyHostKey);
        settings.Proxy = Proxy.Chain(proxies);
        return new SshClient(settings);
    }

    private static SshClientSettings CreateSettings(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        Func<SshHostKeyInfo, bool> verifyHostKey)
    {
        authentication.Validate();
        var settings = new SshClientSettings
        {
            HostName = profile.Host,
            Port = profile.Port,
            UserName = profile.Username,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            KeepAliveInterval = TimeSpan.FromSeconds(25),
            KeepAliveCountMax = 3,
            MinimumRSAKeySize = 2048,
            UserKnownHostsFilePaths = [],
            GlobalKnownHostsFilePaths = [],
            Credentials = authentication.Kind == SshAuthenticationKind.PrivateKey
                ? [LoadPrivateKeyCredential(authentication.PrivateKeyPath!, authentication.PrivateKeyPassphrase)]
                : [new PasswordCredential(authentication.Password ?? string.Empty)],
        };
        settings.HostAuthentication = (context, _) =>
        {
            var key = context.ConnectionInfo.ServerKey.Key;
            return ValueTask.FromResult(verifyHostKey(new SshHostKeyInfo(
                profile.Host, profile.Port, key.Type, 0, key.SHA256FingerPrint)));
        };
        return settings;
    }

    private static PrivateKeyCredential LoadPrivateKeyCredential(string path, string? passphrase)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(path);
        var pem = File.ReadAllText(expandedPath);
        if (pem.Contains("-----BEGIN RSA PRIVATE KEY-----", StringComparison.Ordinal))
        {
            pem = ConvertLegacyRsaKey(expandedPath, passphrase ?? string.Empty);
        }
        var keyData = pem.ToCharArray();
        return new PrivateKeyCredential(keyData, passphrase ?? string.Empty, expandedPath);
    }

    private static string ConvertLegacyRsaKey(string sourcePath, string passphrase)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Legacy RSA key conversion requires Windows OpenSSH.");
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"ssh-win-gui-key-{Guid.NewGuid():N}");
        File.Copy(sourcePath, temporaryPath, overwrite: false);
        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Unable to resolve the current Windows user SID.");
            var security = new FileSecurity();
            security.SetOwner(currentUser);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(temporaryPath).SetAccessControl(security);

            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh-keygen.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add("-q");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(temporaryPath);
            startInfo.ArgumentList.Add("-P");
            startInfo.ArgumentList.Add(passphrase);
            startInfo.ArgumentList.Add("-N");
            startInfo.ArgumentList.Add(passphrase);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start ssh-keygen.exe for legacy RSA key conversion.");
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("ssh-keygen.exe timed out while converting a legacy RSA key.");
            }
            var error = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"ssh-keygen.exe could not read the legacy RSA key: {error}");
            return File.ReadAllText(temporaryPath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
