namespace RsyncShell.Core.Models;

public enum SshAuthenticationKind
{
    Password,
    PrivateKey,
}

/// <summary>
/// Ephemeral SSH credentials for one open tab. These values are never serialized by
/// <see cref="Services.SessionRepository"/>.
/// </summary>
public sealed record SshAuthenticationOptions
{
    public required SshAuthenticationKind Kind { get; init; }
    public string? Password { get; init; }
    public string? PrivateKeyPath { get; init; }
    public string? PrivateKeyPassphrase { get; init; }

    public void Validate()
    {
        if (Kind == SshAuthenticationKind.Password && string.IsNullOrEmpty(Password))
        {
            throw new InvalidOperationException("A non-empty password is required for password authentication.");
        }

        if (Kind == SshAuthenticationKind.PrivateKey && string.IsNullOrWhiteSpace(PrivateKeyPath))
        {
            throw new InvalidOperationException("A private-key path is required for key authentication.");
        }
    }

    public override string ToString() =>
        $"{nameof(SshAuthenticationOptions)} {{ Kind = {Kind}, PrivateKeyPath = {PrivateKeyPath ?? "<none>"}, Secrets = <redacted> }}";
}

public sealed record SshHostKeyInfo(
    string Host,
    int Port,
    string Algorithm,
    int KeyLength,
    string FingerprintSha256);
