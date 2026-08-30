using Microsoft.Win32;

namespace SteamSwitchboard.Services;

public sealed class SteamInstallationService
{
    private static readonly string[] RegistryPaths =
    [
        @"HKEY_CURRENT_USER\Software\Valve\Steam",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"
    ];

    private readonly Func<string, bool> _executableTrustVerifier;
    private readonly Func<string?, IEnumerable<string>> _candidateProvider;

    public SteamInstallationService(
        Func<string, bool>? executableTrustVerifier = null,
        Func<string?, IEnumerable<string>>? candidateProvider = null)
    {
        _executableTrustVerifier = executableTrustVerifier
            ?? SteamExecutableTrust.IsTrustedValveExecutable;
        _candidateProvider = candidateProvider ?? EnumerateCandidates;
    }

    public string? FindSteamExecutable(string? configuredPath = null)
    {
        foreach (var candidate in _candidateProvider(configuredPath))
        {
            var normalized = ValidateSteamExecutableCandidate(candidate);
            if (normalized is not null)
            {
                return normalized;
            }
        }

        return null;
    }

    public string? ValidateSteamExecutableCandidate(string? candidate)
    {
        if (!LocalPathPolicy.TryNormalizeLocalPath(candidate, out var normalized)
            || !string.Equals(
                Path.GetFileName(normalized),
                "steam.exe",
                StringComparison.OrdinalIgnoreCase)
            || !_executableTrustVerifier(normalized))
        {
            return null;
        }

        return normalized;
    }

    public string? FindSteamRoot(string? configuredPath = null) =>
        Path.GetDirectoryName(FindSteamExecutable(configuredPath));

    private static IEnumerable<string> EnumerateCandidates(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return configuredPath;
        }

        foreach (var registryPath in RegistryPaths)
        {
            var steamExe = TryReadRegistryString(registryPath, "SteamExe");
            if (!string.IsNullOrWhiteSpace(steamExe))
            {
                yield return steamExe;
            }

            var installPath = TryReadRegistryString(registryPath, "InstallPath")
                ?? TryReadRegistryString(registryPath, "SteamPath");
            if (!string.IsNullOrWhiteSpace(installPath))
            {
                yield return Path.Combine(installPath, "steam.exe");
            }
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "Steam", "steam.exe");
        }
    }

    private static string? TryReadRegistryString(string key, string valueName)
    {
        try
        {
            return Registry.GetValue(key, valueName, null) as string;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return null;
        }
    }
}
