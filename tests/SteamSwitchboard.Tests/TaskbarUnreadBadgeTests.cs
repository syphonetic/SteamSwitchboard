using System.Windows.Media;
using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class TaskbarUnreadBadgeTests
{
    [TestMethod]
    public void CountLabel_IsBoundedAndCultureIndependent()
    {
        Assert.IsNull(TaskbarUnreadBadge.FormatCount(-1));
        Assert.IsNull(TaskbarUnreadBadge.FormatCount(0));
        Assert.AreEqual("1", TaskbarUnreadBadge.FormatCount(1));
        Assert.AreEqual("99", TaskbarUnreadBadge.FormatCount(99));
        Assert.AreEqual("99", TaskbarUnreadBadge.FormatCount(100));
        Assert.AreEqual("99", TaskbarUnreadBadge.FormatCount(int.MaxValue));
    }

    [TestMethod]
    public async Task Overlay_IsFrozenVectorArtworkAndClearsAtZero()
    {
        await WpfTestHost.RunAsync(() =>
        {
            Assert.IsNull(TaskbarUnreadBadge.CreateOverlay(0));
            foreach (var count in new[] { 1, 42, 100 })
            {
                var overlay = Assert.IsInstanceOfType<DrawingImage>(
                    TaskbarUnreadBadge.CreateOverlay(count));
                Assert.IsTrue(overlay.IsFrozen);
                Assert.IsTrue(overlay.Width > 0);
                Assert.IsTrue(overlay.Height > 0);

                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    16,
                    16,
                    96,
                    96,
                    PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawImage(
                        overlay,
                        new System.Windows.Rect(0, 0, 16, 16));
                }

                bitmap.Render(visual);
                var pixels = new byte[16 * 16 * 4];
                bitmap.CopyPixels(pixels, 16 * 4, 0);
                var redPixels = 0;
                var interiorLightPixels = 0;
                for (var y = 0; y < 16; y++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        var index = ((y * 16) + x) * 4;
                        var blue = pixels[index];
                        var green = pixels[index + 1];
                        var red = pixels[index + 2];
                        var alpha = pixels[index + 3];
                        if (alpha > 180
                            && red > 160
                            && green < 130
                            && blue < 150)
                        {
                            redPixels++;
                        }

                        if (x is >= 4 and <= 11
                            && y is >= 4 and <= 11
                            && alpha > 180
                            && red > 190
                            && green > 190
                            && blue > 190)
                        {
                            interiorLightPixels++;
                        }
                    }
                }

                Assert.IsGreaterThan(40, redPixels);
                Assert.IsGreaterThan(
                    1,
                    interiorLightPixels,
                    $"The {count} badge text was not legible at 16 pixels.");
            }
            return Task.CompletedTask;
        });
    }
}
