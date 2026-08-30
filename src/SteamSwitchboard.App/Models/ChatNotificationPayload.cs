namespace SteamSwitchboard.Models;

public sealed record ChatNotificationPayload(
    string SteamTitle,
    string Preview,
    DateTimeOffset ReceivedUtc)
{
    public string? ReplacementTag { get; init; }

    public bool IsUnreadFallback { get; init; }

    public bool ReplacesUnreadFallback { get; init; }
}
