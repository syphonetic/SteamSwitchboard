namespace SteamSwitchboard.Models;

public sealed record InstalledGame(
    uint AppId,
    string Name,
    string InstallDirectory,
    string LibraryPath,
    long SizeOnDisk,
    DateTimeOffset? LastUpdatedUtc)
{
    public string SizeLabel => SizeOnDisk switch
    {
        >= 1_073_741_824 => $"{SizeOnDisk / 1_073_741_824d:0.#} GB",
        >= 1_048_576 => $"{SizeOnDisk / 1_048_576d:0.#} MB",
        > 0 => $"{SizeOnDisk / 1024d:0.#} KB",
        _ => "Installed"
    };
}
