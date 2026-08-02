namespace RsyncShell.Core.Services;

public sealed class ExternalToolLocator
{
    private readonly string _applicationDirectory;

    public ExternalToolLocator(string? applicationDirectory = null)
    {
        _applicationDirectory = applicationDirectory ?? AppContext.BaseDirectory;
    }

    public string? FindRsyncWorker()
    {
        var bundledPath = Path.Combine(_applicationDirectory, "tools", "rsync", "rsyncworker.exe");
        return File.Exists(bundledPath) ? bundledPath : null;
    }
}
