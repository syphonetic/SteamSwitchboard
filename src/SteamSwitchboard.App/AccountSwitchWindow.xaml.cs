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
    private bool _hasLaunched;

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
            $"In Steam, choose “{_account.SteamLoginName}” to play {_game.Name}";
        RequiredAccountText.Text =
            $"Required: {_account.DisplayName} (@{_account.SteamLoginName})";
        CurrentAccountText.Text = "Current in Steam: checking…";
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
        Loaded += OnLoaded;
        Closed += (_, _) => _timer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CheckAndLaunch();
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e) => CheckAndLaunch();

    private void CheckAndLaunch()
    {
        if (_hasLaunched)
        {
            return;
        }

        LaunchAssessment assessment;
        try
        {
            assessment = _launcher.LaunchIfReady(
                _account,
                _game,
                _configuredSteamPath);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or FileNotFoundException)
        {
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            StatusText.Text = exception.Message;
            return;
        }

        StatusText.Text = assessment.Message;
        CurrentAccountText.Text = assessment.ActiveAccount is null
            ? "Current in Steam: not detected"
            : $"Current in Steam: {assessment.ActiveAccount.PersonaName} (@{assessment.ActiveAccount.AccountName})";
        OpenSteamButton.Content = assessment.Readiness == LaunchReadiness.SteamNotRunning
            ? "Start Steam"
            : "Open Steam to switch account";

        if (!assessment.CanLaunch)
        {
            return;
        }

        _hasLaunched = true;
        _timer.Stop();
        WaitingProgress.IsIndeterminate = false;
        WaitingProgress.Value = 100;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        StatusText.Text = $"Verified. Starting {_game.Name}…";
        LaunchArmedText.Text = "Account verified — launch handed safely to Steam.";

        DialogResult = true;
    }

    private void OnOpenSteamClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _launcher.OpenSteam(_configuredSteamPath);
            StatusText.Text =
                $"Waiting for Steam to activate {_account.SteamLoginName}…";
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or FileNotFoundException)
        {
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            StatusText.Text = exception.Message;
        }
    }
}
