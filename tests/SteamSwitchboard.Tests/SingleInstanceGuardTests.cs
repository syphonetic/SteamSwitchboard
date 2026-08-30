using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class SingleInstanceGuardTests
{
    [TestMethod]
    public void TryAcquire_BlocksAnotherThreadUntilReleased()
    {
        var name = $"SteamSwitchboard.Tests.{Guid.NewGuid():N}";
        using var first = SingleInstanceGuard.TryAcquire(name);
        Assert.IsNotNull(first);

        SingleInstanceGuard? second = null;
        var thread = new Thread(() => second = SingleInstanceGuard.TryAcquire(name));
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));

        Assert.IsNull(second);
    }
}
