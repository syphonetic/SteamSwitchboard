using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class BrowserNavigationTrackerTests
{
    [TestMethod]
    public void ShouldHandleCompletion_AcceptsOnlyLatestAllowedNavigation()
    {
        var tracker = new BrowserNavigationTracker();
        tracker.RecordAllowedNavigation(10);
        tracker.RecordAllowedNavigation(11);

        Assert.IsFalse(tracker.ShouldHandleCompletion(10));
        Assert.IsTrue(tracker.ShouldHandleCompletion(11));
    }

    [TestMethod]
    public void ShouldHandleCompletion_IgnoresHostCancelledNavigation()
    {
        var tracker = new BrowserNavigationTracker();
        tracker.RecordAllowedNavigation(20);
        tracker.RecordHostCancellation(21);

        Assert.IsFalse(tracker.ShouldHandleCompletion(21));
        Assert.IsTrue(tracker.ShouldHandleCompletion(20));
    }

    [TestMethod]
    public void Reset_RejectsCompletionsUntilAnotherAllowedNavigationStarts()
    {
        var tracker = new BrowserNavigationTracker();
        tracker.RecordAllowedNavigation(30);

        tracker.Reset();

        Assert.IsFalse(tracker.ShouldHandleCompletion(30));
    }
}
