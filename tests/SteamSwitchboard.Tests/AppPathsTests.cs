using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class AppPathsTests
{
    [TestMethod]
    public void EnsureCreated_CreatesOnlyExpectedLocalFolders()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);

        paths.EnsureCreated();

        Assert.IsTrue(Directory.Exists(paths.Root));
        Assert.IsTrue(Directory.Exists(paths.BrowserData));
        Assert.IsTrue(Directory.Exists(paths.Logs));
        Assert.IsTrue(LocalPathPolicy.IsStrictDescendant(paths.Root, temporary.Path));
    }

    [TestMethod]
    public void Constructor_RejectsRemoteApplicationDataRoot()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new AppPaths(@"\\attacker.invalid\share"));
    }
}
