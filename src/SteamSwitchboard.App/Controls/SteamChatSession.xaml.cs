using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SteamSwitchboard.Services;
using SteamSwitchboard.ViewModels;

namespace SteamSwitchboard.Controls;

public sealed partial class SteamChatSession : UserControl, IDisposable
{
    private static readonly Uri ChatUri = new("https://steamcommunity.com/chat/");
    private static readonly Regex UnreadTitlePattern = new(
        @"^\((?<count>\d+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly AccountViewModel _account;
    private readonly string _browserDataFolder;
    private readonly HashSet<CoreWebView2Frame> _frames = [];
    private bool _initialized;
    private bool _disposed;
    private bool _eventsAttached;
    private bool _externalPromptOpen;
    private DateTimeOffset _lastExternalPromptUtc = DateTimeOffset.MinValue;

    public SteamChatSession(AccountViewModel account, string browserDataFolder)
    {
        InitializeComponent();
        _account = account ?? throw new ArgumentNullException(nameof(account));
        ArgumentException.ThrowIfNullOrWhiteSpace(browserDataFolder);
        _browserDataFolder = browserDataFolder;
        WorkspaceIdentityText.Text =
            $"Workspace: {_account.DisplayName}  •  expected login: {_account.SteamLoginName}";
    }

    public AccountViewModel Account => _account;

    public Task InitializeAsync() => InitializeCoreAsync(navigateToChat: true);

    public Task InitializeForCleanupAsync() => InitializeCoreAsync(navigateToChat: false);

    private async Task InitializeCoreAsync(bool navigateToChat)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _account.ConnectionState = ChatConnectionState.Starting;
        if (!LocalPathPolicy.TryNormalizeLocalPath(
                _browserDataFolder,
                out _,
                requireExisting: false))
        {
            ShowFailure("The secure browser data folder is not a safe local path.");
            return;
        }

        Directory.CreateDirectory(_browserDataFolder);
        if (!LocalPathPolicy.TryNormalizeLocalPath(_browserDataFolder, out _))
        {
            ShowFailure("The secure browser data folder cannot use links or remote storage.");
            return;
        }

        try
        {
            Browser.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = _browserDataFolder,
                ProfileName = _account.Profile.BrowserProfileName,
                IsInPrivateModeEnabled = false
            };

            await Browser.EnsureCoreWebView2Async();
            ConfigureSecurity(Browser.CoreWebView2);
            if (navigateToChat)
            {
                AttachEvents(Browser.CoreWebView2);
                _eventsAttached = true;
                Browser.CoreWebView2.Navigate(ChatUri.AbsoluteUri);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or ArgumentException
                or COMException)
        {
            ShowFailure("The secure browser could not start. Install or repair Microsoft Edge WebView2, then reconnect.");
        }
    }

    public void ShowChat()
    {
        Visibility = Visibility.Visible;
        if (Browser.CoreWebView2 is { IsSuspended: true } core)
        {
            core.Resume();
        }

        if (_account.ConnectionState == ChatConnectionState.Ready)
        {
            Browser.Focus();
        }
    }

    public void HideChat()
    {
        Visibility = Visibility.Collapsed;
        _ = SuspendWhenHiddenAsync();
    }

    public async Task ClearSessionAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var core = Browser.CoreWebView2
            ?? throw new InvalidOperationException(
                "The account session could not be opened, so its local sign-in data was not removed. Restart Switchboard and try again.");

        Exception? clearFailure = null;
        try
        {
            await core.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.AllProfile);
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            clearFailure = exception;
        }

        try
        {
            core.Profile.Delete();
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "The account's local sign-in data could not be scheduled for deletion. The account remains in Switchboard so the cleanup can be retried.",
                clearFailure ?? exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_eventsAttached && Browser.CoreWebView2 is not null)
        {
            DetachEvents(Browser.CoreWebView2);
            _eventsAttached = false;
        }

        Browser.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void ConfigureSecurity(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;
        core.Settings.IsReputationCheckingRequired = true;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Profile.IsPasswordAutosaveEnabled = false;
        core.Profile.IsGeneralAutofillEnabled = false;
    }

    private void AttachEvents(CoreWebView2 core)
    {
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.ProcessFailed += OnProcessFailed;
        core.FrameCreated += OnFrameCreated;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        core.BasicAuthenticationRequested += OnBasicAuthenticationRequested;
        core.ClientCertificateRequested += OnClientCertificateRequested;
        core.ScreenCaptureStarting += OnScreenCaptureStarting;
        core.ServerCertificateErrorDetected += OnServerCertificateErrorDetected;
        core.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += OnWebResourceRequested;
    }

    private void DetachEvents(CoreWebView2 core)
    {
        core.NavigationStarting -= OnNavigationStarting;
        core.NavigationCompleted -= OnNavigationCompleted;
        core.DocumentTitleChanged -= OnDocumentTitleChanged;
        core.NewWindowRequested -= OnNewWindowRequested;
        core.PermissionRequested -= OnPermissionRequested;
        core.DownloadStarting -= OnDownloadStarting;
        core.ProcessFailed -= OnProcessFailed;
        core.FrameCreated -= OnFrameCreated;
        core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
        core.BasicAuthenticationRequested -= OnBasicAuthenticationRequested;
        core.ClientCertificateRequested -= OnClientCertificateRequested;
        core.ScreenCaptureStarting -= OnScreenCaptureStarting;
        core.ServerCertificateErrorDetected -= OnServerCertificateErrorDetected;
        core.WebResourceRequested -= OnWebResourceRequested;
        core.RemoveWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.Document);

        foreach (var frame in _frames.ToArray())
        {
            DetachFrame(frame);
        }
    }

    private void OnNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!SteamNavigationPolicy.IsAllowedEmbeddedDocument(e.Uri))
        {
            e.Cancel = true;
            if (SteamNavigationPolicy.ShouldPromptForExternalLink(
                    e.Uri,
                    e.IsUserInitiated))
            {
                OpenExternalWithConfirmation(e.Uri);
            }

            return;
        }

        if (SteamNavigationPolicy.IsBootstrapDocument(e.Uri))
        {
            return;
        }

        _account.ConnectionState = ChatConnectionState.Reconnecting;
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (Browser.Source is not { } source
            || !SteamNavigationPolicy.IsAllowedEmbeddedDocument(source.AbsoluteUri))
        {
            ShowFailure("The Steam page was blocked or could not load. Check your connection and reconnect.");
            return;
        }

        if (SteamNavigationPolicy.IsBootstrapDocument(source.AbsoluteUri))
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            _account.ConnectionState = ChatConnectionState.Starting;
            return;
        }
        if (!e.IsSuccess)
        {
            ShowFailure("The Steam page was blocked or could not load. Check your connection and reconnect.");
            return;
        }

        LoadingOverlay.Visibility = Visibility.Collapsed;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        _account.ConnectionState = SteamNavigationPolicy.IsLoginDocument(source)
            ? ChatConnectionState.SignInRequired
            : ChatConnectionState.Ready;
    }

    private void OnDocumentTitleChanged(object? sender, object e)
    {
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        var match = UnreadTitlePattern.Match(
            Browser.CoreWebView2.DocumentTitle ?? string.Empty);
        _account.UnreadCount = match.Success
            && int.TryParse(match.Groups["count"].Value, out var count)
                ? count
                : 0;
    }

    private void OnNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!e.IsUserInitiated)
        {
            return;
        }

        if (SteamNavigationPolicy.IsAllowedEmbeddedDocument(e.Uri))
        {
            Browser.CoreWebView2.Navigate(e.Uri);
        }
        else if (SteamNavigationPolicy.ShouldPromptForExternalLink(
                     e.Uri,
                     e.IsUserInitiated))
        {
            OpenExternalWithConfirmation(e.Uri);
        }
    }

    private void OnPermissionRequested(
        object? sender,
        CoreWebView2PermissionRequestedEventArgs e)
    {
        e.SavesInProfile = false;
        e.State = e.PermissionKind == CoreWebView2PermissionKind.Microphone
            && SteamNavigationPolicy.CanRequestMicrophone(
                e.Uri,
                e.IsUserInitiated,
                IsWorkspaceVisible())
                ? CoreWebView2PermissionState.Default
                : CoreWebView2PermissionState.Deny;
    }

    private void OnFrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs e) =>
        AttachFrame(e.Frame);

    private void AttachFrame(CoreWebView2Frame frame)
    {
        if (!_frames.Add(frame))
        {
            return;
        }

        frame.NavigationStarting += OnFrameNavigationStarting;
        frame.PermissionRequested += OnPermissionRequested;
        frame.ScreenCaptureStarting += OnScreenCaptureStarting;
        frame.FrameCreated += OnFrameCreated;
        frame.Destroyed += OnFrameDestroyed;
    }

    private void DetachFrame(CoreWebView2Frame frame)
    {
        frame.NavigationStarting -= OnFrameNavigationStarting;
        frame.PermissionRequested -= OnPermissionRequested;
        frame.ScreenCaptureStarting -= OnScreenCaptureStarting;
        frame.FrameCreated -= OnFrameCreated;
        frame.Destroyed -= OnFrameDestroyed;
        _frames.Remove(frame);
    }

    private static void OnFrameNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!SteamNavigationPolicy.IsAllowedEmbeddedDocument(e.Uri))
        {
            e.Cancel = true;
        }
    }

    private void OnFrameDestroyed(object? sender, object e)
    {
        if (sender is CoreWebView2Frame frame)
        {
            DetachFrame(frame);
        }
    }

    private void OnWebResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (SteamNavigationPolicy.IsAllowedEmbeddedDocument(e.Request.Uri)
            || sender is not CoreWebView2 core)
        {
            return;
        }

        e.Response = core.Environment.CreateWebResourceResponse(
            Stream.Null,
            403,
            "Blocked",
            "Content-Type: text/plain\r\nCache-Control: no-store");
    }

    private static void OnLaunchingExternalUriScheme(
        object? sender,
        CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true;
    }

    private static void OnBasicAuthenticationRequested(
        object? sender,
        CoreWebView2BasicAuthenticationRequestedEventArgs e)
    {
        e.Cancel = true;
    }

    private static void OnClientCertificateRequested(
        object? sender,
        CoreWebView2ClientCertificateRequestedEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
    }

    private static void OnScreenCaptureStarting(
        object? sender,
        CoreWebView2ScreenCaptureStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
    }

    private static void OnServerCertificateErrorDetected(
        object? sender,
        CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
    }

    private static void OnDownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        ShowFailure("The Steam Chat browser stopped unexpectedly. Reconnect to continue.");
    }

    private async void OnReconnectClicked(object sender, RoutedEventArgs e)
    {
        ErrorOverlay.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;
        _account.ConnectionState = ChatConnectionState.Reconnecting;

        try
        {
            if (Browser.CoreWebView2 is null)
            {
                _initialized = false;
                await InitializeAsync();
            }
            else
            {
                Browser.CoreWebView2.Navigate(ChatUri.AbsoluteUri);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or COMException)
        {
            ShowFailure("The secure browser is still unavailable. Restart Switchboard and try again.");
        }
    }

    private async Task SuspendWhenHiddenAsync()
    {
        var core = Browser.CoreWebView2;
        if (core is null || core.IsSuspended || _disposed)
        {
            return;
        }

        try
        {
            _ = await core.TrySuspendAsync();
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            // Suspension is a best-effort reduction of background browser activity.
        }
    }

    private bool IsWorkspaceVisible() =>
        IsVisible
        && Visibility == Visibility.Visible
        && Window.GetWindow(this)?.IsActive == true;

    private void ShowFailure(string message)
    {
        _account.ConnectionState = ChatConnectionState.Failed;
        LoadingOverlay.Visibility = Visibility.Collapsed;
        ErrorMessage.Text = message;
        ErrorOverlay.Visibility = Visibility.Visible;
    }

    private void OpenExternalWithConfirmation(string? rawUri)
    {
        var uri = SteamNavigationPolicy.GetSafeExternalUri(rawUri);
        var now = DateTimeOffset.UtcNow;
        if (uri is null
            || _externalPromptOpen
            || now - _lastExternalPromptUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _externalPromptOpen = true;
        _lastExternalPromptUtc = now;
        try
        {
            var result = MessageBox.Show(
                $"This link leaves Steam and will open in your default browser.\n\n{uri.AbsoluteUri}\n\nOpen it?",
                "Open external link",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                using var process = Process.Start(
                    new ProcessStartInfo(uri.AbsoluteUri)
                    {
                        UseShellExecute = true
                    });
            }
            catch (Exception exception) when (
                exception is Win32Exception or InvalidOperationException)
            {
                MessageBox.Show(
                    "Windows could not open that link.",
                    "Link could not be opened",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            _externalPromptOpen = false;
        }
    }
}
