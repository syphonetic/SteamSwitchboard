namespace SteamSwitchboard.Models;

public sealed class PersistedState
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<AccountProfile> Accounts { get; set; } = [];

    public AppSettings Settings { get; set; } = new();

    public Guid? LastSelectedAccountId { get; set; }

    public Guid? LastPlayAccountId { get; set; }

    public List<Guid> PendingBrowserProfileDeletionIds { get; set; } = [];

    public bool PendingWindowsNotificationHistoryClear { get; set; }

    public List<Guid> PendingWindowsNotificationAccountCleanupIds { get; set; } = [];

    public Guid? PendingWindowsNotificationCleanupRequestId { get; set; }
}
