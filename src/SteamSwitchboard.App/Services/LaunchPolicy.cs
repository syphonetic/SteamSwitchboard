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
                "Choose an account before starting a game.");
        }

        if (game is null || !Directory.Exists(game.InstallDirectory))
        {
            return new LaunchAssessment(
                LaunchReadiness.GameNotInstalled,
                "Steam no longer reports this game as installed.");
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
                $"Start Steam and sign in as {selectedAccount.DisplayName}.");
        }

        if (activeAccount is null)
        {
            return new LaunchAssessment(
                LaunchReadiness.ActiveAccountUnknown,
                "Steam is running, but its active account could not be verified. Finish signing in or restart Steam; the game will stay blocked until verification succeeds.");
        }

        if (!string.Equals(
                activeAccount.AccountName,
                selectedAccount.SteamLoginName,
                StringComparison.OrdinalIgnoreCase))
        {
            return new LaunchAssessment(
                LaunchReadiness.AccountSwitchRequired,
                $"Steam is currently using {activeAccount.PersonaName}. Switch Steam to {selectedAccount.DisplayName}; Switchboard will verify it before launching.",
                activeAccount);
        }

        return new LaunchAssessment(
            LaunchReadiness.Ready,
            $"Ready to play as {selectedAccount.DisplayName}.",
            activeAccount);
    }
}
