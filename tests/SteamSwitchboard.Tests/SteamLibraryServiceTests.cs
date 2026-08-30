using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class SteamLibraryServiceTests
{
    [TestMethod]
    public async Task LoadInstalledGames_ReadsPrimaryAndAdditionalLibraries()
    {
        using var temporary = new TemporaryDirectory();
        var steamRoot = temporary.CreateDirectory("Steam");
        var primaryApps = temporary.CreateDirectory("Steam", "steamapps");
        var secondaryRoot = temporary.CreateDirectory("Library");
        _ = temporary.CreateDirectory("Library", "steamapps");
        _ = temporary.CreateDirectory("Steam", "steamapps", "common", "PrimaryGame");
        _ = temporary.CreateDirectory("Library", "steamapps", "common", "SecondGame");

        File.WriteAllText(
            System.IO.Path.Combine(primaryApps, "libraryfolders.vdf"),
            $$"""
            "libraryfolders"
            {
                "0" { "path" "{{EscapeForVdf(steamRoot)}}" }
                "1" { "path" "{{EscapeForVdf(secondaryRoot)}}" }
            }
            """);
        File.WriteAllText(
            System.IO.Path.Combine(primaryApps, "appmanifest_10.acf"),
            Manifest(10, "Primary Game", "PrimaryGame", 1_073_741_824));
        File.WriteAllText(
            System.IO.Path.Combine(secondaryRoot, "steamapps", "appmanifest_20.acf"),
            Manifest(20, "Second Game", "SecondGame", 2_147_483_648));

        var games = await new SteamLibraryService().LoadInstalledGamesAsync(steamRoot);

        Assert.HasCount(2, games);
        Assert.AreEqual("Primary Game", games[0].Name);
        Assert.AreEqual("1 GB", games[0].SizeLabel);
        Assert.AreEqual("Second Game", games[1].Name);
        Assert.AreEqual(secondaryRoot, games[1].LibraryPath);
    }

    [TestMethod]
    public async Task LoadInstalledGames_SkipsMalformedManifests()
    {
        using var temporary = new TemporaryDirectory();
        var steamRoot = temporary.CreateDirectory("Steam");
        var steamApps = temporary.CreateDirectory("Steam", "steamapps");
        File.WriteAllText(System.IO.Path.Combine(steamApps, "appmanifest_bad.acf"), "broken {");

        var games = await new SteamLibraryService().LoadInstalledGamesAsync(steamRoot);

        Assert.IsEmpty(games);
    }

    [TestMethod]
    public async Task LoadInstalledGames_RejectsManifestWhoseFilenameDoesNotMatchAppId()
    {
        using var temporary = new TemporaryDirectory();
        var steamRoot = temporary.CreateDirectory("Steam");
        var steamApps = temporary.CreateDirectory("Steam", "steamapps");
        _ = temporary.CreateDirectory("Steam", "steamapps", "common", "Game");
        File.WriteAllText(
            System.IO.Path.Combine(steamApps, "appmanifest_10.acf"),
            Manifest(11, "Wrong identity", "Game", 1));

        var games = await new SteamLibraryService().LoadInstalledGamesAsync(steamRoot);

        Assert.IsEmpty(games);
    }

    [TestMethod]
    public async Task LoadInstalledGames_RejectsInstallDirectoryTraversal()
    {
        using var temporary = new TemporaryDirectory();
        var steamRoot = temporary.CreateDirectory("Steam");
        var steamApps = temporary.CreateDirectory("Steam", "steamapps");
        _ = temporary.CreateDirectory("Steam", "steamapps", "common");
        _ = temporary.CreateDirectory("outside");
        File.WriteAllText(
            System.IO.Path.Combine(steamApps, "appmanifest_10.acf"),
            Manifest(10, "Traversal", @"..\..\..\outside", 1));

        var games = await new SteamLibraryService().LoadInstalledGamesAsync(steamRoot);

        Assert.IsEmpty(games);
    }

    [TestMethod]
    public async Task LoadInstalledGames_SanitizesUntrustedGameNames()
    {
        using var temporary = new TemporaryDirectory();
        var steamRoot = temporary.CreateDirectory("Steam");
        var steamApps = temporary.CreateDirectory("Steam", "steamapps");
        _ = temporary.CreateDirectory("Steam", "steamapps", "common", "Game");
        File.WriteAllText(
            System.IO.Path.Combine(steamApps, "appmanifest_10.acf"),
            Manifest(10, "Trusted\u202Eexe", "Game", 1));

        var games = await new SteamLibraryService().LoadInstalledGamesAsync(steamRoot);

        Assert.HasCount(1, games);
        Assert.IsFalse(games[0].Name.Contains('\u202E', StringComparison.Ordinal));
        Assert.AreEqual("Trusted exe", games[0].Name);
    }

    [TestMethod]
    public void FindLibraryFolders_RejectsRemoteLibraryMetadata()
    {
        using var temporary = new TemporaryDirectory();
        var steamRoot = temporary.CreateDirectory("Steam");
        var steamApps = temporary.CreateDirectory("Steam", "steamapps");
        File.WriteAllText(
            System.IO.Path.Combine(steamApps, "libraryfolders.vdf"),
            "\"libraryfolders\" { \"1\" { \"path\" \"\\\\\\\\attacker.invalid\\\\share\" } }");

        var folders = new SteamLibraryService().FindLibraryFolders(steamRoot);

        Assert.HasCount(1, folders);
        Assert.AreEqual(steamRoot, folders[0]);
    }

    private static string Manifest(uint id, string name, string directory, long size) => $$"""
        "AppState"
        {
            "appid" "{{id}}"
            "name" "{{name}}"
            "installdir" "{{directory}}"
            "SizeOnDisk" "{{size}}"
            "LastUpdated" "1700000000"
        }
        """;

    private static string EscapeForVdf(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
}
