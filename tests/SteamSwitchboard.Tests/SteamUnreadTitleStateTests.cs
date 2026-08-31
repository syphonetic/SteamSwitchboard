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

    [TestMethod]
    public void HiddenNotificationOrdinaryTitleAndVisibleRead_StayInCausalOrder()
    {
        var notification = ChatUnreadLifecycle.ResolveAfterNotification(
            currentUnreadCount: 0,
            isSelectedConversation: true,
            isWorkspaceActuallyVisible: false);
        Assert.AreEqual(1, notification.UnreadCount);
        Assert.IsFalse(notification.ShouldMarkRead);
        Assert.IsTrue(notification.ShouldShowWindowsNotification);

        var afterOrdinaryTitle = SteamUnreadTitleState.ResolveUnreadCount(
            "Steam Community :: Steam Chat",
            notification.UnreadCount,
            isWorkspaceVisible: false);
        Assert.AreEqual(1, afterOrdinaryTitle);

        var afterVisibleRead = SteamUnreadTitleState.ResolveUnreadCount(
            "Steam Community :: Steam Chat",
            afterOrdinaryTitle,
            isWorkspaceVisible: true);
        Assert.AreEqual(0, afterVisibleRead);
    }

    [TestMethod]
    public void SelectedButLoadingWorkspace_DoesNotSuppressUnreadAlert()
    {
        var loadingDecision = ChatUnreadLifecycle.ResolveAfterNotification(
            currentUnreadCount: 9,
            isSelectedConversation: true,
            isWorkspaceActuallyVisible: false);

        Assert.AreEqual(9, loadingDecision.UnreadCount);
        Assert.IsFalse(loadingDecision.ShouldMarkRead);
        Assert.IsTrue(loadingDecision.ShouldShowWindowsNotification);

        var visibleDecision = ChatUnreadLifecycle.ResolveAfterNotification(
            currentUnreadCount: loadingDecision.UnreadCount,
            isSelectedConversation: true,
            isWorkspaceActuallyVisible: true);
        Assert.AreEqual(0, visibleDecision.UnreadCount);
        Assert.IsTrue(visibleDecision.ShouldMarkRead);
        Assert.IsFalse(visibleDecision.ShouldShowWindowsNotification);
    }
}
