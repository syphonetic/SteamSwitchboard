using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SteamSwitchboard.Services;

internal static class TaskbarUnreadBadge
{
    private const double CanvasSize = 32;

    internal static string? FormatCount(int unreadCount) => unreadCount switch
    {
        <= 0 => null,
        > 99 => "99",
        _ => unreadCount.ToString(CultureInfo.InvariantCulture)
    };

    internal static ImageSource? CreateOverlay(int unreadCount)
    {
        var label = FormatCount(unreadCount);
        if (label is null)
        {
            return null;
        }

        var background = new SolidColorBrush(Color.FromRgb(0xD9, 0x2D, 0x45));
        var border = new Pen(Brushes.White, 1.5);
        var typeface = new Typeface(
            new FontFamily("Segoe UI"),
            FontStyles.Normal,
            FontWeights.Bold,
            FontStretches.Normal);
        background.Freeze();
        border.Freeze();

        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawEllipse(
                background,
                border,
                new Point(CanvasSize / 2, CanvasSize / 2),
                14.5,
                14.5);

            var fontSize = label.Length switch
            {
                1 => 17,
                _ => 13.5
            };
            var text = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.White,
                pixelsPerDip: 1);
            context.DrawText(
                text,
                new Point(
                    (CanvasSize - text.WidthIncludingTrailingWhitespace) / 2,
                    (CanvasSize - text.Height) / 2 - 0.5));
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
