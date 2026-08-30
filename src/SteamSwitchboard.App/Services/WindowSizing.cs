using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SteamSwitchboard.Services;

public static class WindowSizing
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;

    public static void ClampToCurrentWorkArea(
        Window window,
        double outerMargin = 24)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentOutOfRangeException.ThrowIfNegative(outerMargin);

        var requestedMinWidth = window.MinWidth;
        var requestedMinHeight = window.MinHeight;
        var requestedMaxWidth = window.MaxWidth;
        var requestedMaxHeight = window.MaxHeight;

        void Apply(bool reposition) => ApplyWindowBounds(
            window,
            outerMargin,
            requestedMinWidth,
            requestedMinHeight,
            requestedMaxWidth,
            requestedMaxHeight,
            reposition);

        if (new WindowInteropHelper(window).Handle == IntPtr.Zero)
        {
            EventHandler? sourceInitialized = null;
            sourceInitialized = (_, _) =>
            {
                window.SourceInitialized -= sourceInitialized;
                Apply(reposition: false);
            };
            window.SourceInitialized += sourceInitialized;
        }
        else
        {
            Apply(reposition: false);
        }

        RoutedEventHandler? loaded = null;
        loaded = (_, _) =>
        {
            window.Loaded -= loaded;
            Apply(reposition: true);
        };
        window.Loaded += loaded;

        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(
            () => Apply(reposition: true));
    }

    internal static Rectangle ClampPixelBounds(
        Rectangle bounds,
        Rectangle workArea,
        int totalHorizontalMargin,
        int totalVerticalMargin)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workArea),
                "The monitor work area must have a positive size.");
        }

        totalHorizontalMargin = Math.Clamp(
            totalHorizontalMargin,
            0,
            Math.Max(0, workArea.Width - 1));
        totalVerticalMargin = Math.Clamp(
            totalVerticalMargin,
            0,
            Math.Max(0, workArea.Height - 1));

        var leftInset = totalHorizontalMargin / 2;
        var rightInset = totalHorizontalMargin - leftInset;
        var topInset = totalVerticalMargin / 2;
        var bottomInset = totalVerticalMargin - topInset;
        var maximumWidth = Math.Max(
            1,
            workArea.Width - leftInset - rightInset);
        var maximumHeight = Math.Max(
            1,
            workArea.Height - topInset - bottomInset);
        var width = Math.Clamp(bounds.Width, 1, maximumWidth);
        var height = Math.Clamp(bounds.Height, 1, maximumHeight);
        var minimumLeft = workArea.Left + leftInset;
        var maximumLeft = workArea.Right - rightInset - width;
        var minimumTop = workArea.Top + topInset;
        var maximumTop = workArea.Bottom - bottomInset - height;

        return new Rectangle(
            Math.Clamp(bounds.Left, minimumLeft, maximumLeft),
            Math.Clamp(bounds.Top, minimumTop, maximumTop),
            width,
            height);
    }

    private static void ApplyWindowBounds(
        Window window,
        double outerMargin,
        double requestedMinWidth,
        double requestedMinHeight,
        double requestedMaxWidth,
        double requestedMaxHeight,
        bool reposition)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var workArea = System.Windows.Forms.Screen
            .FromHandle(handle)
            .WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(window);
        var scaleX = Math.Max(0.01, dpi.DpiScaleX);
        var scaleY = Math.Max(0.01, dpi.DpiScaleY);
        var availableWidth = Math.Max(
            1,
            (workArea.Width / scaleX) - outerMargin);
        var availableHeight = Math.Max(
            1,
            (workArea.Height / scaleY) - outerMargin);

        window.MinWidth = Math.Min(requestedMinWidth, availableWidth);
        window.MinHeight = Math.Min(requestedMinHeight, availableHeight);
        window.MaxWidth = Math.Min(requestedMaxWidth, availableWidth);
        window.MaxHeight = Math.Min(requestedMaxHeight, availableHeight);
        if (!double.IsNaN(window.Width))
        {
            window.Width = Math.Min(window.Width, availableWidth);
        }

        if (!double.IsNaN(window.Height))
        {
            window.Height = Math.Min(window.Height, availableHeight);
        }

        if (!reposition
            || window.WindowState != WindowState.Normal
            || !GetWindowRect(handle, out var nativeBounds))
        {
            return;
        }

        var currentBounds = Rectangle.FromLTRB(
            nativeBounds.Left,
            nativeBounds.Top,
            nativeBounds.Right,
            nativeBounds.Bottom);
        var clampedBounds = ClampPixelBounds(
            currentBounds,
            workArea,
            (int)Math.Ceiling(outerMargin * scaleX),
            (int)Math.Ceiling(outerMargin * scaleY));
        if (clampedBounds == currentBounds)
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            IntPtr.Zero,
            clampedBounds.Left,
            clampedBounds.Top,
            clampedBounds.Width,
            clampedBounds.Height,
            SwpNoActivate | SwpNoZOrder);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr windowHandle,
        out NativeRectangle rectangle);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int left,
        int top,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRectangle
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
