using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SteamSwitchboard.Models;
using SteamSwitchboard.Services;
using SteamSwitchboard.ViewModels;

namespace SteamSwitchboard.Controls;

public sealed partial class SteamChatSession : UserControl, IDisposable
{
    private const int MaximumNotificationsPerMinute = 30;
    private const int MaximumGlobalNotificationsPerMinute = 120;
    private const string SteamOrigin = "https://steamcommunity.com";
    private const string SafeBlankDocument =
        "<!doctype html><html><head><meta charset=\"utf-8\"></head><body></body></html>";

    private static readonly Uri ChatUri = new("https://steamcommunity.com/chat/");
    private static readonly Regex UnreadTitlePattern = new(
        @"^\((?<count>\d+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly object GlobalNotificationGate = new();
    private static readonly Queue<DateTimeOffset> GlobalNotificationTimes = [];
    private static DateTimeOffset _globalNotificationCircuitOpenUntil;

    private readonly AccountViewModel _account;
    private readonly string _browserDataFolder;
    private readonly HashSet<CoreWebView2Frame> _frames = [];
    private readonly DispatcherTimer _notificationFallbackTimer;
    private readonly Queue<DateTimeOffset> _notificationTimes = [];
    private readonly SemaphoreSlim _safeNavigationGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;
    private bool _eventsAttached;
    private bool _externalPromptOpen;
    private bool _keepConnectedWhenHidden = true;
    private bool _mediaTeardownInProgress;
    private bool _microphonePermissionRequested;
    private bool _securityClosed;
    private DateTimeOffset _lastNativeNotificationUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFallbackNotificationUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastExternalPromptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _notificationCircuitOpenUntil = DateTimeOffset.MinValue;

    public SteamChatSession(AccountViewModel account, string browserDataFolder)
    {
        InitializeComponent();
        _account = account ?? throw new ArgumentNullException(nameof(account));
        ArgumentException.ThrowIfNullOrWhiteSpace(browserDataFolder);
        _browserDataFolder = browserDataFolder;
        _notificationFallbackTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1.5),
            DispatcherPriority.Background,
            OnNotificationFallbackTick,
            Dispatcher)
        {
            IsEnabled = false
        };
        _account.PropertyChanged += OnAccountPropertyChanged;
        UpdateWorkspaceIdentity();
    }

    public AccountViewModel Account => _account;

    public event EventHandler<ChatNotificationEventArgs>? ChatNotificationReceived;

    public event EventHandler? ChatReadyWhileVisible;

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
            if (_disposed)
            {
                return;
            }

            await ResetPersistedPermissionsAsync(Browser.CoreWebView2.Profile);
            if (_disposed)
            {
                return;
            }

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

    public bool ShowChat()
    {
        _keepConnectedWhenHidden = true;
        Visibility = Visibility.Visible;
        if (_securityClosed)
        {
            return false;
        }

        _account.IsSleeping = false;
        ResumeIfSuspended();

        if (!_mediaTeardownInProgress
            && Browser.CoreWebView2 is { } core
            && Browser.Source is { } source
            && SteamNavigationPolicy.IsBootstrapDocument(source.AbsoluteUri))
        {
            core.Navigate(ChatUri.AbsoluteUri);
        }

        if (_account.ConnectionState == ChatConnectionState.Ready)
        {
            Browser.Focus();
            return true;
        }

        return false;
    }

    public void HideChat(bool keepConnected)
    {
        _keepConnectedWhenHidden = keepConnected;
        Visibility = Visibility.Collapsed;
        if (StopActiveMediaIfNeeded())
        {
            return;
        }

        if (keepConnected)
        {
            _account.IsSleeping = false;
            ResumeIfSuspended();
        }
        else
        {
            _ = SuspendWhenHiddenAsync();
        }
    }

    public async Task ClearSessionAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var core = Browser.CoreWebView2
            ?? throw new InvalidOperationException(
                "The account session could not be opened, so its local sign-in data was not removed. Restart Switchboard and try again.");

        Exception? clearFailure = null;
        if (_eventsAttached)
        {
            try
            {
                await NavigateToSafeBlankAsync(core);
                _microphonePermissionRequested = false;
            }
            catch (Exception exception) when (
                exception is COMException
                    or InvalidOperationException
                    or TimeoutException)
            {
                clearFailure = exception;
            }
        }

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
        _notificationFallbackTimer.Stop();
        _account.PropertyChanged -= OnAccountPropertyChanged;
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
        core.NotificationReceived += OnNotificationReceived;
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
        core.NotificationReceived -= OnNotificationReceived;
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
        if (_account.ConnectionState == ChatConnectionState.Ready
            && Visibility == Visibility.Visible)
        {
            ChatReadyWhileVisible?.Invoke(this, EventArgs.Empty);
        }
        if (!_keepConnectedWhenHidden && Visibility != Visibility.Visible)
        {
            _ = SuspendWhenHiddenAsync();
        }
    }

    private void OnDocumentTitleChanged(object? sender, object e)
    {
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        var match = UnreadTitlePattern.Match(
            Browser.CoreWebView2.DocumentTitle ?? string.Empty);
        if (IsWorkspaceVisible())
        {
            _notificationFallbackTimer.Stop();
            _account.UnreadCount = 0;
            return;
        }

        var previousUnread = _account.UnreadCount;
        _account.UnreadCount = match.Success
            && int.TryParse(match.Groups["count"].Value, out var count)
                ? count
                : 0;
        if (_account.UnreadCount > previousUnread)
        {
            _notificationFallbackTimer.Stop();
            _notificationFallbackTimer.Start();
        }
    }

    private void OnNotificationReceived(
        object? sender,
        CoreWebView2NotificationReceivedEventArgs e)
    {
        e.Handled = true;
        _notificationFallbackTimer.Stop();
        var notification = e.Notification;
        var replacementTag = notification.Tag;
        if (_disposed
            || !SteamNavigationPolicy.IsTrustedSteamOrigin(e.SenderOrigin)
            || !TryConsumeNotificationQuota()
            || !ChatNotificationPolicy.HasAcceptableRawSize(
                notification.Title,
                notification.Body,
                replacementTag)
            || !ChatNotificationPolicy.TryCreate(
                e.SenderOrigin,
                notification.Title,
                notification.Body,
                out var payload))
        {
            TryReportNotificationSuppressed(notification);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        payload = payload with
        {
            ReplacementTag = ChatNotificationPolicy.NormalizeReplacementTag(
                replacementTag),
            ReplacesUnreadFallback =
                now - _lastFallbackNotificationUtc < TimeSpan.FromSeconds(5)
        };
        var lifecycle = new WebNotificationLifecycle(notification);
        try
        {
            _lastNativeNotificationUtc = now;
            notification.ReportShown();
            var handler = ChatNotificationReceived;
            if (handler is null)
            {
                lifecycle.Close();
                return;
            }

            handler.Invoke(
                this,
                new ChatNotificationEventArgs(
                    _account,
                    payload,
                    lifecycle.ReportClickedAndClose,
                    lifecycle.Close));
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            // Web notification reporting is best effort; untrusted page data is never logged.
            lifecycle.Close();
        }
    }

    private void OnNotificationFallbackTick(object? sender, EventArgs e)
    {
        _notificationFallbackTimer.Stop();
        if (_disposed
            || _account.UnreadCount == 0
            || DateTimeOffset.UtcNow - _lastNativeNotificationUtc < TimeSpan.FromSeconds(3)
            || !TryConsumeNotificationQuota())
        {
            return;
        }

        _lastFallbackNotificationUtc = DateTimeOffset.UtcNow;
        ChatNotificationReceived?.Invoke(
            this,
            new ChatNotificationEventArgs(
                _account,
                new ChatNotificationPayload(
                    "Steam Chat",
                    "Open Switchboard to see the new message",
                    _lastFallbackNotificationUtc)
                {
                    IsUnreadFallback = true
                }));
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
        e.Handled = true;
        if (e.PermissionKind == CoreWebView2PermissionKind.Notifications
            && SteamNavigationPolicy.IsTrustedSteamOrigin(e.Uri))
        {
            e.State = CoreWebView2PermissionState.Allow;
            return;
        }

        var canUseMicrophone = e.PermissionKind == CoreWebView2PermissionKind.Microphone
            && SteamNavigationPolicy.CanRequestMicrophone(
                e.Uri,
                e.IsUserInitiated,
                IsWorkspaceVisible());
        if (canUseMicrophone)
        {
            _microphonePermissionRequested = true;
        }

        e.State = canUseMicrophone
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
        if (_securityClosed)
        {
            ShowFailure(
                "This browser was closed to protect microphone privacy. Restart Switchboard to reopen it.");
            return;
        }

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
        if (core is null || _disposed)
        {
            return;
        }

        if (core.IsSuspended)
        {
            _account.IsSleeping = true;
            return;
        }

        try
        {
            _account.IsSleeping = await core.TrySuspendAsync();
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            _account.IsSleeping = false;
            // Suspension is a best-effort reduction of background browser activity.
        }
    }

    private void ResumeIfSuspended()
    {
        try
        {
            if (Browser.CoreWebView2 is { IsSuspended: true } core)
            {
                core.Resume();
            }

            _account.IsSleeping = false;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            // A failed resume will be surfaced by navigation/process failure handling.
        }
    }

    private static void TryReportNotificationClosed(CoreWebView2Notification notification)
    {
        try
        {
            notification.ReportClosed();
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            // The originating document may already have closed.
        }
    }

    private static void TryReportNotificationSuppressed(
        CoreWebView2Notification notification)
    {
        try
        {
            notification.ReportShown();
            notification.ReportClosed();
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            // Suppression is already guaranteed by the handled event flag.
        }
    }

    private bool TryConsumeNotificationQuota()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _notificationCircuitOpenUntil)
        {
            return false;
        }

        var cutoff = now - TimeSpan.FromMinutes(1);
        while (_notificationTimes.TryPeek(out var timestamp) && timestamp < cutoff)
        {
            _ = _notificationTimes.Dequeue();
        }

        if (_notificationTimes.Count >= MaximumNotificationsPerMinute)
        {
            _notificationCircuitOpenUntil = now + TimeSpan.FromSeconds(30);
            return false;
        }

        lock (GlobalNotificationGate)
        {
            if (now < _globalNotificationCircuitOpenUntil)
            {
                return false;
            }

            while (GlobalNotificationTimes.TryPeek(out var timestamp)
                   && timestamp < cutoff)
            {
                _ = GlobalNotificationTimes.Dequeue();
            }

            if (GlobalNotificationTimes.Count
                >= MaximumGlobalNotificationsPerMinute)
            {
                _globalNotificationCircuitOpenUntil =
                    now + TimeSpan.FromSeconds(30);
                return false;
            }

            GlobalNotificationTimes.Enqueue(now);
        }

        _notificationTimes.Enqueue(now);
        return true;
    }

    private bool StopActiveMediaIfNeeded()
    {
        if (!_microphonePermissionRequested
            || Browser.CoreWebView2 is not { } core
            || _disposed
            || _mediaTeardownInProgress)
        {
            return false;
        }

        _mediaTeardownInProgress = true;
        _ = EndActiveMediaDocumentAsync(core);
        return true;
    }

    private async Task EndActiveMediaDocumentAsync(CoreWebView2 core)
    {
        try
        {
            await NavigateToSafeBlankAsync(core);
            if (_disposed || _securityClosed)
            {
                return;
            }

            _microphonePermissionRequested = false;
            if (_keepConnectedWhenHidden || Visibility == Visibility.Visible)
            {
                core.Navigate(ChatUri.AbsoluteUri);
            }
            else
            {
                await SuspendWhenHiddenAsync();
            }
        }
        catch (Exception exception) when (
            exception is COMException
                or InvalidOperationException
                or TimeoutException)
        {
            if (!_disposed)
            {
                CloseBrowserForMediaSafety();
            }
        }
        finally
        {
            _mediaTeardownInProgress = false;
        }
    }

    private async Task NavigateToSafeBlankAsync(CoreWebView2 core)
    {
        await _safeNavigationGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CoreWebView2NavigationCompletedEventArgs>? completed = null;
            completed = (_, args) => completion.TrySetResult(args.IsSuccess);
            core.NavigationCompleted += completed;
            try
            {
                core.NavigateToString(SafeBlankDocument);
                var succeeded = await completion.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));
                if (!succeeded
                    || Browser.Source is not { } source
                    || !SteamNavigationPolicy.IsBootstrapDocument(source.AbsoluteUri))
                {
                    throw new InvalidOperationException(
                        "The browser did not commit its local privacy reset page.");
                }
            }
            finally
            {
                core.NavigationCompleted -= completed;
            }
        }
        finally
        {
            _safeNavigationGate.Release();
        }
    }

    private void CloseBrowserForMediaSafety()
    {
        _securityClosed = true;
        _notificationFallbackTimer.Stop();
        if (_eventsAttached && Browser.CoreWebView2 is { } core)
        {
            DetachEvents(core);
            _eventsAttached = false;
        }

        Browser.Dispose();
        ShowFailure(
            "This workspace was closed because its microphone session could not be ended safely. Restart Switchboard to reconnect.");
    }

    private static async Task ResetPersistedPermissionsAsync(
        CoreWebView2Profile profile)
    {
        var persistedPermissions = await profile
            .GetNonDefaultPermissionSettingsAsync();
        foreach (var permission in persistedPermissions)
        {
            await profile.SetPermissionStateAsync(
                permission.PermissionKind,
                permission.PermissionOrigin,
                CoreWebView2PermissionState.Default);
        }

        await profile.SetPermissionStateAsync(
            CoreWebView2PermissionKind.Microphone,
            SteamOrigin,
            CoreWebView2PermissionState.Default);
        await profile.SetPermissionStateAsync(
            CoreWebView2PermissionKind.Camera,
            SteamOrigin,
            CoreWebView2PermissionState.Default);
    }

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountViewModel.DisplayName)
            or nameof(AccountViewModel.SteamLoginName))
        {
            UpdateWorkspaceIdentity();
        }
    }

    private void UpdateWorkspaceIdentity()
    {
        WorkspaceIdentityText.Text =
            $"Workspace label: {_account.DisplayName}  •  expected Steam account: {_account.SteamLoginName}";
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

    private sealed class WebNotificationLifecycle
    {
        private CoreWebView2Notification? _notification;

        public WebNotificationLifecycle(CoreWebView2Notification notification)
        {
            _notification = notification
                ?? throw new ArgumentNullException(nameof(notification));
        }

        public void ReportClickedAndClose()
        {
            var notification = Volatile.Read(ref _notification);
            if (notification is null)
            {
                return;
            }

            try
            {
                notification.ReportClicked();
            }
            catch (Exception exception) when (
                exception is COMException or InvalidOperationException)
            {
                // The document may have closed before the host click arrived.
            }

            Close();
        }

        public void Close()
        {
            var notification = Interlocked.Exchange(ref _notification, null);
            if (notification is not null)
            {
                TryReportNotificationClosed(notification);
            }
        }
    }
}

public sealed class ChatNotificationEventArgs : EventArgs
{
    public ChatNotificationEventArgs(
        AccountViewModel account,
        ChatNotificationPayload notification,
        Action? reportClicked = null,
        Action? reportClosed = null)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));
        Notification = notification
            ?? throw new ArgumentNullException(nameof(notification));
        ReportClicked = reportClicked;
        ReportClosed = reportClosed;
    }

    public AccountViewModel Account { get; }

    public ChatNotificationPayload Notification { get; }

    public Action? ReportClicked { get; }

    public Action? ReportClosed { get; }
}
