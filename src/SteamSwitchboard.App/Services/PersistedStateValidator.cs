using SteamSwitchboard.Models;

namespace SteamSwitchboard.Services;

public static class PersistedStateValidator
{
    private const int MaximumConfiguredPathLength = 2048;

    public static void ValidateAndNormalize(PersistedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Accounts ??= [];
        state.Settings ??= new AppSettings();
        state.PendingBrowserProfileDeletionIds ??= [];
        if (state.Accounts.Count > AccountValidator.MaximumAccountProfiles)
        {
            throw new InvalidDataException(
                $"The settings contain more than {AccountValidator.MaximumAccountProfiles} account profiles.");
        }

        var validatedAccounts = new List<AccountProfile>(state.Accounts.Count);
        var accountIds = new HashSet<Guid>();
        var accountNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in state.Accounts)
        {
            if (account is null
                || !accountIds.Add(account.Id)
                || !accountNames.Add(account.SteamLoginName?.Trim() ?? string.Empty))
            {
                throw new InvalidDataException(
                    "The settings contain duplicate or missing account identities.");
            }

            AccountValidator.Normalize(account);
            if (AccountValidator.Validate(account, Array.Empty<AccountProfile>()) is not null)
            {
                throw new InvalidDataException("The settings contain an invalid account profile.");
            }

            validatedAccounts.Add(account);
        }

        state.Accounts = validatedAccounts;
        if (state.LastSelectedAccountId is { } selectedId && !accountIds.Contains(selectedId))
        {
            state.LastSelectedAccountId = null;
        }

        if (state.LastPlayAccountId is { } playAccountId && !accountIds.Contains(playAccountId))
        {
            state.LastPlayAccountId = null;
        }

        var pendingDeletionIds = new HashSet<Guid>();
        foreach (var pendingId in state.PendingBrowserProfileDeletionIds)
        {
            if (pendingId == Guid.Empty
                || !accountIds.Contains(pendingId)
                || !pendingDeletionIds.Add(pendingId))
            {
                throw new InvalidDataException(
                    "The settings contain an invalid browser-profile cleanup request.");
            }
        }

        state.PendingBrowserProfileDeletionIds = pendingDeletionIds.ToList();

        var configuredPath = state.Settings.SteamExecutablePath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            state.Settings.SteamExecutablePath = null;
        }
        else
        {
            configuredPath = configuredPath.Trim();
            state.Settings.SteamExecutablePath = configuredPath.Length <= MaximumConfiguredPathLength
                && !SafeText.ContainsUnsafeIdentityCharacters(configuredPath)
                    ? configuredPath
                    : null;
        }
    }
}
