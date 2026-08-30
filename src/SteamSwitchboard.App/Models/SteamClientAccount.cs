namespace SteamSwitchboard.Models;

public sealed record SteamClientAccount(
    string SteamId,
    string AccountName,
    string PersonaName,
    bool MostRecent,
    DateTimeOffset? TimestampUtc);
