using System.IO;
using System.Text;

namespace RsyncShell.App.Services;

internal static class DiagnosticLog
{
    private static readonly object Gate = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RsyncShell",
        "logs");

    public static void Write(string source, Exception exception)
        => Write(source, exception.ToString());

    public static void Write(string source, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, $"app-{DateTime.UtcNow:yyyyMMdd}.log");
                var record = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("O"))
                    .Append(" [")
                    .Append(source)
                    .AppendLine("]")
                    .AppendLine(message)
                    .AppendLine()
                    .ToString();
                File.AppendAllText(path, record, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Diagnostics must never hide or replace the original failure.
        }
    }
}
