using System.Diagnostics;
using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class WindowsAppNotificationServiceTests
{
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [TestMethod]
    public void AppLogoUri_UsesThePackagedPngAsAnAbsoluteLocalFile()
    {
        using var temporary = new TemporaryDirectory();
        var logoDirectory = temporary.CreateDirectory(
            "Assets",
            "Branding");
        var logoPath = Path.Combine(
            logoDirectory,
            "SteamSwitchboard-app-logo.png");
        var packagedLogoPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Branding",
            "SteamSwitchboard-app-logo.png");
        File.Copy(packagedLogoPath, logoPath);

        var opened = WindowsAppNotificationService.TryOpenAppLogo(
            temporary.Path,
            out var uri,
            out var readLease);

        Assert.IsTrue(opened);
        Assert.IsNotNull(uri);
        Assert.IsTrue(uri.IsFile);
        Assert.AreEqual(Path.GetFullPath(logoPath), uri.LocalPath);
        Assert.IsNotNull(readLease);
        using (readLease)
        {
            Assert.ThrowsExactly<IOException>(() =>
            {
                using var writer = new FileStream(
                    logoPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            });
        }

        using var service = new WindowsAppNotificationService(temporary.Path);
        Assert.ThrowsExactly<IOException>(() =>
        {
            using var writer = new FileStream(
                logoPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
        });
        service.Dispose();
        using var releasedWriter = new FileStream(
            logoPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
    }

    [TestMethod]
    public void AppLogoUri_FailsClosedForMissingInvalidOrOversizedAssets()
    {
        using var temporary = new TemporaryDirectory();
        Assert.IsFalse(WindowsAppNotificationService.TryOpenAppLogo(
            temporary.Path,
            out _,
            out _));
        Assert.IsFalse(WindowsAppNotificationService.TryOpenAppLogo(
            @"\\attacker.invalid\share\SteamSwitchboard",
            out _,
            out _));

        var logoDirectory = temporary.CreateDirectory(
            "Assets",
            "Branding");
        var logoPath = Path.Combine(
            logoDirectory,
            "SteamSwitchboard-app-logo.png");
        File.WriteAllBytes(logoPath, []);
        Assert.IsFalse(WindowsAppNotificationService.TryOpenAppLogo(
            temporary.Path,
            out _,
            out _));

        File.WriteAllText(logoPath, "not a PNG");
        Assert.IsFalse(WindowsAppNotificationService.TryOpenAppLogo(
            temporary.Path,
            out _,
            out _));

        using (var oversized = new FileStream(
                   logoPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            oversized.Write(PngSignature);
            oversized.SetLength((1024 * 1024) + 1);
        }
        Assert.IsFalse(WindowsAppNotificationService.TryOpenAppLogo(
            temporary.Path,
            out _,
            out _));
    }

    [TestMethod]
    public void AppLogoUri_RejectsReparsePointApplicationDirectories()
    {
        using var temporary = new TemporaryDirectory();
        var target = temporary.CreateDirectory("real-app");
        var targetBranding = Directory.CreateDirectory(Path.Combine(
            target,
            "Assets",
            "Branding"));
        File.Copy(
            Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Branding",
                "SteamSwitchboard-app-logo.png"),
            Path.Combine(
                targetBranding.FullName,
                "SteamSwitchboard-app-logo.png"));
        var link = Path.Combine(temporary.Path, "linked-app");
        var startInfo = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);
        using (var process = Process.Start(startInfo))
        {
            Assert.IsNotNull(process);
            Assert.IsTrue(process.WaitForExit(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, process.ExitCode);
        }

        try
        {
            Assert.IsTrue(BrandAssetPolicy.TryOpenAppLogoForRendering(
                link,
                out var renderingLease));
            renderingLease?.Dispose();
            Assert.IsFalse(WindowsAppNotificationService.TryOpenAppLogo(
                link,
                out _,
                out _));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public void ReplacementTag_IsBoundedOpaqueAndAccountScoped()
    {
        var firstAccount = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondAccount = Guid.Parse("11111111-1111-1111-2222-222222222222");

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
    public void NotificationGroup_IsolatesTestsFromAccountHistory()
    {
        var firstAccount = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondAccount = Guid.Parse("11111111-1111-1111-2222-222222222222");

        var firstAccountGroup = WindowsAppNotificationService.GetNotificationGroup(
            firstAccount,
            isTest: false);
        var secondAccountGroup = WindowsAppNotificationService.GetNotificationGroup(
            secondAccount,
            isTest: false);
        var selectedAccountTestGroup = WindowsAppNotificationService.GetNotificationGroup(
            firstAccount,
            isTest: true);
        var accountlessTestGroup = WindowsAppNotificationService.GetNotificationGroup(
            accountId: null,
            isTest: true);

        Assert.IsNotNull(firstAccountGroup);
        Assert.IsNotNull(secondAccountGroup);
        Assert.AreEqual(32, firstAccountGroup.Length);
        Assert.AreEqual(32, secondAccountGroup.Length);
        Assert.AreNotEqual(firstAccountGroup, secondAccountGroup);
        Assert.AreEqual(selectedAccountTestGroup, accountlessTestGroup);
        Assert.AreNotEqual(firstAccountGroup, selectedAccountTestGroup);
        Assert.IsNull(WindowsAppNotificationService.GetNotificationGroup(
            accountId: null,
            isTest: false));
    }

    [TestMethod]
    public void CompatibilityReplacement_IsolatesTestsFromGenuineAlerts()
    {
        var accountId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Assert.IsTrue(WindowsNotificationDeliveryPolicy.IsSameReplacement(
            accountId,
            "sender-tag",
            existingIsTest: false,
            accountId,
            "sender-tag",
            incomingIsTest: false));
        Assert.IsFalse(WindowsNotificationDeliveryPolicy.IsSameReplacement(
            accountId,
            "settings-test",
            existingIsTest: false,
            accountId,
            "settings-test",
            incomingIsTest: true));
        Assert.IsFalse(WindowsNotificationDeliveryPolicy.IsSameReplacement(
            accountId,
            "settings-test",
            existingIsTest: true,
            accountId,
            "settings-test",
            incomingIsTest: false));
        Assert.IsFalse(WindowsNotificationDeliveryPolicy.IsSameReplacement(
            accountId,
            "sender-tag",
            existingIsTest: false,
            accountId,
            incomingReplacementTag: null,
            incomingIsTest: false));
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
            },
            rejectedResult: false);
        Assert.IsTrue(firstStarted.Wait(TimeSpan.FromSeconds(5)));

        var second = queue.EnqueueAsync(
            values =>
            {
                values.Add(2);
                return true;
            },
            rejectedResult: false);
        var third = queue.EnqueueAsync(
            values =>
            {
                values.Add(3);
                return true;
            },
            rejectedResult: false);

        releaseFirst.Set();
        await Task.WhenAll(first, second, third);

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, observed);
        queue.Dispose();
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task OrderedCommandQueue_RejectsImmediatelyWhenCapacityIsFull()
    {
        var observed = new List<int>();
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var queue = new OrderedCommandQueue<List<int>>(
            observed,
            maximumPendingCommands: 1);

        var first = queue.EnqueueAsync(
            values =>
            {
                firstStarted.Set();
                releaseFirst.Wait();
                values.Add(1);
                return true;
            },
            rejectedResult: false);
        Assert.IsTrue(firstStarted.Wait(TimeSpan.FromSeconds(5)));
        var second = queue.EnqueueAsync(
            values =>
            {
                values.Add(2);
                return true;
            },
            rejectedResult: false);
        var rejected = queue.EnqueueAsync(
            values =>
            {
                values.Add(3);
                return true;
            },
            rejectedResult: false);

        Assert.IsFalse(await rejected.WaitAsync(TimeSpan.FromSeconds(1)));
        releaseFirst.Set();
        Assert.IsTrue(await first);
        Assert.IsTrue(await second);
        CollectionAssert.AreEqual(new[] { 1, 2 }, observed);
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
            },
            rejectedResult: false);
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
