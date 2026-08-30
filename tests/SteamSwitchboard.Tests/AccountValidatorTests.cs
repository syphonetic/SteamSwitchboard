using SteamSwitchboard.Models;
using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class AccountValidatorTests
{
    [TestMethod]
    public void Validate_RejectsDuplicateSteamLoginNameIgnoringCase()
    {
        var existing = new AccountProfile
        {
            DisplayName = "Main",
            SteamLoginName = "Example_User"
        };
        var candidate = new AccountProfile
        {
            DisplayName = "Other",
            SteamLoginName = "example_user"
        };

        var error = AccountValidator.Validate(candidate, [existing]);

        Assert.AreEqual("That Steam account is already in Switchboard.", error);
    }

    [TestMethod]
    public void Normalize_TrimsUserVisibleFields()
    {
        var account = new AccountProfile
        {
            DisplayName = "  Main  ",
            SteamLoginName = "  login_name  "
        };

        AccountValidator.Normalize(account);

        Assert.AreEqual("Main", account.DisplayName);
        Assert.AreEqual("login_name", account.SteamLoginName);
    }

    [TestMethod]
    public void Validate_RejectsControlCharacters()
    {
        var account = new AccountProfile
        {
            DisplayName = "Main",
            SteamLoginName = "name\nother"
        };

        Assert.IsNotNull(AccountValidator.Validate(account, []));
    }

    [TestMethod]
    public void Validate_RejectsBidirectionalIdentityOverrides()
    {
        var account = new AccountProfile
        {
            DisplayName = "Alice\u202Eevil",
            SteamLoginName = "alice"
        };

        Assert.IsNotNull(AccountValidator.Validate(account, []));
    }

    [TestMethod]
    [DataRow("name-with-dash")]
    [DataRow("néme")]
    [DataRow("a")]
    [DataRow("name name")]
    public void Validate_RejectsSteamLoginNamesThatCouldSpoofIdentity(string loginName)
    {
        var account = new AccountProfile
        {
            DisplayName = "Account",
            SteamLoginName = loginName
        };

        Assert.IsNotNull(AccountValidator.Validate(account, []));
    }

    [TestMethod]
    public void Normalize_UsesUnicodeCanonicalComposition()
    {
        var account = new AccountProfile
        {
            DisplayName = "Cafe\u0301",
            SteamLoginName = "cafe"
        };

        AccountValidator.Normalize(account);

        Assert.AreEqual("Café", account.DisplayName);
    }

    [TestMethod]
    [DataRow("66C0F4")]
    [DataRow("#66C0FG")]
    [DataRow("#66C0F4FF")]
    public void Validate_RejectsInvalidAccentColors(string accent)
    {
        var account = new AccountProfile
        {
            DisplayName = "Account",
            SteamLoginName = "account",
            AccentHex = accent
        };

        Assert.IsNotNull(AccountValidator.Validate(account, []));
    }

    [TestMethod]
    public void Validate_RejectsDuplicateLocalProfileIdentifiers()
    {
        var id = Guid.NewGuid();
        var existing = new AccountProfile
        {
            Id = id,
            DisplayName = "Existing",
            SteamLoginName = "existing"
        };
        var candidate = new AccountProfile
        {
            Id = id,
            DisplayName = "Candidate",
            SteamLoginName = "candidate"
        };

        var error = AccountValidator.Validate(candidate, [existing]);

        Assert.AreEqual(
            "That local profile identifier is already in Switchboard.",
            error);
    }
}
