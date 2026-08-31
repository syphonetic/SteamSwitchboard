using SteamSwitchboard.Models;

namespace SteamSwitchboard.Services;

public static class AccountValidator
{
    private const int MaximumNameLength = 64;
    public const int MaximumAccountProfiles = 512;
    public const string DefaultAccentHex = "#66C0F4";

    public static string? Validate(AccountProfile account, IEnumerable<AccountProfile> existingAccounts)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(existingAccounts);

        var existing = existingAccounts as IReadOnlyCollection<AccountProfile>
            ?? existingAccounts.ToArray();
        if (existing.Count >= MaximumAccountProfiles)
        {
            return $"Switchboard supports up to {MaximumAccountProfiles} saved profiles on one PC.";
        }

        if (account.Id == Guid.Empty)
        {
            return "The profile identifier is not valid.";
        }

        if (string.IsNullOrWhiteSpace(account.DisplayName))
        {
            return "Enter a name you will recognize in Switchboard.";
        }

        if (account.DisplayName.Trim().Length > MaximumNameLength
            || SafeText.ContainsUnsafeIdentityCharacters(account.DisplayName))
        {
            return $"The profile name must be safe text and {MaximumNameLength} characters or fewer.";
        }

        if (!IsSafeSteamLoginName(account.SteamLoginName))
        {
            return "The Steam login name is not valid.";
        }

        if (!IsAccentHex(account.AccentHex))
        {
            return "The profile color is not valid.";
        }

        if (existing.Any(item => item.Id == account.Id))
        {
            return "That local profile identifier is already in Switchboard.";
        }

        if (existing.Any(item =>
                !string.IsNullOrWhiteSpace(item.SteamLoginName)
                && string.Equals(
                    item.SteamLoginName.Trim(),
                    account.SteamLoginName.Trim(),
                    StringComparison.OrdinalIgnoreCase)))
        {
            return "That Steam login is already linked to another Switchboard profile.";
        }

        return null;
    }

    public static void Normalize(AccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(account);
        account.DisplayName = SafeText.NormalizeIdentityInput(account.DisplayName);
        account.SteamLoginName = SafeText.NormalizeIdentityInput(account.SteamLoginName);
        account.AccentHex = string.IsNullOrWhiteSpace(account.AccentHex)
            ? DefaultAccentHex
            : account.AccentHex.Trim().ToUpperInvariant();
    }

    public static bool IsSafeSteamLoginName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Trim() is { Length: >= 2 and <= MaximumNameLength } normalized
        && normalized.All(character =>
            character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_');

    public static bool IsAccentHex(string? value) =>
        value is { Length: 7 }
        && value[0] == '#'
        && value.AsSpan(1).ToString().All(Uri.IsHexDigit);
}
