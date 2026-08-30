using System.IO;
using System.Windows;
using System.Windows.Threading;
using SteamSwitchboard.Services;
using SteamSwitchboard.ViewModels;

namespace SteamSwitchboard;

public partial class App : Application
{
    private AppPaths? _paths;
    private SingleInstanceGuard? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        if (ProcessSecurity.IsElevated())
        {
            MessageBox.Show(
                "SteamSwitchboard is designed to run with normal Windows permissions. Close it and start it normally, without ‘Run as administrator’.",
                "Normal permissions required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        _singleInstance = SingleInstanceGuard.TryAcquire("SteamSwitchboard");
        if (_singleInstance is null)
        {
            MessageBox.Show(
                "SteamSwitchboard is already open.",
                "SteamSwitchboard",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            _paths = new AppPaths();
            _paths.EnsureCreated();
            var store = new StateStore(_paths.StateFile);
            var installation = new SteamInstallationService();
            var library = new SteamLibraryService();
            var accounts = new SteamClientAccountService();
            var launcher = new GameLaunchService(installation, accounts);
            var viewModel = new MainViewModel(store, installation, library);

            MainWindow = new MainWindow(viewModel, _paths, launcher);
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            TryWriteStartupDiagnostic(exception);
            MessageBox.Show(
                $"Switchboard could not start.\n\n{exception.Message}",
                "SteamSwitchboard",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }

    private void TryWriteStartupDiagnostic(Exception exception)
    {
        try
        {
            var logRoot = _paths?.Logs
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SteamSwitchboard",
                    "Logs");
            SafeDiagnosticLog.WriteSingleRecord(
                Path.Combine(logRoot, "startup.log"),
                exception);
        }
        catch (Exception writeException) when (
            writeException is IOException or UnauthorizedAccessException)
        {
            // Startup diagnostics are best effort only.
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        TryWriteSafeCrashRecord(e.Exception);
        MessageBox.Show(
            "Something unexpected happened. Your accounts and Steam credentials were not changed. "
            + "Restart Switchboard; a privacy-safe diagnostic was saved locally.",
            "SteamSwitchboard needs to restart",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private void TryWriteSafeCrashRecord(Exception exception)
    {
        if (_paths is null)
        {
            return;
        }

        try
        {
            SafeDiagnosticLog.AppendBoundedRecord(
                Path.Combine(_paths.Logs, "crashes.log"),
                exception);
        }
        catch (Exception writeException) when (
            writeException is IOException or UnauthorizedAccessException)
        {
            // A diagnostic must never prevent shutdown.
        }
    }
}
