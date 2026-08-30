using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class SteamClientAccountServiceTests
{
    [TestMethod]
    public void FindActiveAccount_FailsClosedWhenAuthoritativeSignalIsUnavailable()
    {
        using var temporary = new TemporaryDirectory();
        var steamRoot = temporary.CreateDirectory("Steam");
        var config = temporary.CreateDirectory("Steam", "config");
        File.WriteAllText(
            System.IO.Path.Combine(config, "loginusers.vdf"),
            """
            "users"
            {
                "76561197960265729" { "AccountName" "first" "PersonaName" "First" "MostRecent" "0" "Timestamp" "200" }
                "76561197960265730" { "AccountName" "second" "PersonaName" "Second" "MostRecent" "1" "Timestamp" "100" }
            }
            """);

        var active = new SteamClientAccountService(() => null).FindActiveAccount(steamRoot);

        Assert.IsNull(active);
    }

    [TestMethod]
    public void FindActiveAccount_PrefersSteamActiveUserOverMostRecentMarker()
    {
        using var temporary = new TemporaryDirectory();
        var steamRoot = temporary.CreateDirectory("Steam");
        var config = temporary.CreateDirectory("Steam", "config");
        File.WriteAllText(
            System.IO.Path.Combine(config, "loginusers.vdf"),
            """
            "users"
            {
                "76561197960265770" { "AccountName" "active" "PersonaName" "Active" "MostRecent" "0" }
                "76561197960265805" { "AccountName" "cached" "PersonaName" "Cached" "MostRecent" "1" }
            }
            """);

        var active = new SteamClientAccountService(() => 42).FindActiveAccount(steamRoot);

        Assert.IsNotNull(active);
        Assert.AreEqual("active", active.AccountName);
    }

    [TestMethod]
    public void FindActiveAccount_ReturnsNullWhenSteamReportsSignedOut()
    {
        using var temporary = new TemporaryDirectory();
        var steamRoot = temporary.CreateDirectory("Steam");
        var config = temporary.CreateDirectory("Steam", "config");
        File.WriteAllText(
            System.IO.Path.Combine(config, "loginusers.vdf"),
            """
            "users"
            {
                "76561197960265770" { "AccountName" "cached" "PersonaName" "Cached" "MostRecent" "1" }
            }
            """);

        var active = new SteamClientAccountService(() => 0).FindActiveAccount(steamRoot);

        Assert.IsNull(active);
    }

    [TestMethod]
    public void LoadAccounts_RejectsMalformedSteamIdsAndUnsafeAccountNames()
    {
        using var temporary = new TemporaryDirectory();
        var steamRoot = temporary.CreateDirectory("Steam");
        var config = temporary.CreateDirectory("Steam", "config");
        File.WriteAllText(
            System.IO.Path.Combine(config, "loginusers.vdf"),
            """
            "users"
            {
                "42" { "AccountName" "small" "PersonaName" "Small" }
                "76561197960265729" { "AccountName" "unsafe-name" "PersonaName" "Unsafe" }
                "76561197960265730" { "AccountName" "safe_name" "PersonaName" "Safe" }
            }
            """);

        var accounts = new SteamClientAccountService(() => 2).LoadAccounts(steamRoot);

        Assert.HasCount(1, accounts);
        Assert.AreEqual("safe_name", accounts[0].AccountName);
    }

    [TestMethod]
    public void LoadAccounts_RemovesIdentityControlCharactersFromPersonaNames()
    {
        using var temporary = new TemporaryDirectory();
        var steamRoot = temporary.CreateDirectory("Steam");
        var config = temporary.CreateDirectory("Steam", "config");
        File.WriteAllText(
            System.IO.Path.Combine(config, "loginusers.vdf"),
            """
            "users"
            {
                "76561197960265729" { "AccountName" "safe_name" "PersonaName" "Alice\u202Eevil" }
            }
            """.Replace("\\u202E", "\u202E", StringComparison.Ordinal));

        var account = new SteamClientAccountService(() => 1)
            .LoadAccounts(steamRoot)
            .Single();

        Assert.AreEqual("Alice evil", account.PersonaName);
        Assert.IsFalse(account.PersonaName.Contains('\u202E', StringComparison.Ordinal));
    }
}
