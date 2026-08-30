using System.Drawing;
using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class WindowSizingTests
{
    [TestMethod]
    public void ClampPixelBounds_FitsOversizedCenteredWindowInsideScaledMonitor()
    {
        var workArea = new Rectangle(0, 0, 1635, 1121);
        var oversized = new Rectangle(-113, -40, 1860, 1200);

        var result = WindowSizing.ClampPixelBounds(
            oversized,
            workArea,
            totalHorizontalMargin: 48,
            totalVerticalMargin: 48);

        Assert.AreEqual(new Rectangle(24, 24, 1587, 1073), result);
    }

    [TestMethod]
    public void ClampPixelBounds_RepositionsWithoutResizingVisibleWindow()
    {
        var workArea = new Rectangle(-1920, 0, 1920, 1040);
        var partlyHidden = new Rectangle(-2050, 100, 1200, 800);

        var result = WindowSizing.ClampPixelBounds(
            partlyHidden,
            workArea,
            totalHorizontalMargin: 32,
            totalVerticalMargin: 32);

        Assert.AreEqual(new Rectangle(-1904, 100, 1200, 800), result);
    }

    [TestMethod]
    public void ClampPixelBounds_PreservesAlreadyVisibleWindow()
    {
        var workArea = new Rectangle(0, 0, 2560, 1400);
        var visible = new Rectangle(200, 100, 1240, 800);

        var result = WindowSizing.ClampPixelBounds(
            visible,
            workArea,
            totalHorizontalMargin: 32,
            totalVerticalMargin: 32);

        Assert.AreEqual(visible, result);
    }
}
