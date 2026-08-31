namespace SteamSwitchboard.Services;

internal sealed class NotificationPrivacyGate
{
    private readonly object _gate = new();
    private long _revision;

    public long Capture()
    {
        lock (_gate)
        {
            return _revision;
        }
    }

    public bool IsCurrent(long revision) => revision == Capture();

    public bool ExecuteIfCurrent(long revision, Func<bool> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            return revision == _revision && action();
        }
    }

    public void Revoke()
    {
        lock (_gate)
        {
            _revision++;
        }
    }
}
