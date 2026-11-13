using RsyncShell.App.Controls;

namespace RsyncShell.App.Tests;

public sealed class ComboBoxDropDownPlacementTests
{
    private const double PopupHeight = 500;
    private const double TargetHeight = 48;
    private const double Gap = 2;
    private const double LeftInset = 52;
    private const double BottomInset = 52;
    private const double ScreenBottom = 1440;

    [Fact]
    public void OpensBelowWhenListAndShadowMarginFit()
    {
        var targetTop = ScreenBottom - TargetHeight - Gap - PopupHeight - BottomInset;

        var placements = Order(targetTop);

        Assert.Equal(TargetHeight, placements[0].Point.Y);
        Assert.Equal(-LeftInset, placements[0].Point.X);
    }

    [Fact]
    public void OpensAboveWhenOnlyTheShadowMarginWouldLeaveTheScreen()
    {
        // The list alone still fits below here; WPF would open it below and then push the
        // whole popup up over the ComboBox.
        var targetTop = ScreenBottom - TargetHeight - Gap - PopupHeight - BottomInset + 1;

        var placements = Order(targetTop);

        Assert.Equal(-PopupHeight - (2 * Gap), placements[0].Point.Y);
        Assert.Equal(TargetHeight, placements[1].Point.Y);
    }

    [Fact]
    public void StaysBelowWhenNeitherSideFits()
    {
        var placements = Order(targetTop: 100, screenBottom: 400);

        Assert.Equal(TargetHeight, placements[0].Point.Y);
    }

    private static System.Windows.Controls.Primitives.CustomPopupPlacement[] Order(
        double targetTop,
        double screenBottom = ScreenBottom) =>
        ComboBoxDropDownPlacement.Order(
            PopupHeight,
            TargetHeight,
            Gap,
            LeftInset,
            BottomInset,
            targetTop,
            screenTop: 0,
            screenBottom);
}
