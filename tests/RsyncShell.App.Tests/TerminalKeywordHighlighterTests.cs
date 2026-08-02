using RsyncShell.App.Controls;
using RsyncShell.App.Services;

namespace RsyncShell.App.Tests;

public sealed class TerminalKeywordHighlighterTests
{
    [Fact]
    public void CustomEditorParsesTrimmedNonEmptyLines()
    {
        Assert.Equal(
            ["true", "ship it", "稳了"],
            Dialogs.KeywordHighlightingDialog.ParseLines(" true\r\n\r\nship it\n 稳了 "));
    }

    [Fact]
    public void ColorsDefaultForegroundKeywordsByCategory()
    {
        var highlighter = new TerminalKeywordHighlighter();

        var result = highlighter.Highlight("true FALSE warning successful failed ok");

        Assert.Equal(
            "\x1b[92mtrue\x1b[39m \x1b[91mFALSE\x1b[39m \x1b[93mwarning\x1b[39m " +
            "\x1b[92msuccessful\x1b[39m \x1b[91mfailed\x1b[39m \x1b[92mok\x1b[39m",
            result);
    }

    [Fact]
    public void MatchesWholeWordsOnly()
    {
        var highlighter = new TerminalKeywordHighlighter();

        var result = highlighter.Highlight("truth falsehood token_ok okay");

        Assert.Equal("truth falsehood token_ok okay", result);
    }

    [Fact]
    public void PreservesExistingAnsiForegroundAndResumesAfterReset()
    {
        var highlighter = new TerminalKeywordHighlighter();

        var result = highlighter.Highlight("\x1b[34mtrue\x1b[0m false");

        Assert.Equal("\x1b[34mtrue\x1b[0m \x1b[91mfalse\x1b[39m", result);
    }

    [Fact]
    public void TracksForegroundAcrossOutputChunks()
    {
        var highlighter = new TerminalKeywordHighlighter();

        Assert.Equal("\x1b[31m", highlighter.Highlight("\x1b[31m"));
        Assert.Equal("true", highlighter.Highlight("true"));
        Assert.Equal("\x1b[0m", highlighter.Highlight("\x1b[0m"));
        Assert.Equal("\x1b[92mtrue\x1b[39m", highlighter.Highlight("true"));
    }

    [Fact]
    public void LeavesTerminalMetadataChunksUntouched()
    {
        var highlighter = new TerminalKeywordHighlighter();
        var data = "\x1b]0;false warning true\x07";

        Assert.Equal(data, highlighter.Highlight(data));
    }

    [Fact]
    public void AppliesCustomLiteralKeywordRulesImmediately()
    {
        var highlighter = new TerminalKeywordHighlighter();
        var rules = TerminalKeywordRules.CreateNormalized(
            ["ship it", "稳了"],
            ["炸了"],
            []);

        highlighter.Configure(rules);
        var result = highlighter.Highlight("ship it 稳了 炸了 true");

        Assert.Equal(
            "\x1b[92mship it\x1b[39m \x1b[92m稳了\x1b[39m \x1b[91m炸了\x1b[39m true",
            result);
    }

    [Fact]
    public void EmptyCustomRulesDisableMatchingWithoutDisablingTerminalOutput()
    {
        var highlighter = new TerminalKeywordHighlighter();
        highlighter.Configure(TerminalKeywordRules.CreateNormalized([], [], []));

        Assert.Equal("true false warning", highlighter.Highlight("true false warning"));
    }

    [Fact]
    public void NormalizationCapsTheCombinedRuleCountAndRemovesCrossColorDuplicates()
    {
        var green = Enumerable.Range(0, TerminalKeywordRules.MaximumKeywordCount).Select(index => $"g{index}");
        var rules = TerminalKeywordRules.CreateNormalized(green, ["g1", "red-extra"], ["yellow-extra"]);

        Assert.Equal(TerminalKeywordRules.MaximumKeywordCount, rules.Green.Length);
        Assert.Empty(rules.Red);
        Assert.Empty(rules.Yellow);
    }
}
