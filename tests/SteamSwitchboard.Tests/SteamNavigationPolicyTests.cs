using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class SteamNavigationPolicyTests
{
    [TestMethod]
    [DataRow("https://steamcommunity.com/chat/")]
    [DataRow("https://steamcommunity.com/login/home/?goto=chat%2F")]
    [DataRow("about:blank")]
    public void IsAllowedSteamNavigation_AllowsOnlyExpectedSteamPages(string uri)
    {
        Assert.IsTrue(SteamNavigationPolicy.IsAllowedSteamNavigation(uri));
    }

    [TestMethod]
    [DataRow("http://steamcommunity.com/chat/")]
    [DataRow("https://steamcommunity.com.evil.example/chat/")]
    [DataRow("https://notsteamcommunity.com/chat/")]
    [DataRow("https://evil.steamcommunity.com/chat/")]
    [DataRow("https://steamcommunity.com/profiles/123")]
    [DataRow("https://help.steampowered.com/en/")]
    [DataRow("https://cdn.cloudflare.steamstatic.com/file.png")]
    [DataRow("https://steamcommunity.com:4443/chat/")]
    [DataRow("https://user@steamcommunity.com/chat/")]
    [DataRow("javascript:alert(1)")]
    [DataRow("file:///C:/Windows/win.ini")]
    [DataRow("not a uri")]
    public void IsAllowedSteamNavigation_RejectsUnsafeOrLookalikePages(string uri)
    {
        Assert.IsFalse(SteamNavigationPolicy.IsAllowedSteamNavigation(uri));
    }

    [TestMethod]
    public void GetSafeExternalUri_RequiresHttps()
    {
        Assert.IsNotNull(SteamNavigationPolicy.GetSafeExternalUri("https://example.com/path"));
        Assert.IsNull(SteamNavigationPolicy.GetSafeExternalUri("http://example.com/path"));
        Assert.IsNull(SteamNavigationPolicy.GetSafeExternalUri("steam://run/10"));
        Assert.IsNull(SteamNavigationPolicy.GetSafeExternalUri("https://user@example.com/path"));
        Assert.IsNull(SteamNavigationPolicy.GetSafeExternalUri("https://example.com:4443/path"));
    }

    [TestMethod]
    public void IsBootstrapDocument_AcceptsOnlyTheExactInternalBlankPage()
    {
        Assert.IsTrue(SteamNavigationPolicy.IsBootstrapDocument("about:blank"));
        Assert.IsFalse(SteamNavigationPolicy.IsBootstrapDocument("about:blank#spoof"));
        Assert.IsFalse(SteamNavigationPolicy.IsBootstrapDocument("https://steamcommunity.com/chat/"));
    }

    [TestMethod]
    public void GetSafeExternalUri_RejectsOversizedDestinations()
    {
        var uri = "https://example.com/" + new string('a', 3_000);

        Assert.IsNull(SteamNavigationPolicy.GetSafeExternalUri(uri));
    }

    [TestMethod]
    public void ShouldPromptForExternalLink_RequiresDirectUserAction()
    {
        const string destination = "https://example.com/path";

        Assert.IsTrue(SteamNavigationPolicy.ShouldPromptForExternalLink(destination, true));
        Assert.IsFalse(SteamNavigationPolicy.ShouldPromptForExternalLink(destination, false));
    }

    [TestMethod]
    public void CanRequestMicrophone_RequiresTrustedVisibleUserInitiatedWorkspace()
    {
        const string steamUri = "https://steamcommunity.com/chat/";

        Assert.IsTrue(SteamNavigationPolicy.CanRequestMicrophone(steamUri, true, true));
        Assert.IsFalse(SteamNavigationPolicy.CanRequestMicrophone(steamUri, false, true));
        Assert.IsFalse(SteamNavigationPolicy.CanRequestMicrophone(steamUri, true, false));
        Assert.IsFalse(SteamNavigationPolicy.CanRequestMicrophone("https://example.com/", true, true));
    }
}
