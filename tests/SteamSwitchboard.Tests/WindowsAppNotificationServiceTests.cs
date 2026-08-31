using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class WindowsAppNotificationServiceTests
{
    [TestMethod]
    public void ReplacementTag_IsBoundedOpaqueAndAccountScoped()
    {
        var firstAccount = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondAccount = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var first = WindowsAppNotificationService.CreateReplacementTag(
            firstAccount,
            "sender-controlled-tag");
        var repeated = WindowsAppNotificationService.CreateReplacementTag(
            firstAccount,
            "sender-controlled-tag");
        var otherAccount = WindowsAppNotificationService.CreateReplacementTag(
            secondAccount,
            "sender-controlled-tag");

        Assert.AreEqual(16, first.Length);
        Assert.AreEqual(first, repeated);
        Assert.AreNotEqual(first, otherAccount);
        Assert.IsFalse(first.Contains("sender", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ActivationArguments_AcceptOnlyBoundedKnownShape()
    {
        var accountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var notificationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var valid = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = "open",
            ["account"] = accountId.ToString("N"),
            ["notification"] = notificationId.ToString("N")
        };

        var accepted = WindowsAppNotificationService.TryParseActivationArguments(
            valid,
            out var parsedAccountId,
            out var parsedNotificationId);

        Assert.IsTrue(accepted);
        Assert.AreEqual(accountId, parsedAccountId);
        Assert.AreEqual(notificationId, parsedNotificationId);
        Assert.IsFalse(WindowsAppNotificationService.TryParseActivationArguments(
            new Dictionary<string, string>
            {
                ["action"] = "open",
                ["account"] = accountId.ToString("D"),
                ["command"] = "anything"
            },
            out _,
            out _));
        Assert.IsFalse(WindowsAppNotificationService.TryParseActivationArguments(
            new Dictionary<string, string>
            {
                ["action"] = "launch",
                ["account"] = accountId.ToString("N")
            },
            out _,
            out _));

        Assert.IsFalse(WindowsAppNotificationService.TryParseActivationArguments(
            new Dictionary<string, string>
            {
                ["action"] = "open",
                ["notification"] = notificationId.ToString("N")
            },
            out _,
            out _));
    }

    [TestMethod]
    public void ActivationArguments_AllowGenericNotificationCenterOpen()
    {
        var accepted = WindowsAppNotificationService.TryParseActivationArguments(
            new Dictionary<string, string>
            {
                ["action"] = "open"
            },
            out var accountId,
            out var notificationId);

        Assert.IsTrue(accepted);
        Assert.IsNull(accountId);
        Assert.IsNull(notificationId);
    }

    [TestMethod]
    public async Task OrderedCommandQueue_PreservesSubmissionOrder()
    {
        var observed = new List<int>();
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var queue = new OrderedCommandQueue<List<int>>(observed);

        var first = queue.EnqueueAsync(
            values =>
            {
                firstStarted.Set();
                releaseFirst.Wait();
                values.Add(1);
                return true;
            });
        Assert.IsTrue(firstStarted.Wait(TimeSpan.FromSeconds(5)));

        var second = queue.EnqueueAsync(
            values =>
            {
                values.Add(2);
                return true;
            });
        var third = queue.EnqueueAsync(
            values =>
            {
                values.Add(3);
                return true;
            });

        releaseFirst.Set();
        await Task.WhenAll(first, second, third);

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, observed);
        queue.Dispose();
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task OrderedCommandQueue_DisposeDrainsAcceptedCommands()
    {
        var observed = new List<int>();
        using var commandStarted = new ManualResetEventSlim();
        using var releaseCommand = new ManualResetEventSlim();
        var queue = new OrderedCommandQueue<List<int>>(observed);
        var accepted = queue.EnqueueAsync(
            values =>
            {
                commandStarted.Set();
                releaseCommand.Wait();
                values.Add(1);
                return true;
            });
        Assert.IsTrue(commandStarted.Wait(TimeSpan.FromSeconds(5)));

        queue.Dispose();
        releaseCommand.Set();
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(await accepted);
        CollectionAssert.AreEqual(new[] { 1 }, observed);
    }

    [TestMethod]
    public void RemoveMany_EmptyRequestSucceedsWithoutRegistration()
    {
        using var service = new WindowsAppNotificationService();

        Assert.IsTrue(service.RemoveMany(removeAll: false, []));
    }

    [TestMethod]
    public void RemoveMany_DisposedServiceCannotConfirmCleanup()
    {
        var service = new WindowsAppNotificationService();
        service.Dispose();

        Assert.IsFalse(service.RemoveMany(removeAll: true, []));
    }
}
