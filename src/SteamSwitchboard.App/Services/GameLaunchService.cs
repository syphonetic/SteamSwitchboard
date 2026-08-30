using System.ComponentModel;
using System.Diagnostics;
using SteamSwitchboard.Models;

namespace SteamSwitchboard.Services;

public sealed class GameLaunchService
{
    private readonly SteamInstallationService _installationService;
    private readonly SteamClientAccountService _accountService;
    private readonly Func<string, bool> _steamProcessChecker;
    private readonly Action<ProcessStartInfo> _processStarter;
    private readonly object _launchGate = new();

    public GameLaunchService(
        SteamInstallationService installationService,
        SteamClientAccountService accountService,
        Func<string, bool>? steamProcessChecker = null,
        Action<ProcessStartInfo>? processStarter = null)
    {
        _installationService = installationService
            ?? throw new ArgumentNullException(nameof(installationService));
        _accountService = accountService
            ?? throw new ArgumentNullException(nameof(accountService));
        _steamProcessChecker = steamProcessChecker ?? IsSteamRunning;
        _processStarter = processStarter ?? StartProcess;
    }

    public LaunchAssessment Assess(
        AccountProfile? account,
        InstalledGame? game,
        string? configuredSteamPath) =>
        Evaluate(account, game, configuredSteamPath).Assessment;

    public LaunchAssessment LaunchIfReady(
        AccountProfile? account,
        InstalledGame? game,
        string? configuredSteamPath)
    {
        lock (_launchGate)
        {
            var first = Evaluate(account, game, configuredSteamPath);
            if (!first.Assessment.CanLaunch)
            {
                return first.Assessment;
            }

            var confirmed = Evaluate(account, game, configuredSteamPath);
            if (!confirmed.Assessment.CanLaunch
                || !string.Equals(
                    first.SteamExecutable,
                    confirmed.SteamExecutable,
                    StringComparison.OrdinalIgnoreCase))
            {
                return confirmed.Assessment.CanLaunch
                    ? new LaunchAssessment(
                        LaunchReadiness.ActiveAccountUnknown,
                        "Steam changed while the launch was being verified. Try again once Steam has settled.")
                    : confirmed.Assessment;
            }

            ArgumentNullException.ThrowIfNull(game);
            using var executableLock = TryLockVerifiedExecutable(
                confirmed.SteamExecutable!);
            if (executableLock is null)
            {
                return new LaunchAssessment(
                    LaunchReadiness.SteamNotFound,
                    "Steam changed before it could be started. Select the signed Valve steam.exe again.");
            }

            var final = Evaluate(account, game, configuredSteamPath);
            if (!final.Assessment.CanLaunch
                || !string.Equals(
                    confirmed.SteamExecutable,
                    final.SteamExecutable,
                    StringComparison.OrdinalIgnoreCase))
            {
                return final.Assessment.CanLaunch
                    ? new LaunchAssessment(
                        LaunchReadiness.ActiveAccountUnknown,
                        "Steam changed at the final launch check. Try again once Steam has settled.")
                    : final.Assessment;
            }

            var startInfo = new ProcessStartInfo(confirmed.SteamExecutable!)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(confirmed.SteamExecutable)
            };
            startInfo.ArgumentList.Add("-applaunch");
            startInfo.ArgumentList.Add(
                game.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _processStarter(startInfo);
            return final.Assessment;
        }
    }

    public void OpenSteam(string? configuredSteamPath)
    {
        var steamExecutable = _installationService.FindSteamExecutable(configuredSteamPath)
            ?? throw new FileNotFoundException(
                "A signed Valve Steam installation could not be found.");
        using var executableLock = TryLockVerifiedExecutable(steamExecutable)
            ?? throw new FileNotFoundException(
                "Steam changed while it was being verified. Select steam.exe again.");
        var startInfo = new ProcessStartInfo(steamExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(steamExecutable)
        };
        _processStarter(startInfo);
    }

    public static bool IsSteamRunning(string expectedExecutable)
    {
        if (!LocalPathPolicy.TryNormalizeLocalPath(
                expectedExecutable,
                out var expectedPath))
        {
            return false;
        }

        int currentSession;
        using (var currentProcess = Process.GetCurrentProcess())
        {
            currentSession = currentProcess.SessionId;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("steam");
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var imagePath = process.MainModule?.FileName;
                    if (process.SessionId == currentSession
                        && !string.IsNullOrWhiteSpace(imagePath)
                        && LocalPathPolicy.TryNormalizeLocalPath(
                            imagePath,
                            out var normalizedImage)
                        && string.Equals(
                            normalizedImage,
                            expectedPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (
                    exception is Win32Exception
                        or InvalidOperationException
                        or NotSupportedException)
                {
                    // An inaccessible or exiting process cannot prove Steam is active.
                }
            }
        }

        return false;
    }

    private LaunchContext Evaluate(
        AccountProfile? account,
        InstalledGame? game,
        string? configuredSteamPath)
    {
        var steamExecutable = _installationService.FindSteamExecutable(configuredSteamPath);
        var steamRoot = Path.GetDirectoryName(steamExecutable);
        var isRunning = !string.IsNullOrWhiteSpace(steamExecutable)
            && _steamProcessChecker(steamExecutable);
        var activeAccount = !string.IsNullOrWhiteSpace(steamRoot) && isRunning
            ? _accountService.FindActiveAccount(steamRoot)
            : null;

        return new LaunchContext(
            LaunchPolicy.Assess(
                account,
                game,
                steamExecutable,
                isRunning,
                activeAccount),
            steamExecutable);
    }

    private static void StartProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start Steam.");
    }

    private FileStream? TryLockVerifiedExecutable(string steamExecutable)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                steamExecutable,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.SequentialScan);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (_installationService.ValidateSteamExecutableCandidate(steamExecutable) is not null)
        {
            return stream;
        }

        stream.Dispose();
        return null;
    }

    private sealed record LaunchContext(
        LaunchAssessment Assessment,
        string? SteamExecutable);
}
