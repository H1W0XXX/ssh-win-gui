using RsyncShell.App.Dialogs;
using RsyncShell.Core.Models;

namespace RsyncShell.App.Tests;

public sealed class SessionDialogTests
{
    [Theory]
    [InlineData("  172.31.0.25  ", "172.31.0.25")]
    [InlineData("\t1080\r\n", "1080")]
    [InlineData("\u3000root\u00A0", "root")]
    [InlineData(null, "")]
    public void TrimOuterWhitespaceRemovesOnlyLeadingAndTrailingWhitespace(string? value, string expected)
    {
        Assert.Equal(expected, SessionDialog.TrimOuterWhitespace(value));
    }

    [Fact]
    public void GroupChoicesIncludeDefaultExistingAndCurrentWithoutCaseDuplicates()
    {
        var profiles = new[]
        {
            Profile("Development"),
            Profile("development"),
            Profile("Production"),
            Profile(" "),
        };

        var groups = SessionDialog.BuildGroupChoices(profiles, "Archive");

        Assert.Equal(["Sessions", "Archive", "Development", "Production"], groups);
    }

    [Fact]
    public void PrivateKeyChoicesPutCurrentFirstAndRemoveCaseInsensitiveDuplicates()
    {
        var profiles = new[]
        {
            Profile("Sessions", @"C:\keys\work.pem"),
            Profile("Sessions", @"c:\KEYS\WORK.pem"),
            Profile("Sessions", @"C:\keys\other.pem"),
        };

        var choices = SessionDialog.BuildPrivateKeyChoices(
            profiles,
            @"C:\keys\current.pem",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.Equal(
            [@"C:\keys\current.pem", @"C:\keys\work.pem", @"C:\keys\other.pem"],
            choices);
    }

    private static ConnectionProfile Profile(string group, string? privateKeyPath = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = group,
        Host = "example.test",
        Username = "test",
        Group = group,
        PrivateKeyPath = privateKeyPath,
    };
}
