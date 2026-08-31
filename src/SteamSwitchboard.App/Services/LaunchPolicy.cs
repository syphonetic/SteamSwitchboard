using SteamSwitchboard.Models;

namespace SteamSwitchboard.Services;

public static class LaunchPolicy
{
    public static LaunchAssessment Assess(
        AccountProfile? selectedAccount,
        InstalledGame? game,
        string? steamExecutable,
        bool steamIsRunning,
        SteamClientAccount? activeAccount)
    {
        if (selectedAccount is null || string.IsNullOrWhiteSpace(selectedAccount.SteamLoginName))
        {
            return new LaunchAssessment(
                LaunchReadiness.InvalidAccount,
                "Choose an account before starting a library item.");
        }

        if (game is null || !Directory.Exists(game.InstallDirectory))
        {
            return new LaunchAssessment(
                LaunchReadiness.GameNotInstalled,
                "This application's local install folder is missing. Refresh the library and let Steam finish any install or update first.");
        }

        if (string.IsNullOrWhiteSpace(steamExecutable) || !File.Exists(steamExecutable))
        {
            return new LaunchAssessment(
                LaunchReadiness.SteamNotFound,
                "Steam could not be found. Choose steam.exe in Settings.");
        }

        if (!steamIsRunning)
        {
            return new LaunchAssessment(
                LaunchReadiness.SteamNotRunning,
                $"Start Steam and sign in to Steam login “{selectedAccount.SteamLoginName}”.");
        }

        if (activeAccount is null)
        {
            return new LaunchAssessment(
                LaunchReadiness.ActiveAccountUnknown,
                "Steam is running, but its active account could not be verified. Finish signing in or restart Steam; the launch will stay blocked until verification succeeds.");
        }

        if (!string.Equals(
                activeAccount.AccountName,
                selectedAccount.SteamLoginName,
                StringComparison.OrdinalIgnoreCase))
        {
            return new LaunchAssessment(
                LaunchReadiness.AccountSwitchRequired,
                $"Steam is currently using login “{activeAccount.AccountName}”. Switch Steam to login “{selectedAccount.SteamLoginName}”; Switchboard will verify it before launching.",
                activeAccount);
        }

        return new LaunchAssessment(
            LaunchReadiness.Ready,
            $"Steam login “{selectedAccount.SteamLoginName}” verified. Ready to launch.",
            activeAccount);
    }
}
