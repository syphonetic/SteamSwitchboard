using System.Windows;

namespace SteamSwitchboard.Services;

public static class WindowSizing
{
    public static void ClampToCurrentWorkArea(
        Window window,
        double outerMargin = 24)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentOutOfRangeException.ThrowIfNegative(outerMargin);

        var availableWidth = Math.Max(
            1,
            SystemParameters.WorkArea.Width - outerMargin);
        var availableHeight = Math.Max(
            1,
            SystemParameters.WorkArea.Height - outerMargin);

        window.MinWidth = Math.Min(window.MinWidth, availableWidth);
        window.MinHeight = Math.Min(window.MinHeight, availableHeight);
        window.MaxWidth = Math.Min(window.MaxWidth, availableWidth);
        window.MaxHeight = Math.Min(window.MaxHeight, availableHeight);

        if (!double.IsNaN(window.Width))
        {
            window.Width = Math.Min(window.Width, availableWidth);
        }

        if (!double.IsNaN(window.Height))
        {
            window.Height = Math.Min(window.Height, availableHeight);
        }
    }
}
