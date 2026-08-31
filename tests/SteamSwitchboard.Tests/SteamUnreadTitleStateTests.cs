using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class SteamUnreadTitleStateTests
{
    [TestMethod]
    public void HiddenWorkspace_PreservesUnreadForUnrecognisedTitleChanges()
    {
        Assert.AreEqual(
            1,
            SteamUnreadTitleState.ResolveUnreadCount(
                "Steam Community :: Steam Chat",
                1,
                isWorkspaceVisible: false));
        Assert.AreEqual(
            7,
            SteamUnreadTitleState.ResolveUnreadCount(
                null,
                7,
                isWorkspaceVisible: false));
    }

    [TestMethod]
    public void HiddenWorkspace_AcceptsExplicitNumericUnreadPrefix()
    {
        Assert.AreEqual(
            83,
            SteamUnreadTitleState.ResolveUnreadCount(
                "(83) Steam Chat",
                1,
                isWorkspaceVisible: false));
        Assert.AreEqual(
            0,
            SteamUnreadTitleState.ResolveUnreadCount(
                "(0) Steam Chat",
                83,
                isWorkspaceVisible: false));
    }

    [TestMethod]
    public void HiddenWorkspace_BoundsLargeCountsAndRejectsMalformedPrefixes()
    {
        Assert.AreEqual(
            100,
            SteamUnreadTitleState.ResolveUnreadCount(
                "(2147483647) Steam Chat",
                2,
                isWorkspaceVisible: false));
        Assert.AreEqual(
            2,
            SteamUnreadTitleState.ResolveUnreadCount(
                "(12x) Steam Chat",
                2,
                isWorkspaceVisible: false));
        Assert.AreEqual(
            2,
            SteamUnreadTitleState.ResolveUnreadCount(
                "(12345678901) Steam Chat",
                2,
                isWorkspaceVisible: false));
    }

    [TestMethod]
    public void VisibleWorkspace_ClearsUnreadRegardlessOfTitle()
    {
        Assert.AreEqual(
            0,
            SteamUnreadTitleState.ResolveUnreadCount(
                "(83) Steam Chat",
                83,
                isWorkspaceVisible: true));
    }
}
