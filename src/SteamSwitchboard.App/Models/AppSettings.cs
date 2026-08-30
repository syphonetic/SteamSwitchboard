namespace SteamSwitchboard.Models;

public sealed class AppSettings
{
    public string? SteamExecutablePath { get; set; }

    public bool LaunchAtWindowsSignIn { get; set; }

    public bool ShowNotificationPreviews { get; set; }

    public bool EnableWindowsNotifications { get; set; } = true;

    public bool KeepAllChatsLive { get; set; } = true;
}
