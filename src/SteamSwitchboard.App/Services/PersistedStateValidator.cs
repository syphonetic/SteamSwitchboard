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

        var validatedAccounts = new List<AccountProfile>(state.Accounts.Count);
        var accountIds = new HashSet<Guid>();
        foreach (var account in state.Accounts)
        {
            if (account is null || !accountIds.Add(account.Id))
            {
                throw new InvalidDataException("The settings contain duplicate or missing account identifiers.");
            }

            AccountValidator.Normalize(account);
            if (AccountValidator.Validate(account, validatedAccounts) is not null)
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
