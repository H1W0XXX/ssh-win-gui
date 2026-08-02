using System.Text;
using System.Text.RegularExpressions;
using RsyncShell.App.Services;

namespace RsyncShell.App.Controls;

internal sealed class TerminalKeywordHighlighter
{
    private const string Green = "\x1b[92m";
    private const string Red = "\x1b[91m";
    private const string Yellow = "\x1b[93m";
    private const string DefaultForeground = "\x1b[39m";

    private bool _defaultForeground = true;
    private TerminalKeywordRules? _rules;
    private Regex? _keywordPattern;
    private Dictionary<string, string> _keywordColors = new(StringComparer.OrdinalIgnoreCase);

    public TerminalKeywordHighlighter() => Configure(TerminalKeywordRules.Default);

    public void Configure(TerminalKeywordRules rules)
    {
        if (ReferenceEquals(_rules, rules))
        {
            return;
        }

        _rules = rules;
        _keywordColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddRules(rules.Green, Green);
        AddRules(rules.Red, Red);
        AddRules(rules.Yellow, Yellow);

        var alternatives = _keywordColors.Keys
            .OrderByDescending(keyword => keyword.Length)
            .ThenBy(keyword => keyword, StringComparer.OrdinalIgnoreCase)
            .Select(Regex.Escape)
            .ToArray();
        _keywordPattern = alternatives.Length == 0
            ? null
            : new Regex(
                $@"(?<![\p{{L}}\p{{N}}_])(?:{string.Join('|', alternatives)})(?![\p{{L}}\p{{N}}_])",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private void AddRules(IEnumerable<string> rules, string color)
    {
        foreach (var rule in rules)
        {
            _keywordColors.TryAdd(rule, color);
        }
    }

    public string Highlight(string data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return data;
        }

        // OSC/DCS/APC sequences can contain arbitrary printable payloads. Leave
        // their entire chunk untouched rather than ever coloring terminal metadata.
        if (data.Contains("\x1b]", StringComparison.Ordinal) ||
            data.Contains("\x1bP", StringComparison.Ordinal) ||
            data.Contains("\x1b_", StringComparison.Ordinal))
        {
            UpdateForegroundState(data);
            return data;
        }

        var output = new StringBuilder(data.Length + 32);
        var textStart = 0;
        for (var index = 0; index < data.Length; index++)
        {
            if (data[index] != '\x1b' || index + 1 >= data.Length || data[index + 1] != '[')
            {
                continue;
            }

            AppendText(output, data.AsSpan(textStart, index - textStart));
            var end = FindCsiEnd(data, index + 2);
            if (end < 0)
            {
                output.Append(data, index, data.Length - index);
                return output.ToString();
            }

            output.Append(data, index, end - index + 1);
            if (data[end] == 'm')
            {
                ApplySgr(data.AsSpan(index + 2, end - index - 2));
            }
            index = end;
            textStart = end + 1;
        }

        AppendText(output, data.AsSpan(textStart));
        return output.ToString();
    }

    private void AppendText(StringBuilder output, ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return;
        }

        if (!_defaultForeground)
        {
            output.Append(text);
            return;
        }

        if (_keywordPattern is null)
        {
            output.Append(text);
            return;
        }

        var value = text.ToString();
        output.Append(_keywordPattern.Replace(value, match =>
        {
            return !_keywordColors.TryGetValue(match.Value, out var color)
                ? match.Value
                : color + match.Value + DefaultForeground;
        }));
    }

    private void UpdateForegroundState(string data)
    {
        for (var index = 0; index < data.Length - 2; index++)
        {
            if (data[index] != '\x1b' || data[index + 1] != '[')
            {
                continue;
            }

            var end = FindCsiEnd(data, index + 2);
            if (end < 0)
            {
                return;
            }
            if (data[end] == 'm')
            {
                ApplySgr(data.AsSpan(index + 2, end - index - 2));
            }
            index = end;
        }
    }

    private static int FindCsiEnd(string data, int start)
    {
        for (var index = start; index < data.Length; index++)
        {
            if (data[index] is >= '@' and <= '~')
            {
                return index;
            }
        }
        return -1;
    }

    private void ApplySgr(ReadOnlySpan<char> parameters)
    {
        if (parameters.IsEmpty)
        {
            _defaultForeground = true;
            return;
        }

        foreach (var part in parameters.ToString().Split(';'))
        {
            if (!int.TryParse(part, out var code))
            {
                continue;
            }

            if (code is 0 or 39)
            {
                _defaultForeground = true;
            }
            else if (code is >= 30 and <= 37 or >= 90 and <= 97 or 38)
            {
                _defaultForeground = false;
            }
        }
    }
}
