using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class NotificationPrivacyGateTests
{
    [TestMethod]
    public async Task QueuedDeliveryIsRejectedAfterPrivacyRevocation()
    {
        var gate = new NotificationPrivacyGate();
        var deliveryRevision = gate.Capture();
        var shown = new List<string>();
        using var commandStarted = new ManualResetEventSlim();
        using var releaseCommand = new ManualResetEventSlim();
        using var queue = new OrderedCommandQueue<List<string>>(shown);
        var blocker = queue.EnqueueAsync(
            _ =>
            {
                commandStarted.Set();
                releaseCommand.Wait();
                return true;
            },
            rejectedResult: false);
        Assert.IsTrue(commandStarted.Wait(TimeSpan.FromSeconds(5)));
        var delivery = queue.EnqueueAsync(
            shown =>
            gate.ExecuteIfCurrent(
                deliveryRevision,
                () =>
                {
                    shown.Add("sensitive preview");
                    return true;
                }),
            rejectedResult: false);

        gate.Revoke();
        releaseCommand.Set();

        Assert.IsTrue(await blocker);
        Assert.IsFalse(await delivery);
        Assert.IsEmpty(shown);
    }

    [TestMethod]
    public async Task RevocationCannotPassAnExecutingDelivery()
    {
        var gate = new NotificationPrivacyGate();
        var deliveryRevision = gate.Capture();
        using var deliveryEntered = new ManualResetEventSlim();
        using var releaseDelivery = new ManualResetEventSlim();
        var delivery = Task.Run(() => gate.ExecuteIfCurrent(
            deliveryRevision,
            () =>
            {
                deliveryEntered.Set();
                releaseDelivery.Wait();
                return true;
            }));
        Assert.IsTrue(deliveryEntered.Wait(TimeSpan.FromSeconds(5)));

        var revocation = Task.Run(gate.Revoke);
        await Task.Delay(50);
        Assert.IsFalse(revocation.IsCompleted);

        releaseDelivery.Set();
        Assert.IsTrue(await delivery.WaitAsync(TimeSpan.FromSeconds(5)));
        await revocation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(gate.IsCurrent(deliveryRevision));
    }
}
