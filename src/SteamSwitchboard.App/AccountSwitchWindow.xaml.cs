using System.Windows;
using System.Windows.Threading;
using SteamSwitchboard.Models;
using SteamSwitchboard.Services;

namespace SteamSwitchboard;

public partial class AccountSwitchWindow : Window
{
    private readonly AccountProfile _account;
    private readonly InstalledGame _game;
    private readonly string? _configuredSteamPath;
    private readonly GameLaunchService _launcher;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private bool _hasLaunched;
    private bool _closed;

    public AccountSwitchWindow(
        AccountProfile account,
        InstalledGame game,
        string? configuredSteamPath,
        GameLaunchService launcher)
    {
        InitializeComponent();
        WindowSizing.ClampToCurrentWorkArea(this);
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _configuredSteamPath = configuredSteamPath;
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));

        InstructionText.Text =
            $"In Steam, switch to login “{_account.SteamLoginName}” for {_game.Name}";
        RequiredAccountText.Text =
            $"Required in Steam: {_account.SteamLoginName} (profile nickname: {_account.DisplayName})";
        CurrentAccountText.Text = "Active in Steam: checking…";
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await CheckAndLaunchAsync();
        if (!_closed && !_hasLaunched)
        {
            _timer.Start();
        }
    }

    private async void OnTimerTick(object? sender, EventArgs e) =>
        await CheckAndLaunchAsync();

    private async Task CheckAndLaunchAsync()
    {
        if (_hasLaunched
            || _closed
            || !await _checkGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            LaunchAssessment assessment;
            try
            {
                assessment = await Task.Run(
                    () => _launcher.LaunchIfReady(
                        _account,
                        _game,
                        _configuredSteamPath,
                        _lifetime.Token),
                    _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                StatusText.Foreground =
                    (System.Windows.Media.Brush)FindResource("DangerBrush");
                StatusText.Text = exception.Message;
                return;
            }

            if (_closed)
            {
                return;
            }

            StatusText.Text = assessment.Message;
            CurrentAccountText.Text = assessment.ActiveAccount is null
                ? "Active in Steam: not detected"
                : $"Active in Steam: {assessment.ActiveAccount.AccountName} ({assessment.ActiveAccount.PersonaName})";
            OpenSteamButton.Content = assessment.Readiness == LaunchReadiness.SteamNotRunning
                ? "Start Steam"
                : "Open Steam";

            if (!assessment.CanLaunch)
            {
                return;
            }

            _hasLaunched = true;
            _timer.Stop();
            WaitingProgress.IsIndeterminate = false;
            WaitingProgress.Value = 100;
            StatusText.Foreground =
                (System.Windows.Media.Brush)FindResource("SuccessBrush");
            StatusText.Text = $"Verified. Sending {_game.Name} to Steam…";
            LaunchArmedText.Text =
                "Account verified — launch request handed safely to Steam.";

            DialogResult = true;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async void OnOpenSteamClicked(object sender, RoutedEventArgs e)
    {
        OpenSteamButton.IsEnabled = false;
        try
        {
            await Task.Run(
                () => _launcher.OpenSteam(_configuredSteamPath),
                _lifetime.Token);
            if (_closed)
            {
                return;
            }

            StatusText.Text =
                $"Waiting for Steam to use login {_account.SteamLoginName}…";
        }
        catch (OperationCanceledException)
        {
            // Normal when the account-switch window closes.
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            StatusText.Text = exception.Message;
        }
        finally
        {
            if (!_closed)
            {
                OpenSteamButton.IsEnabled = true;
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _timer.Stop();
        _lifetime.Cancel();
    }
}
