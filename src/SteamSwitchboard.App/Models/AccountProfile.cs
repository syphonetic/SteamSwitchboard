namespace SteamSwitchboard.Models;

public sealed class AccountProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string DisplayName { get; set; } = string.Empty;

    public string SteamLoginName { get; set; } = string.Empty;

    public string AccentHex { get; set; } = "#66C0F4";

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;

    public string BrowserProfileName => $"account-{Id:N}";
}
