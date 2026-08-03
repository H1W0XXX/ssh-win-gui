namespace RsyncShell.App.Controls;

internal sealed class AlternateScreenTracker
{
    private const int TailLength = 24;
    private string _tail = string.Empty;

    public bool IsActive { get; private set; }

    public void Append(string data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        var input = _tail + data;
        for (var index = 0; index + 4 < input.Length; index++)
        {
            if (input[index] != '\x1b' || input[index + 1] != '[' || input[index + 2] != '?')
            {
                continue;
            }

            var end = index + 3;
            while (end < input.Length && (char.IsAsciiDigit(input[end]) || input[end] == ';'))
            {
                end++;
            }
            if (end >= input.Length || input[end] is not ('h' or 'l'))
            {
                continue;
            }

            var parameters = input.AsSpan(index + 3, end - index - 3);
            if (ContainsAlternateScreenMode(parameters))
            {
                IsActive = input[end] == 'h';
            }
            index = end;
        }

        _tail = input.Length <= TailLength ? input : input[^TailLength..];
    }

    public void Reset()
    {
        IsActive = false;
        _tail = string.Empty;
    }

    private static bool ContainsAlternateScreenMode(ReadOnlySpan<char> parameters)
    {
        while (!parameters.IsEmpty)
        {
            var separator = parameters.IndexOf(';');
            var value = separator < 0 ? parameters : parameters[..separator];
            if (value is "47" or "1047" or "1049")
            {
                return true;
            }
            if (separator < 0)
            {
                break;
            }
            parameters = parameters[(separator + 1)..];
        }
        return false;
    }
}
