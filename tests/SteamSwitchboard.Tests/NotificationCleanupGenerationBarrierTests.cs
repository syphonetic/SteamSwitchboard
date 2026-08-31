using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class NotificationCleanupGenerationBarrierTests
{
    [TestMethod]
    public async Task WaitForLatest_CoalescesGenerationAndBlocksForNewerRequest()
    {
        var firstGeneration = Guid.NewGuid();
        var secondGeneration = Guid.NewGuid();
        Guid? currentGeneration = firstGeneration;
        var firstRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = new List<Guid>();
        var barrier = new NotificationCleanupGenerationBarrier();

        Task<bool> Execute(Guid generation)
        {
            lock (executions)
            {
                executions.Add(generation);
            }

            return generation == firstGeneration
                ? firstRelease.Task
                : secondRelease.Task;
        }

        var firstWaiter = barrier.WaitForLatestAsync(
            () => currentGeneration,
            Execute);
        var secondWaiter = barrier.WaitForLatestAsync(
            () => currentGeneration,
            Execute);
        CollectionAssert.AreEqual(
            new[] { firstGeneration },
            executions.ToArray());

        currentGeneration = secondGeneration;
        firstRelease.SetResult(true);
        await WaitUntilAsync(
            () =>
            {
                lock (executions)
                {
                    return executions.Contains(secondGeneration);
                }
            });
        Assert.IsFalse(firstWaiter.IsCompleted);
        Assert.IsFalse(secondWaiter.IsCompleted);

        currentGeneration = null;
        secondRelease.SetResult(true);
        Assert.IsTrue(await firstWaiter.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(await secondWaiter.WaitAsync(TimeSpan.FromSeconds(5)));
        CollectionAssert.AreEqual(
            new[] { firstGeneration, secondGeneration },
            executions.ToArray());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("The next cleanup generation did not start in time.");
            }

            await Task.Delay(10);
        }
    }
}
