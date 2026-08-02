using RsyncShell.App.Dialogs;
using RsyncShell.Core.Models;

namespace RsyncShell.App.Tests;

public sealed class SessionDialogTests
{
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

    private static ConnectionProfile Profile(string group) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = group,
        Host = "example.test",
        Username = "test",
        Group = group,
    };
}
