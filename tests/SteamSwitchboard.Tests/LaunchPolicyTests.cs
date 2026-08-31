using SteamSwitchboard.Models;
using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class LaunchPolicyTests
{
    [TestMethod]
    public void Assess_BlocksLaunchWhenWrongSteamAccountIsActive()
    {
        using var temporary = new TemporaryDirectory();
        var steamExe = temporary.CreateFile("Steam/steam.exe");
        var gameDirectory = temporary.CreateDirectory("Library", "steamapps", "common", "Game");
        var selected = new AccountProfile
        {
            DisplayName = "Main",
            SteamLoginName = "main_login"
        };
        var active = new SteamClientAccount("123", "other_login", "Other", true, null);
        var game = new InstalledGame(10, "Game", gameDirectory, temporary.Path, 1, null);

        var result = LaunchPolicy.Assess(selected, game, steamExe, true, active);

        Assert.AreEqual(LaunchReadiness.AccountSwitchRequired, result.Readiness);
        Assert.IsFalse(result.CanLaunch);
        StringAssert.Contains(result.Message, "other_login");
        StringAssert.Contains(result.Message, "main_login");
        Assert.IsFalse(
            result.Message.Contains("play as", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Assess_AllowsLaunchOnlyAfterExactAccountMatch()
    {
        using var temporary = new TemporaryDirectory();
        var steamExe = temporary.CreateFile("Steam/steam.exe");
        var gameDirectory = temporary.CreateDirectory("Library", "steamapps", "common", "Game");
        var selected = new AccountProfile
        {
            DisplayName = "Main",
            SteamLoginName = "Main_Login"
        };
        var active = new SteamClientAccount("123", "main_login", "Main", true, null);
        var game = new InstalledGame(10, "Game", gameDirectory, temporary.Path, 1, null);

        var result = LaunchPolicy.Assess(selected, game, steamExe, true, active);

        Assert.AreEqual(LaunchReadiness.Ready, result.Readiness);
        Assert.IsTrue(result.CanLaunch);
    }

    [TestMethod]
    public void Assess_BlocksMissingGameBeforeStartingSteam()
    {
        using var temporary = new TemporaryDirectory();
        var steamExe = temporary.CreateFile("Steam/steam.exe");
        var selected = new AccountProfile
        {
            DisplayName = "Main",
            SteamLoginName = "main_login"
        };
        var game = new InstalledGame(10, "Game", System.IO.Path.Combine(temporary.Path, "missing"), temporary.Path, 1, null);

        var result = LaunchPolicy.Assess(selected, game, steamExe, false, null);

        Assert.AreEqual(LaunchReadiness.GameNotInstalled, result.Readiness);
    }

    [TestMethod]
    public void Assess_FailsClosedWhenActiveAccountCannotBeVerified()
    {
        using var temporary = new TemporaryDirectory();
        var steamExe = temporary.CreateFile("Steam/steam.exe");
        var gameDirectory = temporary.CreateDirectory("Library", "steamapps", "common", "Game");
        var selected = new AccountProfile
        {
            DisplayName = "Main",
            SteamLoginName = "main_login"
        };
        var game = new InstalledGame(10, "Game", gameDirectory, temporary.Path, 1, null);

        var result = LaunchPolicy.Assess(selected, game, steamExe, true, null);

        Assert.AreEqual(LaunchReadiness.ActiveAccountUnknown, result.Readiness);
        Assert.IsFalse(result.CanLaunch);
    }
}
