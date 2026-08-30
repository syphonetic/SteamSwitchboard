using System.Globalization;
using SteamSwitchboard.Models;

namespace SteamSwitchboard.Services;

public sealed class SteamLibraryService
{
    private const int MaximumLibraries = 256;
    private const int MaximumManifestsPerLibrary = 100_000;
    private const int MaximumGameNameCharacters = 200;

    public Task<IReadOnlyList<InstalledGame>> LoadInstalledGamesAsync(
        string steamRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamRoot);
        return Task.Run<IReadOnlyList<InstalledGame>>(
            () => LoadInstalledGames(steamRoot, cancellationToken),
            cancellationToken);
    }

    public IReadOnlyList<string> FindLibraryFolders(string steamRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamRoot);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!AddLibraryIfUsable(folders, steamRoot))
        {
            return [];
        }

        var normalizedSteamRoot = folders.Single();

        var libraryCandidate = Path.Combine(
            normalizedSteamRoot,
            "steamapps",
            "libraryfolders.vdf");
        if (!LocalPathPolicy.TryNormalizeLocalPath(
                libraryCandidate,
                out var libraryFile))
        {
            return folders.ToArray();
        }

        try
        {
            var document = VdfParser.ParseFile(libraryFile);
            var root = document.Get("libraryfolders") ?? document;
            foreach (var entry in root.Children.Values.Take(MaximumLibraries))
            {
                var path = entry.Value ?? entry.GetValue("path");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    AddLibraryIfUsable(folders, path);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or System.Security.SecurityException)
        {
            // The primary library remains usable even if Steam is updating this file.
        }

        return folders.ToArray();
    }

    private IReadOnlyList<InstalledGame> LoadInstalledGames(
        string steamRoot,
        CancellationToken cancellationToken)
    {
        var games = new Dictionary<uint, InstalledGame>();

        foreach (var library in FindLibraryFolders(steamRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var steamApps = Path.Combine(library, "steamapps");
            var libraryGames = new List<InstalledGame>();
            var manifestLimitExceeded = false;

            try
            {
                var manifestCount = 0;
                foreach (var manifest in Directory.EnumerateFiles(
                             steamApps,
                             "appmanifest_*.acf",
                             SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    manifestCount++;
                    if (manifestCount > MaximumManifestsPerLibrary)
                    {
                        manifestLimitExceeded = true;
                        break;
                    }

                    var game = TryReadManifest(
                        manifest,
                        library,
                        cancellationToken);
                    if (game is not null)
                    {
                        libraryGames.Add(game);
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                continue;
            }

            if (manifestLimitExceeded)
            {
                continue;
            }

            foreach (var game in libraryGames)
            {
                games[game.AppId] = game;
            }
        }

        return games.Values
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static InstalledGame? TryReadManifest(
        string manifestPath,
        string libraryPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!LocalPathPolicy.TryNormalizeLocalPath(
                    manifestPath,
                    out var normalizedManifest)
                || LocalPathPolicy.IsReparsePoint(normalizedManifest)
                || !TryGetManifestAppId(normalizedManifest, out var manifestAppId))
            {
                return null;
            }

            var document = VdfParser.ParseFile(normalizedManifest, cancellationToken);
            var state = document.Get("AppState") ?? document.Get("appstate");
            if (state is null
                || !uint.TryParse(
                    state.GetValue("appid"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var appId)
                || appId == 0
                || appId != manifestAppId)
            {
                return null;
            }

            var name = SafeText.SanitizeDisplayText(
                state.GetValue("name"),
                $"App {appId}",
                MaximumGameNameCharacters);

            var installFolderName = state.GetValue("installdir")?.Trim() ?? string.Empty;
            if (!IsSafeInstallFolderName(installFolderName))
            {
                return null;
            }

            var commonDirectory = Path.Combine(
                libraryPath,
                "steamapps",
                "common");
            var installCandidate = Path.Combine(commonDirectory, installFolderName);
            if (!LocalPathPolicy.TryNormalizeLocalPath(
                    commonDirectory,
                    out var normalizedCommon)
                || !LocalPathPolicy.TryNormalizeLocalPath(
                    installCandidate,
                    out var installDirectory)
                || !LocalPathPolicy.IsStrictDescendant(
                    installDirectory,
                    normalizedCommon))
            {
                return null;
            }

            _ = long.TryParse(
                state.GetValue("SizeOnDisk"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sizeOnDisk);
            sizeOnDisk = Math.Max(0, sizeOnDisk);
            DateTimeOffset? updated = null;
            if (long.TryParse(
                    state.GetValue("LastUpdated"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var timestamp)
                && timestamp > 0)
            {
                try
                {
                    updated = DateTimeOffset.FromUnixTimeSeconds(timestamp);
                }
                catch (ArgumentOutOfRangeException)
                {
                    updated = null;
                }
            }

            return new InstalledGame(
                appId,
                name,
                installDirectory,
                libraryPath,
                sizeOnDisk,
                updated);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or ArgumentException
                or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool AddLibraryIfUsable(ISet<string> folders, string candidate)
    {
        if (!LocalPathPolicy.TryNormalizeLocalPath(candidate, out var normalized)
            || !LocalPathPolicy.TryNormalizeLocalPath(
                Path.Combine(normalized, "steamapps"),
                out _))
        {
            return false;
        }

        folders.Add(normalized);
        return true;
    }

    private static bool TryGetManifestAppId(string manifestPath, out uint appId)
    {
        const string prefix = "appmanifest_";
        appId = 0;
        var name = Path.GetFileNameWithoutExtension(manifestPath);
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(
                name.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out appId)
            && appId != 0;
    }

    private static bool IsSafeInstallFolderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 240
            || value is "." or ".."
            || Path.IsPathRooted(value)
            || value.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || SafeText.ContainsUnsafeIdentityCharacters(value))
        {
            return false;
        }

        var stem = value.TrimEnd('.', ' ');
        return stem.Length > 0
            && !IsReservedDeviceName(stem);
    }

    private static bool IsReservedDeviceName(string value)
    {
        var stem = value.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem[3] is >= '1' and <= '9');
    }
}
