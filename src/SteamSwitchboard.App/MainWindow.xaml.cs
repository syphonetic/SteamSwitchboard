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
    private readonly MainViewModel _viewModel;
    private readonly AppPaths _paths;
    private readonly GameLaunchService _launcher;
    private readonly Dictionary<Guid, SteamChatSession> _chatSessions = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _playAccountTimer;
    private bool _isLoaded;

    public MainWindow(
        MainViewModel viewModel,
        AppPaths paths,
        GameLaunchService launcher)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        DataContext = _viewModel;
        DataFolderText.Text = _paths.Root;
        FitToCurrentWorkArea();

        _playAccountTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _playAccountTimer.Tick += (_, _) => UpdateCurrentPlayAccount();
    }

    private void FitToCurrentWorkArea()
    {
        const double outerMargin = 32;
        Width = Math.Max(
            MinWidth,
            Math.Min(Width, SystemParameters.WorkArea.Width - outerMargin));
        Height = Math.Max(
            MinHeight,
            Math.Min(Height, SystemParameters.WorkArea.Height - outerMargin));
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        try
        {
            await _viewModel.InitializeAsync(_lifetime.Token);
            await ResumePendingProfileDeletionsAsync();
            ShowSelectedChat();
            await InitializeChatSessionsAsync();
            UpdateCurrentPlayAccount();
            _playAccountTimer.Start();
        }
        catch (OperationCanceledException)
        {
            // Normal during app shutdown.
        }
        catch (Exception exception)
        {
            _viewModel.StatusMessage = $"Startup needs attention: {exception.Message}";
            MessageBox.Show(
                exception.Message,
                "SteamSwitchboard could not finish starting",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task InitializeChatSessionsAsync()
    {
        var selected = _viewModel.SelectedAccount;
        if (selected is not null
            && !_viewModel.IsAccountDeletionPending(selected.Id))
        {
            await EnsureChatSessionAsync(selected);
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
            return existing;
        }

        var session = new SteamChatSession(account, _paths.BrowserData);
        session.HideChat();
        _chatSessions.Add(account.Id, session);
        ChatSessionContainer.Children.Add(session);
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

    private async Task ResumePendingProfileDeletionsAsync()
    {
        foreach (var account in _viewModel.AccountsPendingBrowserProfileDeletion.ToArray())
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            try
            {
                var session = await EnsureChatSessionAsync(
                    account,
                    forProfileCleanup: true);
                await session.ClearSessionAsync();
                _chatSessions.Remove(account.Id);
                ChatSessionContainer.Children.Remove(session);
                session.Dispose();
                await _viewModel.RemoveAccountAsync(account, _lifetime.Token);
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

    private void ShowSelectedChat()
    {
        foreach (var (accountId, session) in _chatSessions)
        {
            if (_viewModel.SelectedSection == AppSection.Chats
                && _viewModel.SelectedAccount?.Id == accountId
                && !_viewModel.IsAccountDeletionPending(accountId))
            {
                session.ShowChat();
            }
            else
            {
                session.HideChat();
            }
        }
    }

    private async void OnAddAccountClicked(object sender, RoutedEventArgs e)
    {
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
            var account = await _viewModel.AddAccountAsync(dialog.Result, _lifetime.Token);
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

    private async void OnAccountSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded)
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
            await _viewModel.SaveAsync(_lifetime.Token);
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
                session.HideChat();
            }
        }
    }

    private void SetNavStyle(Button button, bool isSelected)
    {
        button.Style = (Style)FindResource(isSelected ? "SecondaryButton" : "QuietButton");
    }

    private async void OnRefreshGamesClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.IsBusy = true;
        _viewModel.StatusMessage = "Refreshing the Steam library…";
        try
        {
            await _viewModel.RefreshGamesAsync(_lifetime.Token);
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

    private void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledGame game }
            || _viewModel.SelectedAccount is not { } account)
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
            await _viewModel.SaveAsync(_lifetime.Token);
            await _viewModel.RefreshGamesAsync(_lifetime.Token);
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
        if (_viewModel.SelectedAccount is not { } account)
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
            await _viewModel.MarkAccountDeletionPendingAsync(account, _lifetime.Token);
            var session = await EnsureChatSessionAsync(
                account,
                forProfileCleanup: true);
            await session.ClearSessionAsync();
            _chatSessions.Remove(account.Id);
            ChatSessionContainer.Children.Remove(session);
            session.Dispose();

            await _viewModel.RemoveAccountAsync(account, _lifetime.Token);
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
            return;
        }

        var steamRoot = Path.GetDirectoryName(steamExecutable);
        if (string.IsNullOrWhiteSpace(steamRoot))
        {
            return;
        }

        var activeAccount = new SteamClientAccountService().FindActiveAccount(steamRoot);
        var match = _viewModel.Accounts.FirstOrDefault(account =>
            string.Equals(
                account.SteamLoginName,
                activeAccount?.AccountName,
                StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            match.IsCurrentPlayAccount = true;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _playAccountTimer.Stop();
        _lifetime.Cancel();
        foreach (var session in _chatSessions.Values)
        {
            session.Dispose();
        }

        _chatSessions.Clear();
        _lifetime.Dispose();
    }
}
