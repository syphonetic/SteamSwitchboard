namespace SteamSwitchboard.Services;

internal sealed class NotificationCleanupGenerationBarrier
{
    private readonly object _gate = new();
    private Guid? _generation;
    private Task<bool>? _barrier;

    public async Task<bool> WaitForLatestAsync(
        Func<Guid?> readLatestGeneration,
        Func<Guid, Task<bool>> executeGeneration)
    {
        ArgumentNullException.ThrowIfNull(readLatestGeneration);
        ArgumentNullException.ThrowIfNull(executeGeneration);
        while (readLatestGeneration() is Guid generation)
        {
            Task<bool> barrier;
            lock (_gate)
            {
                if (_generation != generation || _barrier is null)
                {
                    var createdBarrier = executeGeneration(generation);
                    _generation = generation;
                    _barrier = createdBarrier;
                }

                barrier = _barrier;
            }

            if (!await barrier.ConfigureAwait(true))
            {
                return false;
            }
        }

        return true;
    }
}
