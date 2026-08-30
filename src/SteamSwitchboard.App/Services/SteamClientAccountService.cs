using System.Security;
using System.Globalization;
using Microsoft.Win32;
using SteamSwitchboard.Models;

namespace SteamSwitchboard.Services;

public sealed class SteamClientAccountService
{
    private const string SteamActiveProcessKey = @"Software\Valve\Steam\ActiveProcess";
    private const ulong IndividualSteamIdBase = 76_561_197_960_265_728;
    private readonly Func<uint?> _activeAccountIdProvider;

    public SteamClientAccountService(Func<uint?>? activeAccountIdProvider = null)
    {
        _activeAccountIdProvider = activeAccountIdProvider ?? ReadActiveAccountId;
    }

    public IReadOnlyList<SteamClientAccount> LoadAccounts(string steamRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamRoot);
        if (!LocalPathPolicy.TryNormalizeLocalPath(steamRoot, out var normalizedRoot))
        {
            return [];
        }

        var loginUsersCandidate = Path.Combine(
            normalizedRoot,
            "config",
            "loginusers.vdf");
        if (!LocalPathPolicy.TryNormalizeLocalPath(
                loginUsersCandidate,
                out var loginUsersFile))
        {
            return [];
        }

        try
        {
            var document = VdfParser.ParseFile(loginUsersFile);
            var users = document.Get("users") ?? document;
            var accounts = new List<SteamClientAccount>();

            foreach (var (steamId, node) in users.Children)
            {
                if (!node.IsObject
                    || !TryGetAccountId(steamId, out _))
                {
                    continue;
                }

                var accountName = node.GetValue("AccountName")?.Trim() ?? string.Empty;
                if (!AccountValidator.IsSafeSteamLoginName(accountName))
                {
                    continue;
                }

                DateTimeOffset? timestamp = null;
                if (long.TryParse(
                        node.GetValue("Timestamp"),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var unixTime)
                    && unixTime > 0)
                {
                    try
                    {
                        timestamp = DateTimeOffset.FromUnixTimeSeconds(unixTime);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        timestamp = null;
                    }
                }

                accounts.Add(new SteamClientAccount(
                    steamId,
                    accountName,
                    SafeText.SanitizeDisplayText(
                        node.GetValue("PersonaName"),
                        accountName,
                        maximumLength: 100),
                    string.Equals(node.GetValue("MostRecent"), "1", StringComparison.Ordinal),
                    timestamp));
            }

            return accounts
                .OrderByDescending(account => account.MostRecent)
                .ThenByDescending(account => account.TimestampUtc)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or SecurityException)
        {
            return [];
        }
    }

    public SteamClientAccount? FindActiveAccount(string steamRoot)
    {
        var accounts = LoadAccounts(steamRoot);
        var activeAccountId = _activeAccountIdProvider();
        if (!activeAccountId.HasValue || activeAccountId.Value == 0)
        {
            return null;
        }

        return accounts.FirstOrDefault(account =>
            TryGetAccountId(account.SteamId, out var accountId)
            && accountId == activeAccountId.Value);
    }

    private static bool TryGetAccountId(string steamId, out uint accountId)
    {
        accountId = 0;
        if (!ulong.TryParse(
                steamId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedSteamId)
            || parsedSteamId <= IndividualSteamIdBase
            || parsedSteamId > IndividualSteamIdBase + uint.MaxValue)
        {
            return false;
        }

        accountId = (uint)(parsedSteamId - IndividualSteamIdBase);
        return accountId != 0;
    }

    private static uint? ReadActiveAccountId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SteamActiveProcessKey);
            return key?.GetValue("ActiveUser") switch
            {
                int signed => unchecked((uint)signed),
                uint unsigned => unsigned,
                long signedLong when signedLong is >= int.MinValue and <= uint.MaxValue =>
                    unchecked((uint)signedLong),
                string text when uint.TryParse(text, out var parsed) => parsed,
                _ => null
            };
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or SecurityException)
        {
            return null;
        }
    }
}
