using System.Text;
using Microsoft.Terminal.Wpf;

namespace RsyncShell.App.Controls;

public sealed class PasswordPromptTerminalConnection : ITerminalConnection, IDisposable
{
    private readonly object _gate = new();
    private readonly StringBuilder _password = new();
    private readonly string _initialOutput;
    private bool _attached;
    private bool _closed;
    private bool _submitted;

    public PasswordPromptTerminalConnection(string initialOutput)
    {
        _initialOutput = initialOutput;
    }

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;
    public event EventHandler<string>? PasswordSubmitted;

    public void Start() => AttachRenderer();

    public void AttachRenderer()
    {
        lock (_gate)
        {
            if (_closed || _attached)
            {
                return;
            }

            _attached = true;
        }

        PublishOutput(_initialOutput);
    }

    public void WriteInput(string data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        string? submittedPassword = null;
        lock (_gate)
        {
            if (_closed || _submitted)
            {
                return;
            }

            foreach (var character in data)
            {
                switch (character)
                {
                    case '\r':
                    case '\n':
                        if (_password.Length > 0)
                        {
                            submittedPassword = _password.ToString();
                            _password.Clear();
                            _submitted = true;
                        }
                        break;
                    case '\b':
                    case '\u007f':
                        RemoveLastTextElement(_password);
                        break;
                    default:
                        if (!char.IsControl(character))
                        {
                            _password.Append(character);
                        }
                        break;
                }

                if (_submitted)
                {
                    break;
                }
            }
        }

        if (submittedPassword is not null)
        {
            PublishOutput("\r\n");
            PasswordSubmitted?.Invoke(this, submittedPassword);
        }
    }

    public void Resize(uint rows, uint columns)
    {
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _password.Clear();
        }
    }

    public void Dispose() => Close();

    private void PublishOutput(string output) =>
        TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(output));

    private static void RemoveLastTextElement(StringBuilder value)
    {
        if (value.Length == 0)
        {
            return;
        }

        var removeCount = value.Length >= 2 &&
                          char.IsHighSurrogate(value[^2]) &&
                          char.IsLowSurrogate(value[^1])
            ? 2
            : 1;
        value.Remove(value.Length - removeCount, removeCount);
    }
}
