namespace SteamSwitchboard.Models;

public sealed record SteamProcessIdentity(
    int ProcessId,
    long StartTimeUtcTicks);
