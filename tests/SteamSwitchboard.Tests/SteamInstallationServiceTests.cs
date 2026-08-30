using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class SteamInstallationServiceTests
{
    [TestMethod]
    public void FindSteamExecutable_RequiresTrustVerification()
    {
        using var temporary = new TemporaryDirectory();
        var fakeSteam = temporary.CreateFile(System.IO.Path.Combine("Steam", "steam.exe"));
        var service = new SteamInstallationService(
            _ => false,
            _ => [fakeSteam]);

        Assert.IsNull(service.FindSteamExecutable(fakeSteam));
    }

    [TestMethod]
    public void FindSteamExecutable_AcceptsValidatedLocalCandidate()
    {
        using var temporary = new TemporaryDirectory();
        var fakeSteam = temporary.CreateFile(System.IO.Path.Combine("Steam", "steam.exe"));
        var service = new SteamInstallationService(
            _ => true,
            _ => [fakeSteam]);

        Assert.AreEqual(fakeSteam, service.FindSteamExecutable(fakeSteam));
    }

    [TestMethod]
    public void FindSteamExecutable_RejectsRemotePathsBeforeTrustInspection()
    {
        var trustWasCalled = false;
        var service = new SteamInstallationService(
            _ =>
            {
                trustWasCalled = true;
                return true;
            },
            _ => [@"\\attacker.invalid\share\steam.exe"]);

        Assert.IsNull(service.FindSteamExecutable());
        Assert.IsFalse(trustWasCalled);
    }

    [TestMethod]
    public void SteamExecutableTrust_RejectsUnsignedFiles()
    {
        using var temporary = new TemporaryDirectory();
        var fakeSteam = temporary.CreateFile("steam.exe", "not an executable");

        Assert.IsFalse(SteamExecutableTrust.IsTrustedValveExecutable(fakeSteam));
    }
}
