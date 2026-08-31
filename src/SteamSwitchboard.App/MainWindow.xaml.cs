using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SteamSwitchboard.Controls;
using SteamSwitchboard.Models;
using SteamSwitchboard.Services;
using SteamSwitchboard.ViewModels;

namespace SteamSwitchboard;

public partial class MainWindow : Window
{
    private const int MaximumQueuedWindowsNotifications = 20;
    private const int MaximumSimultaneousChatSessions = 16;
    private const string SettingsTestReplacementTag = "settings-test";

    private readonly MainViewModel _viewModel;
    private readonly AppPaths _paths;
    private readonly GameLaunchService _launcher;
    private readonly Func<AccountViewModel, string, SteamChatSession> _chatSessionFactory;
    private readonly Dictionary<Guid, SteamChatSession> _chatSessions = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly DispatcherTimer _playAccountTimer;
    private readonly DispatcherTimer _balloonReleaseTimer;
    private readonly System.Windows.Forms.NotifyIcon _notificationIcon;
    private readonly WindowsAppNotificationService _appNotifications;
    private readonly Queue<WindowsNotificationDelivery> _pendingBalloonNotifications = [];
    private readonly OrderedCommandQueue<WindowsAppNotificationService> _windowsNotificationCommands;
    private readonly NotificationPrivacyGate _notificationPrivacyGate = new();
    private System.Drawing.Icon? _notificationTrayIcon;
    private bool _notificationTrayIconUsesPackagedBrand;
    private WindowsNotificationDelivery? _activeBalloonNotification;
    private readonly NotificationCleanupGenerationBarrier
        _notificationCleanupBarrier = new();
    private bool _startupStarted;
    private bool _isLoaded;
    private bool _suppressSelectionEvents;
    private bool _legacyNotificationIconVisible;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private int _notificationSettingsRevision;

    internal event Action<AccountViewModel>? ChatSessionInitializationStarted;

    internal bool UsesPackagedBrandNotificationIconForValidation =>
        _notificationTrayIconUsesPackagedBrand;

    public MainWindow(
        MainViewModel viewModel,
        AppPaths paths,
        GameLaunchService launcher)
        : this(viewModel, paths, launcher, null)
    {
    }

    internal MainWindow(
        MainViewModel viewModel,
        AppPaths paths,
        GameLaunchService launcher,
        Func<AccountViewModel, string, SteamChatSession>? chatSessionFactory)
    {
        InitializeComponent();
        LoadBrandingImages();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _chatSessionFactory = chatSessionFactory
            ?? (static (account, browserData) =>
                new SteamChatSession(account, browserData));
        _lifetimeToken = _lifetime.Token;
        DataContext = _viewModel;
        DataFolderText.Text = _paths.Root;
        WindowSizing.ClampToCurrentWorkArea(this, 32);

        _notificationIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "SteamSwitchboard"
        };
        InitializeNotificationIcon();
        _appNotifications = new WindowsAppNotificationService();
        _appNotifications.Activated += OnWindowsAppNotificationActivated;
        _windowsNotificationCommands = new OrderedCommandQueue<WindowsAppNotificationService>(
            _appNotifications);

        _balloonReleaseTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(7)
        };
        _balloonReleaseTimer.Tick += OnBalloonReleaseTimerTick;

        _playAccountTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _playAccountTimer.Tick += (_, _) => UpdateCurrentPlayAccount();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateTaskbarUnreadBadge();
        NavigateTo(_viewModel.SelectedSection);
    }

    private void LoadBrandingImages()
    {
        if (!BrandAssetPolicy.TryOpenAppLogoForRendering(
                AppContext.BaseDirectory,
                out var logoStream)
            || logoStream is null)
        {
            return;
        }

        try
        {
            using (logoStream)
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption =
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.CreateOptions =
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat;
                bitmap.DecodePixelWidth = 512;
                bitmap.StreamSource = logoStream;
                bitmap.EndInit();
                bitmap.Freeze();
                Icon = bitmap;
                HeaderBrandLogo.Source = bitmap;
                AboutBrandLogo.Source = bitmap;
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or InvalidOperationException
                or ArgumentException
                or FileFormatException)
        {
            // The compiled icon and text branding remain usable if a portable install is incomplete.
        }
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_startupStarted)
        {
            return;
        }

        _startupStarted = true;
        var notificationStartup = Task.CompletedTask;
        _suppressSelectionEvents = true;
        try
        {
            await _viewModel.InitializeAsync(_lifetimeToken, refreshGames: false);
            var restoredAccount = _viewModel.SelectedAccount;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            _viewModel.SelectedAccount = restoredAccount;
            AccountList.SelectedItem = restoredAccount;
            notificationStartup = InitializeWindowsNotificationStateAsync();
        }
        catch (OperationCanceledException)
        {
            // Normal during app shutdown.
            return;
        }
        catch (Exception exception)
        {
            _viewModel.StatusMessage = $"Startup needs attention: {exception.Message}";
            MessageBox.Show(
                exception.Message,
                "SteamSwitchboard could not finish starting",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        finally
        {
            _suppressSelectionEvents = false;
            _isLoaded = true;
        }

        ShowSelectedChat();
        UpdateCurrentPlayAccount();
        _playAccountTimer.Start();

        // Let WPF render an enabled, interactive shell before creating any
        // heavyweight browser controllers. Individual workspaces report their
        // own progress and cannot lock navigation or settings.
        await Dispatcher.Yield(DispatcherPriority.Background);
        var gameRefreshTask = RefreshGamesAfterStartupAsync();
        try
        {
            await notificationStartup;
            await ResumePendingProfileDeletionsAsync();
            await InitializeChatSessionsAsync();
        }
        catch (OperationCanceledException)
        {
            // Normal during app shutdown.
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or COMException)
        {
            _viewModel.StatusMessage =
                $"One or more chat workspaces need attention: {exception.Message}";
        }

        await gameRefreshTask;
    }

    private async Task RefreshGamesAfterStartupAsync()
    {
        try
        {
            await _viewModel.RefreshGamesAsync(_lifetimeToken);
        }
        catch (OperationCanceledException)
        {
            // Normal during app shutdown.
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
        {
            _viewModel.StatusMessage =
                "The local Steam library could not be refreshed. Chats and settings are still available.";
        }
    }

    private async Task InitializeChatSessionsAsync()
    {
        var selected = _viewModel.SelectedAccount;
        if (selected is not null
            && !_viewModel.IsAccountDeletionPending(selected.Id))
        {
            await EnsureChatSessionAsync(selected);
            _lifetimeToken.ThrowIfCancellationRequested();
        }

        if (_viewModel.KeepAllChatsLive)
        {
            var backgroundAccounts = _viewModel.Accounts
                .Where(account =>
                    !ReferenceEquals(account, selected)
                    && !_viewModel.IsAccountDeletionPending(account.Id))
                .Take(Math.Max(
                    0,
                    MaximumSimultaneousChatSessions - _chatSessions.Count))
                .ToArray();
            foreach (var account in backgroundAccounts)
            {
                _lifetimeToken.ThrowIfCancellationRequested();
                if (!_viewModel.KeepAllChatsLive)
                {
                    break;
                }

                // Controller creation must happen on the UI thread. Starting
                // one at a time and yielding at input-safe boundaries prevents
                // a burst of background profiles from monopolising WPF.
                await Dispatcher.Yield(DispatcherPriority.Background);
                await EnsureChatSessionAsync(account);
                _lifetimeToken.ThrowIfCancellationRequested();
            }

            _lifetimeToken.ThrowIfCancellationRequested();
            foreach (var account in _viewModel.Accounts.Where(account =>
                         !_chatSessions.ContainsKey(account.Id)
                         && !_viewModel.IsAccountDeletionPending(account.Id)))
            {
                account.ConnectionState = ChatConnectionState.Dormant;
            }

            if (_viewModel.Accounts.Count > 1)
            {
                var pendingCount = _viewModel.Accounts.Count(account =>
                    _viewModel.IsAccountDeletionPending(account.Id));
                var eligibleCount = _viewModel.Accounts.Count - pendingCount;
                var deferredCount = Math.Max(
                    0,
                    eligibleCount - _chatSessions.Count);
                _viewModel.StatusMessage = pendingCount > 0
                    ? $"{_chatSessions.Count} chat workspaces open · {pendingCount} cleanup pending"
                    : deferredCount > 0
                        ? $"{_chatSessions.Count} chat workspaces open · {deferredCount} open when selected"
                        : $"Keeping {_chatSessions.Count} chat workspaces available";
            }
        }
        else
        {
            foreach (var account in _viewModel.Accounts.Where(
                         account => !_chatSessions.ContainsKey(account.Id)))
            {
                account.ConnectionState = ChatConnectionState.Dormant;
            }
        }

        ShowSelectedChat();
    }

    private async Task<SteamChatSession> EnsureChatSessionAsync(
        AccountViewModel account,
        bool forProfileCleanup = false)
    {
        if (!forProfileCleanup && _viewModel.IsAccountDeletionPending(account.Id))
        {
            throw new InvalidOperationException(
                "This account is waiting for its local sign-in data to be removed. Restart Switchboard to retry the cleanup.");
        }

        if (_chatSessions.TryGetValue(account.Id, out var existing))
        {
            if (forProfileCleanup)
            {
                await existing.InitializeForCleanupAsync();
            }
            else
            {
                await existing.InitializeAsync();
                ShowSelectedChat();
            }

            return existing;
        }

        if (_chatSessions.Count >= MaximumSimultaneousChatSessions)
        {
            ReleaseLeastRecentlyUsedSession(account.Id);
        }

        var session = _chatSessionFactory(account, _paths.BrowserData);
        session.ChatNotificationReceived += OnChatNotificationReceived;
        session.ChatReadyWhileVisible += OnChatReadyWhileVisible;
        session.ReconnectSessionRequested += OnReconnectSessionRequested;
        _chatSessions.Add(account.Id, session);
        ChatSessionContainer.Children.Add(session);
        if (!forProfileCleanup
            && _viewModel.SelectedSection == AppSection.Chats
            && ReferenceEquals(_viewModel.SelectedAccount, account))
        {
            session.ShowChat();
        }
        else
        {
            session.PrepareForBackgroundInitialization(
                _viewModel.KeepAllChatsLive);
        }

        // WebView2 needs a connected WPF visual before explicit
        // initialization. This yield also prevents a browser launch from
        // monopolising the input event that selected the account.
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        ChatSessionInitializationStarted?.Invoke(account);
        if (forProfileCleanup)
        {
            await session.InitializeForCleanupAsync();
        }
        else
        {
            await session.InitializeAsync();
        }

        if (!forProfileCleanup)
        {
            ShowSelectedChat();
        }

        return session;
    }

    private void ReleaseLeastRecentlyUsedSession(Guid incomingAccountId)
    {
        var candidate = _chatSessions.Values
            .Where(session => session.Account.Id != incomingAccountId)
            .OrderBy(session => session.Account.Profile.LastUsedUtc)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "A chat workspace could not be opened safely. Close and restart Switchboard, then try again.");
        ReleaseChatSession(candidate, markDormant: true);
    }

    private void ReleaseChatSession(
        SteamChatSession session,
        bool markDormant)
    {
        DetachChatSessionFromHost(session);
        session.Dispose();
        if (markDormant)
        {
            session.Account.ConnectionState = ChatConnectionState.Dormant;
            session.Account.IsSleeping = false;
        }
    }

    private void DetachChatSessionFromHost(SteamChatSession session)
    {
        _chatSessions.Remove(session.Account.Id);
        ChatSessionContainer.Children.Remove(session);
        session.ChatNotificationReceived -= OnChatNotificationReceived;
        session.ChatReadyWhileVisible -= OnChatReadyWhileVisible;
        session.ReconnectSessionRequested -= OnReconnectSessionRequested;
    }

    private async Task ResumePendingProfileDeletionsAsync()
    {
        foreach (var account in _viewModel.AccountsPendingBrowserProfileDeletion.ToArray())
        {
            _lifetimeToken.ThrowIfCancellationRequested();
            try
            {
                await CompletePendingProfileDeletionAsync(account);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException
                    or COMException)
            {
                account.ConnectionState = ChatConnectionState.Failed;
                _viewModel.StatusMessage =
                    $"Cleanup for {account.DisplayName} is still pending and will be retried next time.";
            }
        }
    }

    private async Task CompletePendingProfileDeletionAsync(
        AccountViewModel account)
    {
        SteamChatSession? session = null;
        try
        {
            session = await EnsureChatSessionAsync(
                account,
                forProfileCleanup: true);
            DetachChatSessionFromHost(session);
            await session.ClearSessionAsync();
            _suppressSelectionEvents = true;
            try
            {
                await _viewModel.RemoveAccountAsync(account, _lifetimeToken);
                AccountList.SelectedItem = _viewModel.SelectedAccount;
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }
        finally
        {
            if (session is null
                && _chatSessions.TryGetValue(account.Id, out var trackedSession))
            {
                session = trackedSession;
            }

            if (session is not null)
            {
                DetachChatSessionFromHost(session);
                session.Dispose();
            }
        }
    }

    private void ShowSelectedChat()
    {
        foreach (var (accountId, session) in _chatSessions)
        {
            if (_viewModel.SelectedSection == AppSection.Chats
                && _viewModel.SelectedAccount?.Id == accountId
                && !_viewModel.IsAccountDeletionPending(accountId))
            {
                if (session.ShowChat() && CanMarkSelectedChatRead(accountId))
                {
                    MarkAccountRead(session.Account);
                }
            }
            else
            {
                session.HideChat(_viewModel.KeepAllChatsLive);
            }
        }
    }

    private async void OnAddAccountClicked(object sender, RoutedEventArgs e)
    {
        if (!EnsureStartupComplete())
        {
            return;
        }

        var dialog = new AddAccountWindow(_viewModel.Accounts.Select(account => account.Profile))
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        try
        {
            var account = await _viewModel.AddAccountAsync(dialog.Result, _lifetimeToken);
            AccountList.SelectedItem = account;
            NavigateTo(AppSection.Chats);
            await EnsureChatSessionAsync(account);
            ShowSelectedChat();
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            MessageBox.Show(
                exception.Message,
                "Account could not be added",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void OnRenameAccountClicked(object sender, RoutedEventArgs e)
    {
        if (!EnsureStartupComplete()
            || _viewModel.SelectedAccount is not { } account)
        {
            return;
        }

        var dialog = new EditAccountWindow(
            account.Profile,
            _viewModel.Accounts.Select(item => item.Profile))
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.ResultName is null)
        {
            return;
        }

        try
        {
            await _viewModel.RenameAccountAsync(
                account,
                dialog.ResultName,
                _lifetimeToken);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            MessageBox.Show(
                exception.Message,
                "Profile name could not be saved",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void OnRelinkSteamLoginClicked(object sender, RoutedEventArgs e)
    {
        if (!EnsureStartupComplete()
            || _viewModel.SelectedAccount is not { } account)
        {
            return;
        }

        var steamRoot = Path.GetDirectoryName(_viewModel.SteamExecutablePath);
        var detectedAccounts = string.IsNullOrWhiteSpace(steamRoot)
            ? []
            : new SteamClientAccountService().LoadAccounts(steamRoot);
        if (detectedAccounts.Count == 0)
        {
            MessageBox.Show(
                "No local Steam logins could be detected. Start Steam, finish signing in, then try again.",
                "Steam login not detected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new RelinkSteamAccountWindow(
            account.Profile,
            detectedAccounts)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true
            || string.IsNullOrWhiteSpace(dialog.ResultLoginName))
        {
            return;
        }

        try
        {
            await _viewModel.RelinkSteamLoginAsync(
                account,
                dialog.ResultLoginName,
                _lifetimeToken);
            UpdateCurrentPlayAccount();
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            MessageBox.Show(
                exception.Message,
                "Steam login could not be relinked",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void OnAccountSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || _suppressSelectionEvents)
        {
            return;
        }

        if (sender is not ListBox { SelectedItem: AccountViewModel selectedAccount })
        {
            return;
        }

        _viewModel.SelectedAccount = selectedAccount;

        ShowSelectedChat();
        if (_viewModel.SelectedAccount is { } account)
        {
            try
            {
                await EnsureChatSessionAsync(account);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or IOException)
            {
                _viewModel.StatusMessage = exception.Message;
            }
        }

        try
        {
            await _viewModel.SaveAsync(_lifetimeToken);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or OperationCanceledException)
        {
            if (exception is not OperationCanceledException)
            {
                _viewModel.StatusMessage = "The selected account could not be saved.";
            }
        }
    }

    private void OnChatsNavClicked(object sender, RoutedEventArgs e) => NavigateTo(AppSection.Chats);

    private void OnLibraryNavClicked(object sender, RoutedEventArgs e) => NavigateTo(AppSection.Library);

    private void OnSettingsNavClicked(object sender, RoutedEventArgs e) => NavigateTo(AppSection.Settings);

    private void NavigateTo(AppSection section)
    {
        _viewModel.SelectedSection = section;
        SetNavStyle(ChatsNavButton, section == AppSection.Chats);
        SetNavStyle(LibraryNavButton, section == AppSection.Library);
        SetNavStyle(SettingsNavButton, section == AppSection.Settings);
        if (section == AppSection.Chats)
        {
            ShowSelectedChat();
        }
        else
        {
            foreach (var session in _chatSessions.Values)
            {
                session.HideChat(_viewModel.KeepAllChatsLive);
            }
        }
    }

    private bool EnsureStartupComplete()
    {
        if (_isLoaded)
        {
            return true;
        }

        _viewModel.StatusMessage = "Finishing startup…";
        return false;
    }

    private void SetNavStyle(Button button, bool isSelected)
    {
        button.Style = (Style)FindResource(isSelected ? "SecondaryButton" : "QuietButton");
        System.Windows.Automation.AutomationProperties.SetItemStatus(
            button,
            isSelected ? "Current page" : string.Empty);
    }

    private async void OnRefreshGamesClicked(object sender, RoutedEventArgs e)
    {
        if (!EnsureStartupComplete())
        {
            return;
        }

        _viewModel.IsBusy = true;
        _viewModel.StatusMessage = "Refreshing the Steam library…";
        try
        {
            await _viewModel.RefreshGamesAsync(_lifetimeToken);
            _viewModel.StatusMessage = $"Found {_viewModel.Games.Count} local Steam library items";
        }
        catch (OperationCanceledException)
        {
            // Normal during app shutdown.
        }
        finally
        {
            _viewModel.IsBusy = false;
        }
    }

    private async void OnPlayAccountSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_isLoaded || _suppressSelectionEvents)
        {
            return;
        }

        try
        {
            await _viewModel.SaveAsync(_lifetimeToken);
            if (_viewModel.SelectedPlayAccount is { } account)
            {
                _viewModel.StatusMessage =
                    $"Steam launches now require login {account.SteamLoginName}";
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or OperationCanceledException)
        {
            if (exception is not OperationCanceledException)
            {
                _viewModel.StatusMessage = "The required Steam account choice could not be saved.";
            }
        }
    }

    private async void OnConversationSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        try
        {
            var chatLivenessChanged = ReferenceEquals(
                sender,
                KeepChatsLiveCheckBox);
            var notificationSettingChanged = ReferenceEquals(
                    sender,
                    WindowsNotificationsCheckBox)
                || ReferenceEquals(sender, NotificationPreviewsCheckBox);
            var previewsWereDisabled = ReferenceEquals(
                    sender,
                    NotificationPreviewsCheckBox)
                && NotificationPreviewsCheckBox.IsChecked != true;
            var notificationsWereDisabled = ReferenceEquals(
                    sender,
                    WindowsNotificationsCheckBox)
                && WindowsNotificationsCheckBox.IsChecked != true;
            int? notificationSettingsRevision = null;
            if (notificationSettingChanged)
            {
                notificationSettingsRevision = Interlocked.Increment(
                    ref _notificationSettingsRevision);
                if (previewsWereDisabled || notificationsWereDisabled)
                {
                    RevokeNotificationPrivacy();
                    _ = _viewModel.MarkWindowsNotificationCleanupPending(
                        accountId: null);
                }
                else
                {
                    _ = _viewModel.RenewWindowsNotificationCleanupRequest();
                }
            }

            await _viewModel.SaveAsync(_lifetimeToken);
            if (notificationSettingsRevision is int revision
                && revision == Volatile.Read(ref _notificationSettingsRevision))
            {
                await ApplyNotificationSettingsAsync(revision);
            }

            if (chatLivenessChanged)
            {
                if (_viewModel.KeepAllChatsLive)
                {
                    await InitializeChatSessionsAsync();
                }
                else
                {
                    ShowSelectedChat();
                    _viewModel.StatusMessage =
                        "Background chats will sleep until selected";
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or OperationCanceledException)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                _viewModel.StatusMessage = "Conversation settings could not be saved.";
            }
        }
    }

    private async void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        if (!EnsureStartupComplete()
            || sender is not Button { Tag: InstalledGame game }
            || _viewModel.SelectedPlayAccount is not { } account)
        {
            return;
        }

        if (!_viewModel.TryBeginLaunchCheck())
        {
            _viewModel.StatusMessage =
                "A Steam launch check is already running.";
            return;
        }

        _viewModel.StatusMessage =
            $"Checking Steam login before launching {game.Name}…";
        var launchAccount = new AccountProfile
        {
            Id = account.Id,
            DisplayName = account.DisplayName,
            SteamLoginName = account.SteamLoginName,
            AccentHex = account.AccentHex,
            CreatedUtc = account.Profile.CreatedUtc,
            LastUsedUtc = account.Profile.LastUsedUtc
        };
        var configuredSteamPath = _viewModel.SteamExecutablePath;
        LaunchAssessment assessment;
        try
        {
            assessment = await Task.Run(
                () => _launcher.LaunchIfReady(
                    launchAccount,
                    game,
                    configuredSteamPath,
                    _lifetimeToken),
                _lifetimeToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or Win32Exception
                or IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            MessageBox.Show(
                exception.Message,
                "Library item could not be started",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        finally
        {
            _viewModel.EndLaunchCheck();
        }

        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        if (assessment.CanLaunch)
        {
            _viewModel.StatusMessage = $"Launch request sent to Steam for {game.Name}";
            return;
        }

        if (assessment.Readiness is LaunchReadiness.SteamNotRunning
            or LaunchReadiness.ActiveAccountUnknown
            or LaunchReadiness.AccountSwitchRequired)
        {
            var switchWindow = new AccountSwitchWindow(
                launchAccount,
                game,
                configuredSteamPath,
                _launcher)
            {
                Owner = this
            };
            _ = switchWindow.ShowDialog();
            return;
        }

        MessageBox.Show(
            assessment.Message,
            "Cannot start this library item yet",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void OnBrowseSteamClicked(object sender, RoutedEventArgs e)
    {
        if (!EnsureStartupComplete())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choose Steam",
            Filter = "Steam executable (steam.exe)|steam.exe",
            CheckFileExists = true,
            Multiselect = false,
            FileName = "steam.exe"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var validatedSteam = _viewModel.ValidateSteamExecutableCandidate(dialog.FileName);
        if (validatedSteam is null)
        {
            MessageBox.Show(
                "Choose the locally installed steam.exe signed by Valve. Remote, linked, renamed, or unsigned files are not accepted.",
                "Steam location was not accepted",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _viewModel.SteamExecutablePath = validatedSteam;
        try
        {
            await _viewModel.SaveAsync(_lifetimeToken);
            await _viewModel.RefreshGamesAsync(_lifetimeToken);
            _viewModel.StatusMessage = "Steam location updated";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            MessageBox.Show(
                exception.Message,
                "Steam location could not be saved",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnOpenDataFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _paths.EnsureCreated();
            using var process = Process.Start(new ProcessStartInfo(_paths.Root)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            MessageBox.Show(
                "Windows could not open the local data folder.",
                "Folder could not be opened",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void OnForgetAccountClicked(object sender, RoutedEventArgs e)
    {
        if (!EnsureStartupComplete()
            || _viewModel.SelectedAccount is not { } account)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            $"Forget {account.DisplayName} on this PC?\n\n"
            + "This clears its local Steam web session and removes the profile from Switchboard. "
            + "It does not change or delete the Steam account.",
            "Forget account",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        RevokeNotificationPrivacy();
        try
        {
            await _viewModel.MarkAccountDeletionPendingAsync(account, _lifetimeToken);
            _viewModel.ClearNotifications(account.Id);
            await PurgeWindowsNotificationsAsync(account.Id);
            await CompletePendingProfileDeletionAsync(account);
            ShowSelectedChat();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or COMException
                or OperationCanceledException)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                MessageBox.Show(
                    exception.Message,
                    "Account could not be forgotten",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private void UpdateCurrentPlayAccount()
    {
        if (_viewModel.Accounts.Count == 0)
        {
            _viewModel.NativeSteamAccountState = NativeSteamAccountState.NoProfiles;
            _viewModel.CurrentSteamAccountStatus =
                "Current in Steam: add a Switchboard account first";
            return;
        }

        foreach (var account in _viewModel.Accounts)
        {
            account.IsActiveInSteam = false;
        }

        var steamExecutable = _viewModel.SteamExecutablePath;
        if (string.IsNullOrWhiteSpace(steamExecutable)
            || !GameLaunchService.IsSteamRunning(steamExecutable))
        {
            _viewModel.NativeSteamAccountState = NativeSteamAccountState.SteamNotRunning;
            _viewModel.CurrentSteamAccountStatus =
                "Current in Steam: Steam is not running";
            _viewModel.NotifyCurrentPlayAccountChanged();
            return;
        }

        var steamRoot = Path.GetDirectoryName(steamExecutable);
        if (string.IsNullOrWhiteSpace(steamRoot))
        {
            _viewModel.NativeSteamAccountState = NativeSteamAccountState.Unknown;
            _viewModel.CurrentSteamAccountStatus =
                "Current in Steam: not detected";
            _viewModel.NotifyCurrentPlayAccountChanged();
            return;
        }

        var activeAccount = new SteamClientAccountService().FindActiveAccount(steamRoot);
        if (activeAccount is null)
        {
            _viewModel.NativeSteamAccountState = NativeSteamAccountState.Unknown;
            _viewModel.CurrentSteamAccountStatus =
                "Current in Steam: sign-in not detected";
            _viewModel.NotifyCurrentPlayAccountChanged();
            return;
        }

        var match = _viewModel.Accounts.FirstOrDefault(account =>
            string.Equals(
                account.SteamLoginName,
                activeAccount.AccountName,
                StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            match.IsActiveInSteam = true;
            _viewModel.NativeSteamAccountState = NativeSteamAccountState.Match;
            _viewModel.CurrentSteamAccountStatus =
                $"Active in Steam: {match.SteamLoginName} (profile: {match.DisplayName})";
        }
        else
        {
            _viewModel.NativeSteamAccountState = NativeSteamAccountState.Mismatch;
            _viewModel.CurrentSteamAccountStatus =
                $"Active in Steam: {activeAccount.AccountName} ({activeAccount.PersonaName}), not linked to a Switchboard profile";
        }

        _viewModel.NotifyCurrentPlayAccountChanged();
    }

    private void InitializeNotificationIcon()
    {
        System.Drawing.Icon? icon = null;
        try
        {
            icon = TryLoadPackagedNotificationIcon();
            var usesPackagedBrand = icon is not null;
            if (icon is null)
            {
                var processPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(processPath))
                {
                    icon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
                }
            }

            if (icon is null)
            {
                return;
            }

            _notificationIcon.Icon = icon;
            _notificationIcon.BalloonTipClicked += OnNotificationBalloonClicked;
            _notificationIcon.DoubleClick += OnNotificationIconDoubleClicked;
            _notificationTrayIcon = icon;
            _notificationTrayIconUsesPackagedBrand = usesPackagedBrand;
            icon = null;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or FileNotFoundException
                or Win32Exception)
        {
            // In-app notifications remain available when Windows cannot create a tray icon.
        }
        finally
        {
            icon?.Dispose();
        }
    }

    private static System.Drawing.Icon? TryLoadPackagedNotificationIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri(
                "/SteamSwitchboard;component/Assets/SteamSwitchboard.ico",
                UriKind.Relative));
            if (resource?.Stream is null)
            {
                return null;
            }

            using (resource.Stream)
            using (var source = new System.Drawing.Icon(resource.Stream))
            {
                return (System.Drawing.Icon)source.Clone();
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or InvalidOperationException
                or NotSupportedException
                or System.Security.SecurityException)
        {
            return null;
        }
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (_shutdownStarted
            || (!string.IsNullOrEmpty(e.PropertyName)
                && e.PropertyName != nameof(MainViewModel.UnreadMessageCount)))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(UpdateTaskbarUnreadBadge);
            return;
        }

        UpdateTaskbarUnreadBadge();
    }

    private void UpdateTaskbarUnreadBadge()
    {
        if (_shutdownStarted || TaskbarItemInfo is null)
        {
            return;
        }

        var unreadCount = Math.Max(0, _viewModel.UnreadMessageCount);
        TaskbarItemInfo.Overlay = TaskbarUnreadBadge.CreateOverlay(unreadCount);
        TaskbarItemInfo.Description = unreadCount switch
        {
            0 => "SteamSwitchboard — no unread Steam messages",
            1 => "SteamSwitchboard — 1 unread Steam message",
            > 99 => "SteamSwitchboard — 99 or more unread Steam messages",
            _ => $"SteamSwitchboard — {unreadCount} unread Steam messages"
        };
    }

    private void OnChatNotificationReceived(
        object? sender,
        ChatNotificationEventArgs e)
    {
        if (sender is not SteamChatSession session
            || !ReferenceEquals(session.Account, e.Account))
        {
            e.ReportClosed?.Invoke();
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                () => HandleChatNotification(
                    session,
                    e.Notification,
                    e.ReportClicked,
                    e.ReportClosed));
            return;
        }

        HandleChatNotification(
            session,
            e.Notification,
            e.ReportClicked,
            e.ReportClosed);
    }

    private void OnChatReadyWhileVisible(object? sender, EventArgs e)
    {
        if (sender is SteamChatSession session
            && IsTrackedChatSession(session)
            && session.IsWorkspaceVisibleForReadState
            && CanMarkSelectedChatRead(session.Account.Id))
        {
            MarkAccountRead(session.Account);
        }
    }

    private async void OnReconnectSessionRequested(object? sender, EventArgs e)
    {
        if (sender is not SteamChatSession session
            || !_chatSessions.TryGetValue(session.Account.Id, out var tracked)
            || !ReferenceEquals(session, tracked)
            || _viewModel.IsAccountDeletionPending(session.Account.Id))
        {
            return;
        }

        var account = session.Account;
        ReleaseChatSession(session, markDormant: false);
        try
        {
            await EnsureChatSessionAsync(account);
        }
        catch (OperationCanceledException)
        {
            // Normal during app shutdown.
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or COMException)
        {
            account.ConnectionState = ChatConnectionState.Failed;
            _viewModel.StatusMessage =
                "That chat workspace could not be recreated. Restart Switchboard and try again.";
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (_isLoaded && _viewModel.SelectedSection == AppSection.Chats)
        {
            ShowSelectedChat();
        }
    }

    internal void HandleChatNotification(
        SteamChatSession session,
        ChatNotificationPayload payload,
        Action? reportClicked,
        Action? reportClosed)
    {
        var account = session.Account;
        if (!IsTrackedChatSession(session)
            || !_viewModel.Accounts.Contains(account)
            || _viewModel.IsAccountDeletionPending(account.Id))
        {
            reportClosed?.Invoke();
            return;
        }

        var notification = _viewModel.AddNotification(
            account,
            payload,
            reportClicked,
            reportClosed);
        var isSelectedConversation = _viewModel.SelectedSection == AppSection.Chats
            && ReferenceEquals(_viewModel.SelectedAccount, account);
        var unreadDecision = ChatUnreadLifecycle.ResolveAfterNotification(
            account.UnreadCount,
            isSelectedConversation,
            session.IsWorkspaceVisibleForReadState);
        account.UnreadCount = unreadDecision.UnreadCount;
        if (unreadDecision.ShouldShowWindowsNotification)
        {
            ShowWindowsNotification(notification);
        }
        else if (unreadDecision.ShouldMarkRead)
        {
            MarkAccountRead(account);
        }

        _viewModel.StatusMessage =
            $"Steam notification for {account.DisplayName}: {payload.SteamTitle}";
    }

    private bool IsTrackedChatSession(SteamChatSession session) =>
        _chatSessions.TryGetValue(session.Account.Id, out var tracked)
        && ReferenceEquals(session, tracked);

    private bool CanMarkSelectedChatRead(Guid accountId) =>
        IsActive
        && WindowState != WindowState.Minimized
        && _viewModel.SelectedSection == AppSection.Chats
        && _viewModel.SelectedAccount?.Id == accountId;

    private void MarkAccountRead(AccountViewModel account)
    {
        account.UnreadCount = 0;
        _viewModel.MarkNotificationsRead(account.Id);
    }

    private async void ShowWindowsNotification(ChatNotificationViewModel notification)
    {
        try
        {
            await DeliverWindowsNotificationAsync(notification);
        }
        catch (Exception exception) when (
            _lifetime.IsCancellationRequested
                && exception is (OperationCanceledException
                    or ObjectDisposedException))
        {
            // A queued browser event may finish while the window is closing.
        }
    }

    private async Task DeliverWindowsNotificationAsync(
        ChatNotificationViewModel notification)
    {
        if (!_viewModel.EnableWindowsNotifications)
        {
            return;
        }

        var delivery = new WindowsNotificationDelivery(
            notification.AccountId,
            notification.Id,
            notification.AccountDisplayName,
            notification.AccountLoginName,
            notification.SteamTitle,
            notification.Preview,
            notification.ReplacementTag,
            _viewModel.ShowNotificationPreviews,
            _notificationPrivacyGate.Capture(),
            IsTest: false);
        var cleanupReady =
            await EnsurePendingWindowsNotificationCleanupAsync();
        var modernShown = cleanupReady
            && await _windowsNotificationCommands.EnqueueAsync(
                service => service.TryEnable()
                    && _notificationPrivacyGate.ExecuteIfCurrent(
                        delivery.PrivacyRevision,
                        () => service.TryShow(
                            delivery.AccountId,
                            delivery.NotificationId,
                            delivery.AccountIdentity,
                            delivery.SteamTitle,
                            delivery.Preview,
                            delivery.ReplacementTag)),
                rejectedResult: false,
                cancellationToken: _lifetimeToken);
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        var accountStillExists = delivery.AccountId is not Guid accountId
            || (_viewModel.Accounts.Any(account => account.Id == accountId)
                && !_viewModel.IsAccountDeletionPending(accountId));
        var previewWasRevoked =
            (delivery.PreviewWasEnabled && !_viewModel.ShowNotificationPreviews)
            || !_notificationPrivacyGate.IsCurrent(delivery.PrivacyRevision);
        if (!_viewModel.EnableWindowsNotifications
            || !accountStillExists
            || previewWasRevoked)
        {
            if (modernShown)
            {
                _ = await _windowsNotificationCommands.EnqueueAsync(
                    service =>
                    {
                        service.Remove(delivery.AccountId);
                        return true;
                    },
                    rejectedResult: false,
                    cancellationToken: _lifetimeToken);
            }

            return;
        }

        var compatibilityQueued = false;
        if (!modernShown)
        {
            compatibilityQueued = QueueLegacyWindowsNotification(delivery);
        }

        UpdateWindowsNotificationStatus(
            compatibilityQueued: compatibilityQueued);
    }

    private void RevokeNotificationPrivacy()
    {
        _notificationPrivacyGate.Revoke();
        _pendingBalloonNotifications.Clear();
        _activeBalloonNotification = null;
        _balloonReleaseTimer.Stop();
        _ = TrySetNotificationIconVisible(false);
    }

    private bool QueueLegacyWindowsNotification(WindowsNotificationDelivery delivery)
    {
        if (_notificationTrayIcon is null
            || !_notificationPrivacyGate.IsCurrent(delivery.PrivacyRevision))
        {
            return false;
        }

        if (!TrySetNotificationIconVisible(true))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(delivery.ReplacementTag))
        {
            var retained = _pendingBalloonNotifications.Where(existing =>
                !WindowsNotificationDeliveryPolicy.IsSameReplacement(
                    existing.AccountId,
                    existing.ReplacementTag,
                    existing.IsTest,
                    delivery.AccountId,
                    delivery.ReplacementTag,
                    delivery.IsTest)).ToArray();
            _pendingBalloonNotifications.Clear();
            foreach (var existing in retained)
            {
                _pendingBalloonNotifications.Enqueue(existing);
            }
        }

        while (_pendingBalloonNotifications.Count
               >= MaximumQueuedWindowsNotifications)
        {
            _ = _pendingBalloonNotifications.Dequeue();
        }

        _pendingBalloonNotifications.Enqueue(delivery);
        return ShowNextWindowsNotification();
    }

    private bool ShowNextWindowsNotification()
    {
        if (_activeBalloonNotification is not null)
        {
            return true;
        }

        if (!_legacyNotificationIconVisible)
        {
            return false;
        }

        WindowsNotificationDelivery? notification = null;
        while (_pendingBalloonNotifications.TryDequeue(out var candidate))
        {
            var accountStillExists = candidate.AccountId is not Guid accountId
                || (_viewModel.Accounts.Any(account => account.Id == accountId)
                    && !_viewModel.IsAccountDeletionPending(accountId));
            if (_notificationPrivacyGate.IsCurrent(candidate.PrivacyRevision)
                && _viewModel.EnableWindowsNotifications
                && accountStillExists)
            {
                notification = candidate;
                break;
            }
        }

        if (notification is null)
        {
            return false;
        }

        try
        {
            _activeBalloonNotification = notification;
            var title = SafeText.SanitizeDisplayText(
                $"{notification.AccountDisplayName} • {notification.SteamTitle}",
                "SteamSwitchboard message",
                60);
            _notificationIcon.ShowBalloonTip(
                5_000,
                title,
                notification.Preview,
                System.Windows.Forms.ToolTipIcon.Info);
            _balloonReleaseTimer.Stop();
            _balloonReleaseTimer.Interval = TimeSpan.FromSeconds(7);
            _balloonReleaseTimer.Start();
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or Win32Exception)
        {
            _activeBalloonNotification = null;
            _pendingBalloonNotifications.Clear();
            _ = TrySetNotificationIconVisible(false);
            return false;
        }
    }

    private void OnNotificationsClicked(object sender, RoutedEventArgs e)
    {
        NotificationPopup.IsOpen = !NotificationPopup.IsOpen;
    }

    private async void OnClearNotificationsClicked(object sender, RoutedEventArgs e)
    {
        RevokeNotificationPrivacy();
        _viewModel.ClearNotifications();
        try
        {
            var windowsCleanupConfirmed =
                await PurgeWindowsNotificationsAsync(accountId: null);
            NotificationPopup.IsOpen = false;
            _viewModel.StatusMessage = windowsCleanupConfirmed
                ? "Notification history cleared"
                : "In-app history cleared. Windows could not confirm cleanup yet, so Switchboard will retry.";
        }
        catch (Exception exception) when (
            _lifetime.IsCancellationRequested
                && exception is (OperationCanceledException
                    or ObjectDisposedException))
        {
            // Normal when the app closes while Windows history is clearing.
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            _viewModel.StatusMessage =
                "In-app history was cleared, but the Windows cleanup request could not be saved. Try Clear again.";
        }
    }

    private async Task<bool> PurgeWindowsNotificationsAsync(Guid? accountId)
    {
        _ = _viewModel.MarkWindowsNotificationCleanupPending(accountId);
        await _viewModel.SaveAsync(_lifetimeToken);
        var windowsCleanupConfirmed =
            await EnsurePendingWindowsNotificationCleanupAsync();

        var retained = _pendingBalloonNotifications
            .Where(notification => accountId is not null
                && notification.AccountId != accountId.Value)
            .ToArray();
        _pendingBalloonNotifications.Clear();
        foreach (var notification in retained)
        {
            _pendingBalloonNotifications.Enqueue(notification);
        }

        var removeActive = _activeBalloonNotification is not null
            && (accountId is null
                || _activeBalloonNotification.AccountId == accountId.Value);
        if (!removeActive)
        {
            return windowsCleanupConfirmed;
        }

        _activeBalloonNotification = null;
        _balloonReleaseTimer.Stop();
        var shouldRemainVisible = _viewModel.EnableWindowsNotifications
            && _notificationTrayIcon is not null;
        _ = TrySetNotificationIconVisible(false);
        _ = TrySetNotificationIconVisible(shouldRemainVisible);
        if (_pendingBalloonNotifications.Count > 0)
        {
            QueueNextWindowsNotification();
        }

        return windowsCleanupConfirmed;
    }

    internal async Task<bool> ClearWindowsNotificationsForValidationAsync()
    {
        var retained = _pendingBalloonNotifications
            .Where(notification => !notification.IsTest)
            .ToArray();
        _pendingBalloonNotifications.Clear();
        foreach (var notification in retained)
        {
            _pendingBalloonNotifications.Enqueue(notification);
        }

        if (_activeBalloonNotification?.IsTest == true)
        {
            _activeBalloonNotification = null;
            _balloonReleaseTimer.Stop();
            var shouldRemainVisible = _viewModel.EnableWindowsNotifications
                && _notificationTrayIcon is not null;
            _ = TrySetNotificationIconVisible(false);
            _ = TrySetNotificationIconVisible(shouldRemainVisible);
            if (_pendingBalloonNotifications.Count > 0)
            {
                QueueNextWindowsNotification();
            }
        }

        var modernCleanupRequired = _appNotifications.IsReady;
        var modernCleanupConfirmed = await _windowsNotificationCommands.EnqueueAsync(
            static service => service.RemoveTestNotifications(),
            rejectedResult: false,
            cancellationToken: _lifetimeToken);
        return !modernCleanupRequired || modernCleanupConfirmed;
    }

    private async void OnNotificationClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChatNotificationViewModel notification })
        {
            notification.ReportClickedAndClose();
            await OpenNotificationAsync(notification.AccountId);
        }
    }

    private void OnNotificationBalloonClicked(object? sender, EventArgs e)
    {
        // Legacy NotifyIcon callbacks carry no notification identifier. A
        // delayed callback must never be mapped to the timer-rotated delivery.
        // The in-app center is authoritative and labels every account.
        Dispatcher.BeginInvoke(OpenNotificationCenter);
    }

    private void OpenNotificationCenter()
    {
        ShowAndActivateWindow();
        NotificationPopup.IsOpen = true;
        NotificationsButton.Focus();
    }

    private void QueueNextWindowsNotification()
    {
        _balloonReleaseTimer.Stop();
        _balloonReleaseTimer.Interval = TimeSpan.FromMilliseconds(500);
        _balloonReleaseTimer.Start();
    }

    private void OnBalloonReleaseTimerTick(object? sender, EventArgs e)
    {
        _balloonReleaseTimer.Stop();
        _activeBalloonNotification = null;
        ShowNextWindowsNotification();
    }

    private async Task<bool> EnsurePendingWindowsNotificationCleanupAsync()
    {
        return await _notificationCleanupBarrier.WaitForLatestAsync(
            () => _viewModel.PendingWindowsNotificationCleanupRequestId,
            cleanupRequestId =>
            {
                var removeAll =
                    _viewModel.HasPendingWindowsNotificationHistoryClear;
                var pendingAccountIds =
                    _viewModel.AccountsPendingWindowsNotificationCleanup.ToArray();
                return ExecuteWindowsNotificationCleanupAsync(
                    removeAll,
                    pendingAccountIds,
                    cleanupRequestId);
            });
    }

    private async Task<bool> ExecuteWindowsNotificationCleanupAsync(
        bool removeAll,
        Guid[] pendingAccountIds,
        Guid cleanupRequestId)
    {
        var removed = await _windowsNotificationCommands.EnqueueAsync(
            service => service.RemoveMany(removeAll, pendingAccountIds),
            rejectedResult: false,
            cancellationToken: _lifetimeToken);
        if (!removed)
        {
            return false;
        }

        try
        {
            await _viewModel.CompleteWindowsNotificationCleanupAsync(
                removeAll,
                pendingAccountIds,
                cleanupRequestId,
                _lifetimeToken);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            _viewModel.StatusMessage =
                "Windows alert history was cleared, but its cleanup receipt could not be saved; delivery will stay in compatibility mode until a retry succeeds.";
            return false;
        }
    }

    private async Task InitializeWindowsNotificationStateAsync()
    {
        var pendingAccountIds = _viewModel.AccountsPendingBrowserProfileDeletion
            .Select(account => account.Id)
            .ToArray();
        var stateChanged = false;
        if (!_viewModel.ShowNotificationPreviews
            || !_viewModel.EnableWindowsNotifications)
        {
            stateChanged |= _viewModel.MarkWindowsNotificationCleanupPending(
                accountId: null);
        }

        foreach (var accountId in pendingAccountIds)
        {
            stateChanged |= _viewModel.MarkWindowsNotificationCleanupPending(
                accountId);
        }

        if (stateChanged)
        {
            await _viewModel.SaveAsync(_lifetimeToken);
        }

        _lifetimeToken.ThrowIfCancellationRequested();
        var revision = Interlocked.Increment(ref _notificationSettingsRevision);
        await ApplyNotificationSettingsAsync(revision);
    }

    private async Task ApplyNotificationSettingsAsync(int revision)
    {
        _pendingBalloonNotifications.Clear();
        _activeBalloonNotification = null;
        _balloonReleaseTimer.Stop();
        _ = TrySetNotificationIconVisible(false);

        if (_lifetime.IsCancellationRequested
            || revision != _notificationSettingsRevision)
        {
            return;
        }

        var notificationsEnabled = _viewModel.EnableWindowsNotifications;
        WindowsNotificationStatusText.Text = notificationsEnabled
            ? "Preparing modern Windows alerts in the background…"
            : "Windows alerts are off. Existing Windows alert history is being cleared.";
        var modernReady = false;
        if (notificationsEnabled)
        {
            var cleanupReady =
                await EnsurePendingWindowsNotificationCleanupAsync();
            if (_lifetime.IsCancellationRequested
                || revision != _notificationSettingsRevision)
            {
                return;
            }

            modernReady = cleanupReady
                && await _windowsNotificationCommands.EnqueueAsync(
                    service => service.TryEnable(),
                    rejectedResult: false,
                    cancellationToken: _lifetimeToken);
        }
        else
        {
            var pendingAccountIds =
                _viewModel.AccountsPendingWindowsNotificationCleanup.ToArray();
            var cleanupRequestId =
                _viewModel.PendingWindowsNotificationCleanupRequestId;
            var cleanupSucceeded =
                await _windowsNotificationCommands.EnqueueAsync(
                    service => service.Disable(),
                    rejectedResult: false,
                    cancellationToken: _lifetimeToken);
            if (cleanupSucceeded
                && cleanupRequestId is Guid completedRequestId)
            {
                await _viewModel.CompleteWindowsNotificationCleanupAsync(
                    removedAll: true,
                    pendingAccountIds,
                    completedRequestId,
                    _lifetimeToken);
            }

            WindowsNotificationStatusText.Text = _appNotifications.StatusText;
        }

        if (_lifetime.IsCancellationRequested
            || revision != _notificationSettingsRevision)
        {
            return;
        }

        _ = TrySetNotificationIconVisible(
            notificationsEnabled
                && !modernReady
                && _notificationTrayIcon is not null);
        UpdateWindowsNotificationStatus();
        if (modernReady
            && _appNotifications.TryGetCurrentActivation() is { } activation)
        {
            OnWindowsAppNotificationActivated(_appNotifications, activation);
        }
    }

    private async void OnTestWindowsNotificationClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await SendTestWindowsNotificationAsync(sender);
        }
        catch (Exception exception) when (
            _lifetime.IsCancellationRequested
                && exception is (OperationCanceledException
                    or ObjectDisposedException))
        {
            // Normal when the app closes while the test alert is queued.
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            WindowsNotificationStatusText.Text =
                "The Windows test alert could not save its pending privacy cleanup. Try again.";
            if (sender is Button button)
            {
                button.IsEnabled = true;
            }
        }
    }

    private async Task SendTestWindowsNotificationAsync(object sender)
    {
        if (!EnsureStartupComplete())
        {
            return;
        }

        if (!_viewModel.EnableWindowsNotifications)
        {
            WindowsNotificationStatusText.Text =
                "Turn on “Show Windows chat notifications” before sending a test.";
            return;
        }

        var delivery = new WindowsNotificationDelivery(
            null,
            null,
            "SteamSwitchboard",
            string.Empty,
            "Test alert",
            "Windows alerts are working. Click to open Switchboard.",
            SettingsTestReplacementTag,
            false,
            _notificationPrivacyGate.Capture(),
            IsTest: true);
        if (sender is Button button)
        {
            button.IsEnabled = false;
        }

        WindowsNotificationStatusText.Text = "Sending a Windows test alert…";
        if (_viewModel.RenewWindowsNotificationCleanupRequest())
        {
            await _viewModel.SaveAsync(_lifetimeToken);
        }

        var cleanupReady =
            await EnsurePendingWindowsNotificationCleanupAsync();
        var modernShown = cleanupReady
            && await _windowsNotificationCommands.EnqueueAsync(
                service => service.TryEnable()
                    && _notificationPrivacyGate.ExecuteIfCurrent(
                        delivery.PrivacyRevision,
                        () => service.TryShow(
                            delivery.AccountId,
                            delivery.NotificationId,
                            delivery.AccountIdentity,
                            delivery.SteamTitle,
                            delivery.Preview,
                            delivery.ReplacementTag,
                            isTest: true)),
                rejectedResult: false,
                cancellationToken: _lifetimeToken);
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        if (!_viewModel.EnableWindowsNotifications)
        {
            if (modernShown)
            {
                var cleanupConfirmed = await _windowsNotificationCommands.EnqueueAsync(
                    static service => service.RemoveTestNotifications(),
                    rejectedResult: false,
                    cancellationToken: _lifetimeToken);
                if (!cleanupConfirmed)
                {
                    WindowsNotificationStatusText.Text =
                        "Windows could not confirm test-alert cleanup yet. The generic test expires automatically.";
                }
            }

            if (sender is Button cancelledButton)
            {
                cancelledButton.IsEnabled = true;
            }

            return;
        }

        var compatibilityQueued = false;
        if (!modernShown)
        {
            compatibilityQueued = QueueLegacyWindowsNotification(delivery);
        }

        UpdateWindowsNotificationStatus(
            isTest: true,
            compatibilityQueued: compatibilityQueued);
        if (sender is Button completedButton)
        {
            completedButton.IsEnabled = true;
        }
    }

    private void OnOpenWindowsNotificationSettingsClicked(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                "ms-settings:notifications")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException)
        {
            WindowsNotificationStatusText.Text =
                "Windows notification settings could not be opened.";
        }
    }

    private void OnWindowsAppNotificationActivated(
        object? sender,
        WindowsAppNotificationActivatedEventArgs e)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (e.AccountId is Guid accountId)
            {
                var notification = e.NotificationId is Guid notificationId
                    ? _viewModel.Notifications.FirstOrDefault(item =>
                        item.Id == notificationId
                        && item.AccountId == accountId)
                    : null;
                notification?.ReportClickedAndClose();
                await OpenNotificationAsync(accountId);
            }
            else
            {
                OpenNotificationCenter();
            }
        });
    }

    private void UpdateWindowsNotificationStatus(
        bool isTest = false,
        bool compatibilityQueued = true)
    {
        if (_appNotifications.IsReady)
        {
            WindowsNotificationStatusText.Text = _appNotifications.StatusText;
            return;
        }

        if (!_viewModel.EnableWindowsNotifications)
        {
            WindowsNotificationStatusText.Text = _appNotifications.StatusText;
            return;
        }

        if (_notificationTrayIcon is null || !compatibilityQueued)
        {
            WindowsNotificationStatusText.Text = isTest
                ? "Windows could not create a test alert. In-app notification history is still available."
                : "Windows alerts are unavailable here. In-app notification history is still available.";
            return;
        }

        WindowsNotificationStatusText.Text = isTest
            ? "Compatibility test alert sent. If nothing appears, check Do not disturb and Windows notification settings."
            : _appNotifications.StatusText;
    }

    private void OnNotificationIconDoubleClicked(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(ShowAndActivateWindow);
    }

    private bool TrySetNotificationIconVisible(bool visible)
    {
        if (visible && _notificationTrayIcon is null)
        {
            _legacyNotificationIconVisible = false;
            return false;
        }

        try
        {
            _notificationIcon.Visible = visible;
            _legacyNotificationIconVisible = visible;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or Win32Exception)
        {
            _legacyNotificationIconVisible = false;
            return false;
        }
    }

    private async Task OpenNotificationAsync(Guid accountId)
    {
        var account = _viewModel.Accounts.FirstOrDefault(
            item => item.Id == accountId);
        if (account is null || _viewModel.IsAccountDeletionPending(account.Id))
        {
            return;
        }

        ShowAndActivateWindow();
        NotificationPopup.IsOpen = false;
        _viewModel.SelectedAccount = account;
        AccountList.SelectedItem = account;
        NavigateTo(AppSection.Chats);
        try
        {
            await EnsureChatSessionAsync(account);
            ShowSelectedChat();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException)
        {
            _viewModel.StatusMessage = exception.Message;
        }
    }

    private void ShowAndActivateWindow()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (TaskbarItemInfo is not null)
        {
            TaskbarItemInfo.Overlay = null;
            TaskbarItemInfo.Description = "SteamSwitchboard";
        }

        IsEnabled = false;
        _playAccountTimer.Stop();
        _balloonReleaseTimer.Stop();

        try
        {
            // Flush any setting/cleanup marker changed immediately before the
            // close request. A failed Windows cleanup can then retry at startup.
            await _viewModel.SaveAsync(CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            // Continue closing. Existing atomically saved state remains intact.
        }

        var removeAll = _viewModel.HasPendingWindowsNotificationHistoryClear;
        var pendingAccountIds =
            _viewModel.AccountsPendingWindowsNotificationCleanup.ToArray();
        var cleanupRequestId =
            _viewModel.PendingWindowsNotificationCleanupRequestId;
        Task<bool>? finalCleanup = null;
        if (removeAll || pendingAccountIds.Length > 0)
        {
            finalCleanup = _windowsNotificationCommands.EnqueueAsync(
                service => service.RemoveMany(removeAll, pendingAccountIds),
                rejectedResult: false,
                cancellationToken: CancellationToken.None);
        }

        _lifetime.Cancel();
        foreach (var session in _chatSessions.Values)
        {
            session.ChatNotificationReceived -= OnChatNotificationReceived;
            session.ChatReadyWhileVisible -= OnChatReadyWhileVisible;
            session.ReconnectSessionRequested -= OnReconnectSessionRequested;
            session.Dispose();
        }

        _chatSessions.Clear();
        _viewModel.ClearNotifications();
        _pendingBalloonNotifications.Clear();
        _activeBalloonNotification = null;
        _ = TrySetNotificationIconVisible(false);
        _notificationIcon.Dispose();
        _notificationTrayIcon?.Dispose();
        _appNotifications.Activated -= OnWindowsAppNotificationActivated;
        _windowsNotificationCommands.Dispose();
        var queueCompleted = await Task.WhenAny(
            _windowsNotificationCommands.Completion,
            Task.Delay(TimeSpan.FromSeconds(5)));
        var queueDrained = ReferenceEquals(
            queueCompleted,
            _windowsNotificationCommands.Completion);
        if (queueDrained)
        {
            await _windowsNotificationCommands.Completion;
        }

        if (finalCleanup is { IsCompletedSuccessfully: true }
            && finalCleanup.Result
            && cleanupRequestId is Guid completedRequestId)
        {
            try
            {
                await _viewModel.CompleteWindowsNotificationCleanupAsync(
                    removeAll,
                    pendingAccountIds,
                    completedRequestId,
                    CancellationToken.None);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
            {
                // The durable marker remains and startup will retry cleanup.
            }
        }

        if (queueDrained)
        {
            _appNotifications.Dispose();
        }

        _lifetime.Dispose();
        _shutdownComplete = true;
        Close();
    }

    private sealed record WindowsNotificationDelivery(
        Guid? AccountId,
        Guid? NotificationId,
        string AccountDisplayName,
        string AccountLoginName,
        string SteamTitle,
        string Preview,
        string? ReplacementTag,
        bool PreviewWasEnabled,
        long PrivacyRevision,
        bool IsTest)
    {
        public string AccountIdentity => string.IsNullOrWhiteSpace(AccountLoginName)
            ? AccountDisplayName
            : $"{AccountDisplayName} — Steam login: {AccountLoginName}";
    }
}

internal static class WindowsNotificationDeliveryPolicy
{
    internal static bool IsSameReplacement(
        Guid? existingAccountId,
        string? existingReplacementTag,
        bool existingIsTest,
        Guid? incomingAccountId,
        string? incomingReplacementTag,
        bool incomingIsTest) =>
        !string.IsNullOrWhiteSpace(incomingReplacementTag)
        && existingAccountId == incomingAccountId
        && existingIsTest == incomingIsTest
        && string.Equals(
            existingReplacementTag,
            incomingReplacementTag,
            StringComparison.Ordinal);
}
