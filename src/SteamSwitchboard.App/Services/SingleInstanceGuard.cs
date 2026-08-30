namespace SteamSwitchboard.Services;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    public static SingleInstanceGuard? TryAcquire(string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        var mutex = new Mutex(
            initiallyOwned: false,
            $@"Local\{applicationName}.SingleInstance");
        try
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.Zero))
                {
                    mutex.Dispose();
                    return null;
                }
            }
            catch (AbandonedMutexException)
            {
                // Ownership transfers to this process after an unclean prior exit.
            }

            return new SingleInstanceGuard(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (!_ownsMutex)
        {
            return;
        }

        _ownsMutex = false;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex was already released during process teardown.
        }

        _mutex.Dispose();
    }
}
