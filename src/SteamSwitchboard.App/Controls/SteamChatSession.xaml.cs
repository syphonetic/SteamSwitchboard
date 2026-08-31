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
    private static readonly TimeSpan DefaultBrowserInitializationTimeout =
        TimeSpan.FromSeconds(15);

    private static readonly Uri ChatUri = new("https://steamcommunity.com/chat/");
    private static readonly Regex UnreadTitlePattern = new(
        @"^\((?<count>\d+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly object GlobalNotificationGate = new();
    private static readonly Queue<DateTimeOffset> GlobalNotificationTimes = [];
    private static DateTimeOffset _globalNotificationCircuitOpenUntil;

    private readonly AccountViewModel _account;
    private readonly Func<WebView2, Task> _browserInitializer;
    private readonly string _browserDataFolder;
    private readonly TimeSpan _browserInitializationTimeout;
    private readonly HashSet<CoreWebView2Frame> _frames = [];
    private readonly BrowserNavigationTracker _navigationTracker = new();
    private readonly DispatcherTimer _notificationFallbackTimer;
    private readonly Queue<DateTimeOffset> _notificationTimes = [];
    private readonly SemaphoreSlim _safeNavigationGate = new(1, 1);
    private Task? _browserInitializationTask;
    private Task? _initializationAttempt;
    private Task? _permissionResetTask;
    private bool _configured;
    private bool _disposed;
    private bool _eventsAttached;
    private bool _externalPromptOpen;
    private bool _initializationFailed;
    private bool _isPresentedToUser;
    private bool _keepConnectedWhenHidden = true;
    private bool _mediaTeardownInProgress;
    private bool _microphonePermissionRequested;
    private bool _securityClosed;
    private long _presentationGeneration;
    private DateTimeOffset _lastNativeNotificationUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFallbackNotificationUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastExternalPromptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _notificationCircuitOpenUntil = DateTimeOffset.MinValue;

    public SteamChatSession(AccountViewModel account, string browserDataFolder)
        : this(
            account,
            browserDataFolder,
            DefaultBrowserInitializationTimeout,
            static browser => browser.EnsureCoreWebView2Async())
    {
    }

    internal SteamChatSession(
        AccountViewModel account,
        string browserDataFolder,
        TimeSpan browserInitializationTimeout,
        Func<WebView2, Task> browserInitializer)
    {
        InitializeComponent();
        _account = account ?? throw new ArgumentNullException(nameof(account));
        ArgumentException.ThrowIfNullOrWhiteSpace(browserDataFolder);
        _browserDataFolder = browserDataFolder;
        if (browserInitializationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(browserInitializationTimeout));
        }

        _browserInitializationTimeout = browserInitializationTimeout;
        _browserInitializer = browserInitializer
            ?? throw new ArgumentNullException(nameof(browserInitializer));
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

    public event EventHandler? ReconnectSessionRequested;

    public Task InitializeAsync() => InitializeOnceAsync(navigateToChat: true);

    public Task InitializeForCleanupAsync() =>
        InitializeOnceAsync(navigateToChat: false);

    private Task InitializeOnceAsync(bool navigateToChat)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_configured)
        {
            return Task.CompletedTask;
        }

        if (_initializationAttempt is { IsCompleted: false })
        {
            return _initializationAttempt;
        }

        _initializationAttempt = InitializeCoreAsync(navigateToChat);
        return _initializationAttempt;
    }

    private async Task InitializeCoreAsync(bool navigateToChat)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _account.ConnectionState = ChatConnectionState.Starting;
        if (!LocalPathPolicy.TryNormalizeLocalPath(
                _browserDataFolder,
                out _,
                requireExisting: false))
        {
            _initializationFailed = true;
            ShowFailure("The secure browser data folder is not a safe local path.");
            return;
        }

        try
        {
            Directory.CreateDirectory(_browserDataFolder);
            if (!LocalPathPolicy.TryNormalizeLocalPath(_browserDataFolder, out _))
            {
                _initializationFailed = true;
                ShowFailure("The secure browser data folder cannot use links or remote storage.");
                return;
            }

            var initializationTimer = Stopwatch.StartNew();
            if (_browserInitializationTask is null)
            {
                Browser.CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = _browserDataFolder,
                    ProfileName = _account.Profile.BrowserProfileName,
                    IsInPrivateModeEnabled = false
                };
                _browserInitializationTask = _browserInitializer(Browser);
            }

            await _browserInitializationTask.WaitAsync(
                _browserInitializationTimeout);
            if (_disposed)
            {
                return;
            }

            var remaining = _browserInitializationTimeout - initializationTimer.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException();
            }

            _permissionResetTask ??=
                ResetPersistedPermissionsAsync(Browser.CoreWebView2.Profile);
            await _permissionResetTask.WaitAsync(remaining);
            if (_disposed)
            {
                return;
            }

            ConfigureSecurity(Browser.CoreWebView2);
            if (navigateToChat)
            {
                if (!_eventsAttached)
                {
                    AttachEvents(Browser.CoreWebView2);
                    _eventsAttached = true;
                }

                Browser.CoreWebView2.Navigate(ChatUri.AbsoluteUri);
            }

            _initializationFailed = false;
            _configured = true;
        }
        catch (TimeoutException)
        {
            _initializationFailed = true;
            ShowFailure(
                "Steam Chat took too long to open. The rest of Switchboard is still available; choose Reconnect to try this workspace again.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or ArgumentException
                or COMException
                or IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or WebView2RuntimeNotFoundException)
        {
            _initializationFailed = true;
            if (_browserInitializationTask is { IsCompleted: true }
                && Browser.CoreWebView2 is null)
            {
                _browserInitializationTask = null;
            }

            if (_permissionResetTask is
                {
                    IsCompleted: true,
                    IsCompletedSuccessfully: false
                })
            {
                _permissionResetTask = null;
            }

            ShowFailure("The secure browser could not start. Install or repair Microsoft Edge WebView2, then reconnect.");
        }
    }

    public bool ShowChat()
    {
        _presentationGeneration++;
        _keepConnectedWhenHidden = true;
        _isPresentedToUser = true;
        Opacity = 1;
        IsHitTestVisible = true;
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
        var presentationGeneration = ++_presentationGeneration;
        _keepConnectedWhenHidden = keepConnected;
        _isPresentedToUser = false;
        Opacity = 1;
        IsHitTestVisible = false;
        Visibility = Visibility.Hidden;
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
            _ = SuspendWhenHiddenAsync(presentationGeneration);
        }
    }

    public void PrepareForBackgroundInitialization(bool keepConnected)
    {
        _presentationGeneration++;
        _keepConnectedWhenHidden = keepConnected;
        _isPresentedToUser = false;
        IsHitTestVisible = false;
        Opacity = 1;

        // Hidden participates in layout and remains connected to the window,
        // unlike Collapsed. WebView2 can therefore create a correctly sized,
        // non-painting controller for a background account.
        Visibility = Visibility.Hidden;
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
        ObserveBackgroundFailure(_browserInitializationTask);
        ObserveBackgroundFailure(_permissionResetTask);
        DisposeBrowserController();
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
        _navigationTracker.Reset();

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
            _navigationTracker.RecordHostCancellation(e.NavigationId);
            e.Cancel = true;
            if (SteamNavigationPolicy.ShouldPromptForExternalLink(
                    e.Uri,
                    e.IsUserInitiated))
            {
                OpenExternalWithConfirmation(e.Uri);
            }

            return;
        }

        _navigationTracker.RecordAllowedNavigation(e.NavigationId);

        if (SteamNavigationPolicy.IsBootstrapDocument(e.Uri))
        {
            return;
        }

        Browser.Visibility = Visibility.Hidden;
        LoadingOverlay.Visibility = Visibility.Visible;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        _account.ConnectionState = ChatConnectionState.Reconnecting;
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_navigationTracker.ShouldHandleCompletion(e.NavigationId))
        {
            return;
        }

        if (Browser.Source is not { } source
            || !SteamNavigationPolicy.IsAllowedEmbeddedDocument(source.AbsoluteUri))
        {
            ShowNavigationFailure(
                "The Steam page was blocked or could not load. Check your connection and reconnect.");
            return;
        }

        if (SteamNavigationPolicy.IsBootstrapDocument(source.AbsoluteUri))
        {
            Browser.Visibility = Visibility.Hidden;
            LoadingOverlay.Visibility = Visibility.Visible;
            _account.ConnectionState = ChatConnectionState.Starting;
            return;
        }
        if (!e.IsSuccess)
        {
            ShowNavigationFailure(
                "The Steam page was blocked or could not load. Check your connection and reconnect.");
            return;
        }

        LoadingOverlay.Visibility = Visibility.Collapsed;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Visible;
        _account.ConnectionState = SteamNavigationPolicy.IsLoginDocument(source)
            ? ChatConnectionState.SignInRequired
            : ChatConnectionState.Ready;
        if (_account.ConnectionState == ChatConnectionState.Ready
            && _isPresentedToUser)
        {
            ChatReadyWhileVisible?.Invoke(this, EventArgs.Empty);
        }
        if (!_keepConnectedWhenHidden && !_isPresentedToUser)
        {
            _ = SuspendWhenHiddenAsync(_presentationGeneration);
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
        _initializationFailed = true;
        ShowFailure("The Steam Chat browser stopped unexpectedly. Reconnect to continue.");
    }

    private static void ObserveBackgroundFailure(Task? task)
    {
        if (task is null)
        {
            return;
        }

        if (task.IsFaulted)
        {
            _ = task.Exception;
            return;
        }

        if (task.IsCompleted)
        {
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
                | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async void OnReconnectClicked(object sender, RoutedEventArgs e)
    {
        if (_securityClosed || _initializationFailed)
        {
            ReconnectSessionRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        ErrorOverlay.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;
        Browser.Visibility = Visibility.Hidden;
        _account.ConnectionState = ChatConnectionState.Reconnecting;

        try
        {
            if (!_configured)
            {
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

    private async Task SuspendWhenHiddenAsync(long presentationGeneration)
    {
        var core = Browser.CoreWebView2;
        if (core is null
            || _disposed
            || presentationGeneration != _presentationGeneration
            || _isPresentedToUser
            || _keepConnectedWhenHidden)
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
            var suspended = await core.TrySuspendAsync();
            if (_disposed)
            {
                return;
            }

            if (presentationGeneration != _presentationGeneration
                || _isPresentedToUser
                || _keepConnectedWhenHidden)
            {
                _account.IsSleeping = false;
                if (suspended || core.IsSuspended)
                {
                    core.Resume();
                }

                return;
            }

            _account.IsSleeping = suspended;
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
            if (_keepConnectedWhenHidden || _isPresentedToUser)
            {
                core.Navigate(ChatUri.AbsoluteUri);
            }
            else
            {
                await SuspendWhenHiddenAsync(_presentationGeneration);
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
        DisposeBrowserController();
        ShowFailure(
            "This workspace was closed because its microphone session could not be ended safely. Choose Reconnect to create a fresh workspace.");
    }

    private void DisposeBrowserController()
    {
        try
        {
            if (_eventsAttached && Browser.CoreWebView2 is { } core)
            {
                DetachEvents(core);
            }
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            // Controller teardown remains best effort after runtime failure.
        }
        finally
        {
            _eventsAttached = false;
        }

        try
        {
            Browser.Dispose();
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            // A failed browser process may already have released the controller.
        }
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
            $"Profile nickname: {_account.DisplayName}  •  expected Steam login: {_account.SteamLoginName}";
    }

    private bool IsWorkspaceVisible() =>
        _isPresentedToUser
        && Opacity > 0
        && IsVisible
        && Browser.Visibility == Visibility.Visible
        && _account.ConnectionState == ChatConnectionState.Ready
        && Window.GetWindow(this)?.IsActive == true;

    private void ShowFailure(string message)
    {
        _account.ConnectionState = ChatConnectionState.Failed;
        Browser.Visibility = Visibility.Hidden;
        LoadingOverlay.Visibility = Visibility.Collapsed;
        ErrorMessage.Text = message;
        ErrorOverlay.Visibility = Visibility.Visible;
    }

    private void ShowNavigationFailure(string message)
    {
        _ = StopActiveMediaIfNeeded();
        ShowFailure(message);
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
