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
using WpfImage = System.Windows.Controls.Image;
using WpfListBox = System.Windows.Controls.ListBox;

internal static class Program
{
    private static readonly TimeSpan InputDeadline = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CompletionDeadline = TimeSpan.FromSeconds(35);

    [STAThread]
    private static int Main(string[] args)
    {
        var notificationSmoke = args is ["--notification-smoke"];

        if (args.Length > 1
            || (args.Length == 1
                && !notificationSmoke
                && args[0].StartsWith("--")))
        {
            Console.Error.WriteLine(
                "Usage: SteamSwitchboard.UiRegression [screenshot.png | --notification-smoke]");
            return 2;
        }

        var screenshotPath = args.Length == 1 && !notificationSmoke
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
            Run(disposableRoot, screenshotPath, notificationSmoke);
            Console.WriteLine($"Composed screenshot: {screenshotPath}");
            if (notificationSmoke)
            {
                Console.WriteLine(
                    "Windows notification submission and cleanup: PASS");
            }

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

    private static void Run(
        string disposableRoot,
        string screenshotPath,
        bool notificationSmoke)
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
                EnableWindowsNotifications = notificationSmoke
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
        Console.WriteLine("Harness application resources loaded.");
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
        Console.WriteLine("Harness main window constructed.");
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
        var notificationTestTriggered = !notificationSmoke;
        var notificationTestValidated = !notificationSmoke;
        var notificationValidationBusy = false;
        WpfButton? notificationTestButton = null;
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
                // The state notification can precede WPF's next layout pass.
                // Retry until the bounded harness deadline instead of treating
                // that normal rendering interval as a product failure.
                return;
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
                ValidateProfileIdentityAndBranding(window);
                ValidateTaskbarUnreadBadge(window, viewModel);
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

        var notificationTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        notificationTimer.Tick += async (_, _) =>
        {
            if (!notificationSmoke
                || !inputValidated
                || notificationValidationBusy)
            {
                return;
            }

            try
            {
                if (!notificationTestTriggered)
                {
                    var settings = (WpfButton?)window.FindName("SettingsNavButton")
                        ?? throw new InvalidOperationException(
                            "The Settings button was not found for notification validation.");
                    settings.RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent));
                    notificationTestButton =
                        (WpfButton?)window.FindName("TestWindowsNotificationButton")
                        ?? throw new InvalidOperationException(
                            "The test-alert button was not found.");
                    notificationTestTriggered = true;
                    notificationTestButton.RaiseEvent(
                        new RoutedEventArgs(WpfButton.ClickEvent));
                    return;
                }

                if (notificationTestButton?.IsEnabled != true)
                {
                    return;
                }

                var status = (TextBlock?)window.FindName(
                    "WindowsNotificationStatusText")
                    ?? throw new InvalidOperationException(
                        "The Windows notification status was not found.");
                if (!status.Text.StartsWith(
                        "Test alert submitted to Windows.",
                        StringComparison.Ordinal)
                    && !status.Text.StartsWith(
                        "Compatibility test alert sent.",
                        StringComparison.Ordinal))
                {
                    if (status.Text.Contains(
                            "could not create a test alert",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(status.Text);
                    }

                    return;
                }

                notificationValidationBusy = true;
                Console.WriteLine($"Notification path: {status.Text}");
                await window.ClearWindowsNotificationsForValidationAsync();
                notificationTestValidated = true;
                notificationTimer.Stop();
                var chats = (WpfButton?)window.FindName("ChatsNavButton")
                    ?? throw new InvalidOperationException(
                        "The Chats button was not found after notification validation.");
                chats.RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent));
            }
            catch (Exception exception)
            {
                failure ??= exception;
                notificationTimer.Stop();
                CloseHarness(window);
            }
            finally
            {
                notificationValidationBusy = false;
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
                || !notificationTestValidated
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
            notificationTimer.Stop();
            try
            {
                if (startupPending)
                {
                    throw new InvalidOperationException(
                        "A browser workspace exceeded its bounded startup window. "
                        + $"input={inputValidated}, reconnect={reconnectTriggered}, "
                        + $"first creations={sessionCreationCounts.GetValueOrDefault(first.Id)}, "
                        + $"start order={startOrder.Count}, states="
                        + string.Join(
                            ", ",
                            viewModel.Accounts.Select(account =>
                                $"{account.DisplayName}:{account.ConnectionState}")));
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
        Console.WriteLine("Harness main window shown.");
        recoveryTimer.Start();
        inputTimer.Start();
        notificationTimer.Start();
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

        if (!string.Equals(
                System.Windows.Automation.AutomationProperties.GetItemStatus(settings),
                "Current page",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The current Settings page was not exposed to UI Automation.");
        }

        var chats = (WpfButton?)window.FindName("ChatsNavButton")
            ?? throw new InvalidOperationException("The Chats button was not found.");
        chats.RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent));
        if (viewModel.SelectedSection != AppSection.Chats)
        {
            throw new InvalidOperationException("The Chats button did not navigate.");
        }

        if (!string.Equals(
                System.Windows.Automation.AutomationProperties.GetItemStatus(chats),
                "Current page",
                StringComparison.Ordinal)
            || !string.IsNullOrEmpty(
                System.Windows.Automation.AutomationProperties.GetItemStatus(settings)))
        {
            throw new InvalidOperationException(
                "The current Chats page state was not updated for UI Automation.");
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

    private static void ValidateProfileIdentityAndBranding(MainWindow window)
    {
        window.UpdateLayout();
        var card = (FrameworkElement?)window.FindName("SelectedProfileIdentityCard")
            ?? throw new InvalidOperationException(
                "The selected-profile identity card was not found.");
        var nickname = (FrameworkElement?)window.FindName("SelectedProfileNicknameText")
            ?? throw new InvalidOperationException(
                "The centered profile nickname was not found.");
        var editButton = (FrameworkElement?)window.FindName("EditProfileNicknameButton")
            ?? throw new InvalidOperationException(
                "The profile nickname edit button was not found.");
        var logo = (WpfImage?)window.FindName("HeaderBrandLogo")
            ?? throw new InvalidOperationException("The header brand logo was not found.");
        var accountList = (WpfListBox?)window.FindName("AccountList")
            ?? throw new InvalidOperationException("The account list was not found.");

        var nicknameCenter = nickname.TranslatePoint(
            new System.Windows.Point(nickname.ActualWidth / 2, 0),
            card).X;
        var cardCenter = card.ActualWidth / 2;
        if (Math.Abs(nicknameCenter - cardCenter) > 1.5)
        {
            throw new InvalidOperationException(
                $"Profile nickname center {nicknameCenter:F1} does not match card center {cardCenter:F1}.");
        }

        var nicknameRight = nickname.TranslatePoint(
            new System.Windows.Point(nickname.ActualWidth, 0),
            card).X;
        var editLeft = editButton.TranslatePoint(
            new System.Windows.Point(0, 0),
            card).X;
        if (nicknameRight > editLeft)
        {
            throw new InvalidOperationException(
                "The centered profile identity overlaps the Edit action.");
        }

        if (logo.Source is not System.Windows.Media.Imaging.BitmapImage
            { PixelWidth: 512, PixelHeight: 512 })
        {
            throw new InvalidOperationException(
                "The generated SteamSwitchboard logo was not decoded into the header.");
        }

        if (window.Icon is not System.Windows.Media.Imaging.BitmapSource
            { PixelWidth: 512, PixelHeight: 512 }
            || !ReferenceEquals(window.Icon, logo.Source))
        {
            throw new InvalidOperationException(
                "The live window did not use the generated SteamSwitchboard logo bitmap.");
        }

        if (!window.UsesPackagedBrandNotificationIconForValidation)
        {
            throw new InvalidOperationException(
                "The compatibility notification icon did not use the packaged SteamSwitchboard artwork.");
        }

        if (!string.IsNullOrEmpty(
                System.Windows.Automation.AutomationProperties.GetName(logo)))
        {
            throw new InvalidOperationException(
                "The decorative header logo was exposed as duplicate accessible content.");
        }

        if (accountList.ItemContainerGenerator.ContainerFromIndex(0)
                is not ListBoxItem selectedItem)
        {
            throw new InvalidOperationException(
                "The selected profile row was not rendered.");
        }

        selectedItem.ApplyTemplate();
        if (selectedItem.Template.FindName("ItemBorder", selectedItem)
                is not Border selectionBorder
            || selectionBorder.BorderThickness.Left < 3
            || selectionBorder.BorderBrush is not System.Windows.Media.SolidColorBrush accent
            || accent.Color != System.Windows.Media.Color.FromRgb(0x66, 0xC0, 0xF4))
        {
            throw new InvalidOperationException(
                "The selected profile does not have a high-contrast accent marker.");
        }

        var packagedLogoPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Branding",
            "SteamSwitchboard-app-logo.png");
        using var packagedLogo = File.OpenRead(packagedLogoPath);
        var packagedHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(packagedLogo));
        if (!string.Equals(
                packagedHash,
                "B684FFBB817F43B3992B44D06EAA04DBFCADFA4CBDD1F2A86572317F4FB59993",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The packaged header logo does not match the generated brand asset.");
        }

        Console.WriteLine(
            "Profile centering, selected marker, accessible navigation, and generated window/notification branding: PASS");
    }

    private static void ValidateTaskbarUnreadBadge(
        MainWindow window,
        MainViewModel viewModel)
    {
        var taskbar = window.TaskbarItemInfo
            ?? throw new InvalidOperationException(
                "The taskbar status integration was not created.");
        if (taskbar.Overlay is not null)
        {
            throw new InvalidOperationException(
                "The taskbar unread badge was visible without unread messages.");
        }

        var account = viewModel.Accounts[0];
        account.UnreadCount = 1;
        _ = viewModel.AddNotification(
            account,
            new ChatNotificationPayload(
                "Badge validation",
                "Message content remains private",
                DateTimeOffset.UtcNow));
        window.UpdateLayout();
        if (taskbar.Overlay is not { IsFrozen: true }
            || !taskbar.Description.Contains(
                "1 unread Steam message",
                StringComparison.Ordinal)
            || !window.Title.Contains(
                "1 unread message",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The taskbar unread badge did not mirror notification state.");
        }

        var accountList = (WpfListBox?)window.FindName("AccountList")
            ?? throw new InvalidOperationException(
                "The account list was not found for unread accessibility validation.");
        if (accountList.ItemContainerGenerator.ContainerFromItem(account)
                is not ListBoxItem accountItem
            || !System.Windows.Automation.AutomationProperties.GetName(accountItem)
                .Contains("1 unread Steam message", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The account unread count was not exposed to UI Automation.");
        }

        var notificationsButton = (WpfButton?)window.FindName("NotificationsButton")
            ?? throw new InvalidOperationException(
                "The Notifications button was not found for accessibility validation.");
        if (!System.Windows.Automation.AutomationProperties.GetName(
                notificationsButton)
            .Contains("1 unread alert", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The unread-alert count was not exposed to UI Automation.");
        }

        viewModel.ClearNotifications();
        if (taskbar.Overlay is null)
        {
            throw new InvalidOperationException(
                "Clearing alert history incorrectly cleared the unread-message badge.");
        }

        account.UnreadCount = 0;
        if (taskbar.Overlay is not null
            || !taskbar.Description.Contains(
                "no unread Steam messages",
                StringComparison.Ordinal)
            || window.Title.Contains("unread message", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The taskbar unread badge did not clear after messages were read.");
        }

        Console.WriteLine(
            "Numeric taskbar unread-message badge and accessible status lifecycle: PASS");
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
