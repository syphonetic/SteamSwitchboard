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
    private readonly Queue<WindowsNotificationDelivery> _pendingBalloonNotifications = [];
    private System.Drawing.Icon? _notificationTrayIcon;
    private WindowsNotificationDelivery? _activeBalloonNotification;
    private bool _startupStarted;
    private bool _isLoaded;
    private bool _suppressSelectionEvents;

    internal event Action<AccountViewModel>? ChatSessionInitializationStarted;

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
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_startupStarted)
        {
            return;
        }

        _startupStarted = true;
        _suppressSelectionEvents = true;
        try
        {
            await _viewModel.InitializeAsync(_lifetimeToken, refreshGames: false);
            var restoredAccount = _viewModel.SelectedAccount;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            _viewModel.SelectedAccount = restoredAccount;
            AccountList.SelectedItem = restoredAccount;
            ApplyNotificationSettings();
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
                "Steam games could not be refreshed. Chats and settings are still available.";
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
            NavigateTo(AppSection.Chats);
            await EnsureChatSessionAsync(account);
            ShowSelectedChat();
        }
        catch (Exception exception) when (
            exception is ArgumentException
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

    private async void OnAccountSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || _suppressSelectionEvents)
        {
            return;
        }

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
            exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (exception is not OperationCanceledException)
            {
                _viewModel.StatusMessage = "The selected account could not be saved.";
            }
        }
    }

    private void OnChatsNavClicked(object sender, RoutedEventArgs e) => NavigateTo(AppSection.Chats);

    private void OnGamesNavClicked(object sender, RoutedEventArgs e) => NavigateTo(AppSection.Games);

    private void OnSettingsNavClicked(object sender, RoutedEventArgs e) => NavigateTo(AppSection.Settings);

    private void NavigateTo(AppSection section)
    {
        _viewModel.SelectedSection = section;
        SetNavStyle(ChatsNavButton, section == AppSection.Chats);
        SetNavStyle(GamesNavButton, section == AppSection.Games);
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
            _viewModel.StatusMessage = $"Found {_viewModel.Games.Count} installed games";
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
                _viewModel.StatusMessage = $"Games will launch with {account.DisplayName} selected";
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or OperationCanceledException)
        {
            if (exception is not OperationCanceledException)
            {
                _viewModel.StatusMessage = "The play-account choice could not be saved.";
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
            await _viewModel.SaveAsync(_lifetimeToken);
            ApplyNotificationSettings();
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
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or OperationCanceledException)
        {
            if (exception is not OperationCanceledException)
            {
                _viewModel.StatusMessage = "Conversation settings could not be saved.";
            }
        }
    }

    private void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        if (!EnsureStartupComplete()
            || sender is not Button { Tag: InstalledGame game }
            || _viewModel.SelectedPlayAccount is not { } account)
        {
            return;
        }

        LaunchAssessment assessment;
        try
        {
            assessment = _launcher.LaunchIfReady(
                account.Profile,
                game,
                _viewModel.SteamExecutablePath);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or Win32Exception
                or FileNotFoundException)
        {
            MessageBox.Show(
                exception.Message,
                "Game could not be started",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (assessment.CanLaunch)
        {
            _viewModel.StatusMessage = $"Starting {game.Name} as {account.DisplayName}";
            return;
        }

        if (assessment.Readiness is LaunchReadiness.SteamNotRunning
            or LaunchReadiness.ActiveAccountUnknown
            or LaunchReadiness.AccountSwitchRequired)
        {
            var switchWindow = new AccountSwitchWindow(
                account.Profile,
                game,
                _viewModel.SteamExecutablePath,
                _launcher)
            {
                Owner = this
            };
            _ = switchWindow.ShowDialog();
            return;
        }

        MessageBox.Show(
            assessment.Message,
            "Cannot start this game yet",
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
            exception is IOException or UnauthorizedAccessException)
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

        try
        {
            await _viewModel.MarkAccountDeletionPendingAsync(account, _lifetimeToken);
            _viewModel.ClearNotifications(account.Id);
            PurgeWindowsNotifications(account.Id);
            await CompletePendingProfileDeletionAsync(account);
            ShowSelectedChat();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or COMException)
        {
            MessageBox.Show(
                exception.Message,
                "Account could not be forgotten",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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
            account.IsCurrentPlayAccount = false;
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
            match.IsCurrentPlayAccount = true;
            _viewModel.NativeSteamAccountState = NativeSteamAccountState.Match;
            _viewModel.CurrentSteamAccountStatus =
                $"Current in Steam: {match.DisplayName} (@{match.SteamLoginName})";
        }
        else
        {
            _viewModel.NativeSteamAccountState = NativeSteamAccountState.Mismatch;
            _viewModel.CurrentSteamAccountStatus =
                $"Current in Steam: {activeAccount.PersonaName} (@{activeAccount.AccountName}), not in Switchboard";
        }

        _viewModel.NotifyCurrentPlayAccountChanged();
    }

    private void InitializeNotificationIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return;
            }

            _notificationTrayIcon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
            if (_notificationTrayIcon is null)
            {
                return;
            }

            _notificationIcon.Icon = _notificationTrayIcon;
            _notificationIcon.BalloonTipClicked += OnNotificationBalloonClicked;
            _notificationIcon.DoubleClick += OnNotificationIconDoubleClicked;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or FileNotFoundException
                or Win32Exception)
        {
            // In-app notifications remain available when Windows cannot create a tray icon.
        }
    }

    private void OnChatNotificationReceived(
        object? sender,
        ChatNotificationEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                () => HandleChatNotification(
                    e.Account,
                    e.Notification,
                    e.ReportClicked,
                    e.ReportClosed));
            return;
        }

        HandleChatNotification(
            e.Account,
            e.Notification,
            e.ReportClicked,
            e.ReportClosed);
    }

    private void OnChatReadyWhileVisible(object? sender, EventArgs e)
    {
        if (sender is SteamChatSession session
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

    private void HandleChatNotification(
        AccountViewModel account,
        ChatNotificationPayload payload,
        Action? reportClicked,
        Action? reportClosed)
    {
        if (!_viewModel.Accounts.Contains(account)
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
        var conversationIsVisible = IsActive
            && WindowState != WindowState.Minimized
            && _viewModel.SelectedSection == AppSection.Chats
            && ReferenceEquals(_viewModel.SelectedAccount, account);
        if (!conversationIsVisible)
        {
            account.UnreadCount = Math.Max(1, account.UnreadCount);
            ShowWindowsNotification(notification);
        }
        else
        {
            MarkAccountRead(account);
        }

        _viewModel.StatusMessage =
            $"Steam notification for {account.DisplayName}: {payload.SteamTitle}";
    }

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

    private void ShowWindowsNotification(ChatNotificationViewModel notification)
    {
        if (!_viewModel.EnableWindowsNotifications
            || _notificationTrayIcon is null)
        {
            return;
        }

        while (_pendingBalloonNotifications.Count
               >= MaximumQueuedWindowsNotifications)
        {
            _ = _pendingBalloonNotifications.Dequeue();
        }

        _pendingBalloonNotifications.Enqueue(new WindowsNotificationDelivery(
            notification.AccountId,
            notification.AccountDisplayName,
            notification.AccountLoginName,
            notification.SteamTitle,
            notification.Preview));
        ShowNextWindowsNotification();
    }

    private void ShowNextWindowsNotification()
    {
        if (_activeBalloonNotification is not null
            || !_notificationIcon.Visible
            || !_pendingBalloonNotifications.TryDequeue(out var notification))
        {
            return;
        }

        _activeBalloonNotification = notification;
        var title = SafeText.SanitizeDisplayText(
            $"{notification.AccountDisplayName} (@{notification.AccountLoginName}) • {notification.SteamTitle}",
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
    }

    private void OnNotificationsClicked(object sender, RoutedEventArgs e)
    {
        NotificationPopup.IsOpen = !NotificationPopup.IsOpen;
    }

    private void OnClearNotificationsClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearNotifications();
        PurgeWindowsNotifications(accountId: null);
        NotificationPopup.IsOpen = false;
    }

    private void PurgeWindowsNotifications(Guid? accountId)
    {
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
            return;
        }

        _activeBalloonNotification = null;
        _balloonReleaseTimer.Stop();
        var shouldRemainVisible = _viewModel.EnableWindowsNotifications
            && _notificationTrayIcon is not null;
        _notificationIcon.Visible = false;
        _notificationIcon.Visible = shouldRemainVisible;
        if (_pendingBalloonNotifications.Count > 0)
        {
            QueueNextWindowsNotification();
        }
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

    private void ApplyNotificationSettings()
    {
        _pendingBalloonNotifications.Clear();
        _activeBalloonNotification = null;
        _balloonReleaseTimer.Stop();
        _notificationIcon.Visible = false;
        _notificationIcon.Visible = _viewModel.EnableWindowsNotifications
            && _notificationTrayIcon is not null;
    }

    private void OnNotificationIconDoubleClicked(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(ShowAndActivateWindow);
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

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _playAccountTimer.Stop();
        _balloonReleaseTimer.Stop();
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
        _notificationIcon.Visible = false;
        _notificationIcon.Dispose();
        _notificationTrayIcon?.Dispose();
        _lifetime.Dispose();
    }

    private sealed record WindowsNotificationDelivery(
        Guid AccountId,
        string AccountDisplayName,
        string AccountLoginName,
        string SteamTitle,
        string Preview);
}
