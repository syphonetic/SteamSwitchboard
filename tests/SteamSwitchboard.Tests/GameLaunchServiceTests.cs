using System.Diagnostics;
using SteamSwitchboard.Models;
using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class GameLaunchServiceTests
{
    [TestMethod]
    public void LaunchIfReady_StartsOnlyAfterRepeatedStableAccountChecks()
    {
        using var fixture = new LaunchFixture(42, 42);

        var result = fixture.Launcher.LaunchIfReady(
            fixture.Account,
            fixture.Game,
            fixture.SteamExecutable);

        Assert.AreEqual(LaunchReadiness.Ready, result.Readiness);
        Assert.AreEqual(1, fixture.StartCount);
        Assert.IsNotNull(fixture.StartInfo);
        Assert.AreEqual(fixture.SteamExecutable, fixture.StartInfo.FileName);
        CollectionAssert.AreEqual(
            new[] { "-applaunch", "10" },
            fixture.StartInfo.ArgumentList.ToArray());
    }

    [TestMethod]
    public void LaunchIfReady_DoesNotStartWhenActiveAccountChangesDuringVerification()
    {
        using var fixture = new LaunchFixture(42, 43);

        var result = fixture.Launcher.LaunchIfReady(
            fixture.Account,
            fixture.Game,
            fixture.SteamExecutable);

        Assert.AreEqual(LaunchReadiness.AccountSwitchRequired, result.Readiness);
        Assert.AreEqual(0, fixture.StartCount);
    }

    [TestMethod]
    public void LaunchIfReady_DoesNotStartWhenActiveAccountIsUnknown()
    {
        using var fixture = new LaunchFixture(null, null);

        var result = fixture.Launcher.LaunchIfReady(
            fixture.Account,
            fixture.Game,
            fixture.SteamExecutable);

        Assert.AreEqual(LaunchReadiness.ActiveAccountUnknown, result.Readiness);
        Assert.AreEqual(0, fixture.StartCount);
    }

    [TestMethod]
    public void LaunchIfReady_DoesNotStartWhenSteamExecutableIsUntrusted()
    {
        using var fixture = new LaunchFixture(42, 42, executableIsTrusted: false);

        var result = fixture.Launcher.LaunchIfReady(
            fixture.Account,
            fixture.Game,
            fixture.SteamExecutable);

        Assert.AreEqual(LaunchReadiness.SteamNotFound, result.Readiness);
        Assert.AreEqual(0, fixture.StartCount);
    }

    [TestMethod]
    public void LaunchIfReady_DoesNotStartWhenExecutableChangesAfterAssessment()
    {
        using var fixture = new LaunchFixture(
            42,
            42,
            trustDecisions: [true, true, false]);

        var result = fixture.Launcher.LaunchIfReady(
            fixture.Account,
            fixture.Game,
            fixture.SteamExecutable);

        Assert.AreEqual(LaunchReadiness.SteamNotFound, result.Readiness);
        Assert.AreEqual(0, fixture.StartCount);
    }

    [TestMethod]
    public void LaunchIfReady_DoesNotStartWhenAccountChangesAfterExecutableLock()
    {
        using var fixture = new LaunchFixture(
            42,
            42,
            activeAccountIds: [42, 42, 43]);

        var result = fixture.Launcher.LaunchIfReady(
            fixture.Account,
            fixture.Game,
            fixture.SteamExecutable);

        Assert.AreEqual(LaunchReadiness.AccountSwitchRequired, result.Readiness);
        Assert.AreEqual(0, fixture.StartCount);
    }

    private sealed class LaunchFixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary = new();
        private readonly Queue<uint?> _activeAccountIds;

        public LaunchFixture(
            uint? firstAccountId,
            uint? secondAccountId,
            bool executableIsTrusted = true,
            IEnumerable<bool>? trustDecisions = null,
            IEnumerable<uint?>? activeAccountIds = null)
        {
            _activeAccountIds = new Queue<uint?>(
                activeAccountIds ?? [firstAccountId, secondAccountId]);
            var steamRoot = _temporary.CreateDirectory("Steam");
            var config = _temporary.CreateDirectory("Steam", "config");
            SteamExecutable = _temporary.CreateFile(
                System.IO.Path.Combine("Steam", "steam.exe"));
            File.WriteAllText(
                System.IO.Path.Combine(config, "loginusers.vdf"),
                """
                "users"
                {
                    "76561197960265770" { "AccountName" "main_login" "PersonaName" "Main" }
                    "76561197960265771" { "AccountName" "other_login" "PersonaName" "Other" }
                }
                """);

            var gameDirectory = _temporary.CreateDirectory(
                "Library",
                "steamapps",
                "common",
                "Game");
            Game = new InstalledGame(10, "Game", gameDirectory, _temporary.Path, 1, null);
            Account = new AccountProfile
            {
                DisplayName = "Main",
                SteamLoginName = "main_login"
            };

            var trustQueue = new Queue<bool>(trustDecisions ?? []);
            var installation = new SteamInstallationService(
                _ => trustQueue.Count > 1
                    ? trustQueue.Dequeue()
                    : trustQueue.Count == 1
                        ? trustQueue.Peek()
                        : executableIsTrusted,
                _ => [SteamExecutable]);
            var accountService = new SteamClientAccountService(() =>
                _activeAccountIds.Count > 1
                    ? _activeAccountIds.Dequeue()
                    : _activeAccountIds.Peek());
            Launcher = new GameLaunchService(
                installation,
                accountService,
                _ => true,
                startInfo =>
                {
                    StartCount++;
                    StartInfo = startInfo;
                });
        }

        public string SteamExecutable { get; }

        public AccountProfile Account { get; }

        public InstalledGame Game { get; }

        public GameLaunchService Launcher { get; }

        public int StartCount { get; private set; }

        public ProcessStartInfo? StartInfo { get; private set; }

        public void Dispose() => _temporary.Dispose();
    }
}
