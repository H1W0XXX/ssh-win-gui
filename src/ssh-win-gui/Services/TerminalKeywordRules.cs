using System.Collections.Immutable;

namespace RsyncShell.App.Services;

public sealed record TerminalKeywordRules(
    ImmutableArray<string> Green,
    ImmutableArray<string> Red,
    ImmutableArray<string> Yellow)
{
    public const int MaximumKeywordLength = 128;
    public const int MaximumKeywordCount = 256;

    public static TerminalKeywordRules Default { get; } = new(
        ["true", "ok", "success", "successful", "succeeded", "pass", "passed", "enabled"],
        ["false", "error", "errors", "fail", "failed", "failure", "fatal", "denied", "disabled"],
        ["warn", "warning", "warnings", "caution"]);

    public static TerminalKeywordRules CreateNormalized(
        IEnumerable<string> green,
        IEnumerable<string> red,
        IEnumerable<string> yellow)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new TerminalKeywordRules(
            NormalizeCategory(green, claimed),
            NormalizeCategory(red, claimed),
            NormalizeCategory(yellow, claimed));
    }

    private static ImmutableArray<string> NormalizeCategory(
        IEnumerable<string> values,
        HashSet<string> claimed)
    {
        var normalized = ImmutableArray.CreateBuilder<string>();
        foreach (var source in values)
        {
            if (claimed.Count >= MaximumKeywordCount)
            {
                break;
            }

            var value = source.Trim();
            if (value.Length is > 0 and <= MaximumKeywordLength && claimed.Add(value))
            {
                normalized.Add(value);
            }
        }
        return normalized.ToImmutable();
    }
}
