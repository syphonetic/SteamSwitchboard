namespace SteamSwitchboard.Services;

internal sealed class BrowserNavigationTracker
{
    private const int MaximumTrackedCancellations = 32;

    private readonly HashSet<ulong> _hostCancelledNavigationIds = [];
    private bool _hasAllowedNavigation;
    private ulong _latestAllowedNavigationId;

    public void RecordAllowedNavigation(ulong navigationId)
    {
        _latestAllowedNavigationId = navigationId;
        _hasAllowedNavigation = true;
    }

    public void RecordHostCancellation(ulong navigationId)
    {
        if (_hostCancelledNavigationIds.Count >= MaximumTrackedCancellations)
        {
            _hostCancelledNavigationIds.Clear();
        }

        _hostCancelledNavigationIds.Add(navigationId);
    }

    public bool ShouldHandleCompletion(ulong navigationId)
    {
        if (_hostCancelledNavigationIds.Remove(navigationId))
        {
            return false;
        }

        return _hasAllowedNavigation
            && navigationId == _latestAllowedNavigationId;
    }

    public void Reset()
    {
        _hostCancelledNavigationIds.Clear();
        _hasAllowedNavigation = false;
        _latestAllowedNavigationId = 0;
    }
}
