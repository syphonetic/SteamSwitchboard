namespace SteamSwitchboard.Models;

public enum LaunchReadiness
{
    Ready,
    SteamNotRunning,
    ActiveAccountUnknown,
    AccountSwitchRequired,
    SteamNotFound,
    InvalidAccount,
    GameNotInstalled
}

public sealed record LaunchAssessment(
    LaunchReadiness Readiness,
    string Message,
    SteamClientAccount? ActiveAccount = null)
{
    public bool CanLaunch => Readiness == LaunchReadiness.Ready;
}
