using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using SteamSwitchboard;
using SteamSwitchboard.Controls;
using SteamSwitchboard.Models;
using SteamSwitchboard.Services;
using SteamSwitchboard.ViewModels;
using WpfButton = System.Windows.Controls.Button;

internal static class Program
{
    private static readonly TimeSpan InputDeadline = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CompletionDeadline = TimeSpan.FromSeconds(35);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 1)
        {
            Console.Error.WriteLine("Usage: SteamSwitchboard.UiRegression [screenshot.png]");
            return 2;
        }

        var screenshotPath = args.Length == 1
            ? Path.GetFullPath(args[0])
            : Path.Combine(
                Path.GetTempPath(),
                $"SteamSwitchboard-ui-regression-{Guid.NewGuid():N}.png");
        var disposableRoot = Path.Combine(
            Path.GetTempPath(),
            "SteamSwitchboard.UiRegression",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(disposableRoot);

        try
        {
            Run(disposableRoot, screenshotPath);
            Console.WriteLine($"Composed screenshot: {screenshotPath}");
            Console.WriteLine("SteamSwitchboard UI regression validation passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            DeleteDisposableRoot(disposableRoot);
        }
    }

    private static void Run(string disposableRoot, string screenshotPath)
    {
        var paths = new AppPaths(disposableRoot);
        paths.EnsureCreated();
        var first = new AccountProfile
        {
            DisplayName = "Legend",
            SteamLoginName = "legend_test",
            AccentHex = "#55DDA0",
            LastUsedUtc = DateTimeOffset.UtcNow
        };
        var second = new AccountProfile
        {
            DisplayName = "Phonetic Accounts",
            SteamLoginName = "phonetic_test",
            AccentHex = "#66C0F4",
            LastUsedUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        new StateStore(paths.StateFile).SaveAsync(new PersistedState
        {
            Accounts = [first, second],
            LastSelectedAccountId = first.Id,
            LastPlayAccountId = first.Id,
            Settings = new AppSettings
            {
                KeepAllChatsLive = true,
                EnableWindowsNotifications = false
            }
        }).GetAwaiter().GetResult();

        var installation = new SteamInstallationService(
            _ => false,
            _ => []);
        var viewModel = new MainViewModel(
            new StateStore(paths.StateFile),
            installation,
            new SteamLibraryService());
        var startOrder = new List<Guid>();
        var sessionCreationCounts = new Dictionary<Guid, int>();

        SteamChatSession CreateSession(
            AccountViewModel account,
            string browserDataFolder)
        {
            var creationCount = sessionCreationCounts.GetValueOrDefault(account.Id) + 1;
            sessionCreationCounts[account.Id] = creationCount;
            if (account.Id == first.Id && creationCount == 1)
            {
                return new SteamChatSession(
                    account,
                    browserDataFolder,
                    TimeSpan.FromMilliseconds(150),
                    static _ => new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously).Task);
            }

            return new SteamChatSession(account, browserDataFolder);
        }

        var application = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
            Resources = LoadApplicationResources()
        };
        var window = new MainWindow(
            viewModel,
            paths,
            new GameLaunchService(
                installation,
                new SteamClientAccountService()),
            CreateSession)
        {
            ShowInTaskbar = false,
            Topmost = true
        };
        window.ChatSessionInitializationStarted += account =>
        {
            if (!startOrder.Contains(account.Id))
            {
                startOrder.Add(account.Id);
            }
        };
        Exception? failure = null;
        var inputValidated = false;
        var reconnectTriggered = false;
        var started = Stopwatch.StartNew();

        var recoveryTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        recoveryTimer.Tick += (_, _) =>
        {
            if (reconnectTriggered
                || viewModel.Accounts.FirstOrDefault(account => account.Id == first.Id)
                    is not { ConnectionState: ChatConnectionState.Failed })
            {
                return;
            }

            var container = (System.Windows.Controls.Panel?)window.FindName(
                "ChatSessionContainer")
                ?? throw new InvalidOperationException(
                    "The chat-session container was not found.");
            var failedSession = container.Children
                .OfType<SteamChatSession>()
                .Single(session => session.Account.Id == first.Id);
            var reconnect = (WpfButton?)failedSession.FindName("ReconnectButton")
                ?? throw new InvalidOperationException(
                    "The workspace Reconnect button was not found.");
            if (!reconnect.IsVisible)
            {
                throw new InvalidOperationException(
                    "A timed-out workspace did not expose its recovery action.");
            }

            reconnectTriggered = true;
            reconnect.RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent));
        };

        var inputTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        inputTimer.Tick += (_, _) =>
        {
            inputTimer.Stop();
            try
            {
                if (started.Elapsed > InputDeadline)
                {
                    throw new InvalidOperationException(
                        $"The UI input queue was blocked for {started.Elapsed.TotalSeconds:F1} seconds.");
                }

                ValidateInteractiveShell(window, viewModel);
                SaveComposedWindow(window, screenshotPath);
                ValidateAccountSidebarIsNotOverpainted(window);
                inputValidated = true;
            }
            catch (Exception exception)
            {
                failure = exception;
                CloseHarness(window);
            }
        };

        var completionTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        completionTimer.Tick += (_, _) =>
        {
            var startupPending = !inputValidated
                || !reconnectTriggered
                || sessionCreationCounts.GetValueOrDefault(first.Id) < 2
                || startOrder.Count < 2
                || viewModel.Accounts.Any(account =>
                    account.ConnectionState == ChatConnectionState.Starting);
            if (startupPending && started.Elapsed < CompletionDeadline)
            {
                return;
            }

            completionTimer.Stop();
            recoveryTimer.Stop();
            try
            {
                if (startupPending)
                {
                    throw new InvalidOperationException(
                        "A browser workspace exceeded its bounded startup window.");
                }

                if (startOrder[0] != first.Id || startOrder[1] != second.Id)
                {
                    throw new InvalidOperationException(
                        "Browser workspaces did not start in selected-then-background order.");
                }
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            CloseHarness(window);
        };

        application.MainWindow = window;
        window.Show();
        recoveryTimer.Start();
        inputTimer.Start();
        completionTimer.Start();
        Dispatcher.Run();

        if (failure is not null)
        {
            throw new InvalidOperationException(
                "The disposable UI regression harness failed.",
                failure);
        }

        Console.WriteLine("Interactive shell: PASS");
        Console.WriteLine("Forced timeout and fresh-session reconnect: PASS");
        Console.WriteLine(
            $"Account states: {string.Join(", ", viewModel.Accounts.Select(account => account.StatusText))}");
    }

    private static void ValidateInteractiveShell(
        MainWindow window,
        MainViewModel viewModel)
    {
        var root = (Grid?)window.FindName("RootLayout")
            ?? throw new InvalidOperationException("RootLayout was not found.");
        if (!root.IsEnabled || !root.IsHitTestVisible)
        {
            throw new InvalidOperationException(
                "The main shell remained disabled while chat sessions opened.");
        }

        var settings = (WpfButton?)window.FindName("SettingsNavButton")
            ?? throw new InvalidOperationException("The Settings button was not found.");
        settings.RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent));
        if (viewModel.SelectedSection != AppSection.Settings)
        {
            throw new InvalidOperationException("The Settings button did not navigate.");
        }

        var chats = (WpfButton?)window.FindName("ChatsNavButton")
            ?? throw new InvalidOperationException("The Chats button was not found.");
        chats.RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent));
        if (viewModel.SelectedSection != AppSection.Chats)
        {
            throw new InvalidOperationException("The Chats button did not navigate.");
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(handle, out var bounds))
        {
            throw new InvalidOperationException("The window bounds could not be read.");
        }

        var workArea = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        if (bounds.Left < workArea.Left
            || bounds.Top < workArea.Top
            || bounds.Right > workArea.Right
            || bounds.Bottom > workArea.Bottom)
        {
            throw new InvalidOperationException(
                $"Window {bounds} falls outside work area {workArea}.");
        }

        Console.WriteLine($"Window bounds: {bounds}");
        Console.WriteLine($"Work area: {workArea}");
    }

    private static void ValidateAccountSidebarIsNotOverpainted(Window window)
    {
        var accountList = (FrameworkElement?)window.FindName("AccountList")
            ?? throw new InvalidOperationException("The account list was not found.");
        var source = PresentationSource.FromVisual(accountList)
            ?? throw new InvalidOperationException("The account list is not rendered.");
        var transform = source.CompositionTarget?.TransformToDevice
            ?? System.Windows.Media.Matrix.Identity;
        var topLeft = accountList.PointToScreen(new System.Windows.Point(0, 0));
        var width = Math.Max(1, (int)Math.Floor(accountList.ActualWidth * transform.M11));
        var height = Math.Max(1, (int)Math.Floor(accountList.ActualHeight * transform.M22));
        using var bitmap = CaptureScreenRegion(
            (int)Math.Floor(topLeft.X),
            (int)Math.Floor(topLeft.Y),
            width,
            height);

        long sampled = 0;
        long nearWhite = 0;
        for (var y = 0; y < bitmap.Height; y += 4)
        {
            for (var x = 0; x < bitmap.Width; x += 4)
            {
                var pixel = bitmap.GetPixel(x, y);
                sampled++;
                if (pixel.R >= 235 && pixel.G >= 235 && pixel.B >= 235)
                {
                    nearWhite++;
                }
            }
        }

        var whiteRatio = sampled == 0 ? 1 : (double)nearWhite / sampled;
        if (whiteRatio > 0.15)
        {
            throw new InvalidOperationException(
                $"The account sidebar is {whiteRatio:P1} white, indicating browser airspace overpaint.");
        }

        Console.WriteLine($"Sidebar near-white ratio: {whiteRatio:P2}");
    }

    private static void SaveComposedWindow(Window window, string outputPath)
    {
        window.UpdateLayout();
        var handle = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(handle, out var bounds))
        {
            throw new InvalidOperationException(
                "The composed window bounds could not be read.");
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("Output directory missing."));
        _ = DwmFlush();
        using var bitmap = CaptureScreenRegion(
            bounds.Left,
            bounds.Top,
            Math.Max(1, bounds.Right - bounds.Left),
            Math.Max(1, bounds.Bottom - bounds.Top));
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static System.Drawing.Bitmap CaptureScreenRegion(
        int left,
        int top,
        int width,
        int height)
    {
        var bitmap = new System.Drawing.Bitmap(
            width,
            height,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(
                left,
                top,
                0,
                0,
                bitmap.Size,
                System.Drawing.CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(),
            "src",
            "SteamSwitchboard.App",
            "App.xaml"));
        var document = XDocument.Load(sourcePath, LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new InvalidDataException("App.xaml has no root element.");
        var presentation = root.Name.Namespace;
        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var resources = root.Element(presentation + "Application.Resources")
            ?? throw new InvalidDataException("App.xaml has no resources.");
        root.Name = presentation + "ResourceDictionary";
        root.Attribute(xaml + "Class")?.Remove();
        root.Attribute("ShutdownMode")?.Remove();
        root.SetAttributeValue(
            XNamespace.Xmlns + "converters",
            "clr-namespace:SteamSwitchboard.Converters;assembly=SteamSwitchboard");
        root.ReplaceNodes(resources.Nodes());
        var localConverters = XNamespace.Get(
            "clr-namespace:SteamSwitchboard.Converters");
        var assemblyConverters = XNamespace.Get(
            "clr-namespace:SteamSwitchboard.Converters;assembly=SteamSwitchboard");
        foreach (var element in root.Descendants().Where(element =>
                     element.Name.Namespace == localConverters))
        {
            element.Name = assemblyConverters + element.Name.LocalName;
        }

        return (ResourceDictionary)XamlReader.Parse(
            document.ToString(SaveOptions.DisableFormatting));
    }

    private static void CloseHarness(Window window)
    {
        if (window.IsVisible)
        {
            window.Close();
        }

        Dispatcher.CurrentDispatcher.BeginInvokeShutdown(
            DispatcherPriority.Background);
    }

    private static void DeleteDisposableRoot(string disposableRoot)
    {
        const int attempts = 50;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (Directory.Exists(disposableRoot))
                {
                    Directory.Delete(disposableRoot, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (
                attempt < attempts
                && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }

        Directory.Delete(disposableRoot, recursive: true);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr windowHandle,
        out NativeRectangle rectangle);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmFlush();

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRectangle
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public override string ToString() =>
            $"({Left}, {Top})-({Right}, {Bottom})";
    }
}
