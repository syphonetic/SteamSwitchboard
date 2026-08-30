namespace SteamSwitchboard.Services;

public sealed class AppPaths
{
    public AppPaths(string? localAppDataOverride = null)
    {
        var localAppData = localAppDataOverride
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Windows did not provide a local application-data folder.");
        }

        if (!LocalPathPolicy.TryNormalizeLocalPath(
                localAppData,
                out var normalizedLocalAppData))
        {
            throw new InvalidOperationException(
                "The local application-data folder is not a safe local path.");
        }

        Root = Path.Combine(normalizedLocalAppData, "SteamSwitchboard");
        StateFile = Path.Combine(Root, "state.json");
        BrowserData = Path.Combine(Root, "BrowserData");
        Logs = Path.Combine(Root, "Logs");
    }

    public string Root { get; }

    public string StateFile { get; }

    public string BrowserData { get; }

    public string Logs { get; }

    public void EnsureCreated()
    {
        EnsureSafeDirectory(Root);
        EnsureSafeDirectory(BrowserData);
        EnsureSafeDirectory(Logs);
    }

    private static void EnsureSafeDirectory(string path)
    {
        if (!LocalPathPolicy.TryNormalizeLocalPath(
                path,
                out _,
                requireExisting: false))
        {
            throw new InvalidOperationException(
                "Switchboard's data folder is not a safe local path.");
        }

        Directory.CreateDirectory(path);
        if (!LocalPathPolicy.TryNormalizeLocalPath(path, out _))
        {
            throw new InvalidOperationException(
                "Switchboard's data folder cannot use links or remote storage.");
        }
    }
}
