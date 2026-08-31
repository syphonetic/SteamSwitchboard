using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace SteamSwitchboard.Services;

public sealed class WindowsAppNotificationService : IDisposable
{
    private const string OpenAction = "open";
    private const string AccountArgument = "account";
    private const string NotificationArgument = "notification";
    private const string TestNotificationGroup = "switchboard-tests";

    private readonly object _gate = new();
    private AppNotificationManager? _manager;
    private bool _enabled;
    private bool _registered;
    private bool _disposed;
    private bool _initialActivationRead;

    public event EventHandler<WindowsAppNotificationActivatedEventArgs>? Activated;

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _enabled && _registered;
            }
        }
    }

    public string StatusText
    {
        get
        {
            lock (_gate)
            {
                return _statusText;
            }
        }
    }

    private string _statusText = "Windows alerts have not been enabled yet.";

    public bool TryEnable()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _enabled = true;
            return TryRegisterCore();
        }
    }

    private bool TryRegisterCore()
    {
        if (_registered)
        {
            return true;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
            || !WindowsAppSdkRuntimeLoader.TryLoad())
        {
            _statusText =
                "Modern Windows alerts are unavailable here; Switchboard will use a compatibility tray alert.";
            return false;
        }

        AppNotificationManager? manager = null;
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                _statusText =
                    "Modern Windows alerts are unavailable here; Switchboard will use a compatibility tray alert.";
                return false;
            }

            manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            _manager = manager;
            _registered = true;
            _statusText =
                "Modern Windows alerts are ready. Use “Send test alert” to verify Windows delivery.";
            return true;
        }
        catch (Exception exception) when (IsRecoverableNotificationFailure(exception))
        {
            if (manager is not null)
            {
                manager.NotificationInvoked -= OnNotificationInvoked;
            }

            _manager = null;
            _registered = false;
            _statusText =
                "Modern Windows alerts are unavailable here; Switchboard will use a compatibility tray alert.";
            return false;
        }
    }

    public bool TryShow(
        Guid? accountId,
        Guid? notificationId,
        string accountIdentity,
        string steamTitle,
        string preview,
        string? replacementTag = null,
        bool isTest = false)
    {
        lock (_gate)
        {
            if (_disposed
                || !_enabled
                || !TryRegisterCore()
                || _manager is null)
            {
                return false;
            }

            var safeIdentity = SafeText.SanitizeDisplayText(
                accountIdentity,
                "SteamSwitchboard profile",
                maximumLength: 100);
            var safeTitle = SafeText.SanitizeDisplayText(
                steamTitle,
                isTest ? "SteamSwitchboard test alert" : "Steam Chat",
                maximumLength: 100);
            var safePreview = SafeText.SanitizeDisplayText(
                preview,
                isTest ? "Windows alerts are working." : "New Steam Chat message",
                maximumLength: 180);

            try
            {
                var builder = new AppNotificationBuilder()
                    .AddArgument("action", OpenAction)
                    .AddText(safeIdentity)
                    .AddText(safeTitle)
                    .AddText(safePreview);
                if (accountId is Guid id)
                {
                    builder.AddArgument(AccountArgument, id.ToString("N"));
                    if (notificationId is Guid liveNotificationId)
                    {
                        builder.AddArgument(
                            NotificationArgument,
                            liveNotificationId.ToString("N"));
                    }

                    builder.SetGroup(GetAccountGroup(id));
                    if (!string.IsNullOrWhiteSpace(replacementTag))
                    {
                        builder.SetTag(CreateReplacementTag(id, replacementTag));
                    }
                }
                else if (isTest && !string.IsNullOrWhiteSpace(replacementTag))
                {
                    builder.SetGroup(TestNotificationGroup);
                    builder.SetTag(CreateReplacementTag(Guid.Empty, replacementTag));
                }

                var notification = builder.BuildNotification();
                notification.Expiration = DateTimeOffset.Now.AddHours(24);
                notification.ExpiresOnReboot = true;
                _manager.Show(notification);
                _statusText = isTest
                    ? "Test alert submitted to Windows. If it is hidden, check Do not disturb and the Windows notification settings."
                    : "Modern chat alert submitted to Windows.";
                return true;
            }
            catch (Exception exception) when (IsRecoverableNotificationFailure(exception))
            {
                DisableRegistrationCore();
                _statusText =
                    "Modern alert delivery failed; Switchboard switched to a compatibility tray alert.";
                return false;
            }
        }
    }

    public bool Remove(Guid? accountId)
    {
        return RemoveMany(
            removeAll: accountId is null,
            accountId is Guid id ? [id] : []);
    }

    public bool RemoveMany(
        bool removeAll,
        IReadOnlyCollection<Guid> accountIds)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (!removeAll && accountIds.Count == 0)
            {
                return true;
            }

            if (!TryRegisterCore() || _manager is null)
            {
                if (!_enabled)
                {
                    _statusText =
                        "Windows alerts are off. In-app notification history remains available.";
                }

                return false;
            }

            var removed = true;
            if (removeAll)
            {
                removed = RemoveHistoryCore(accountId: null);
            }
            else
            {
                foreach (var accountId in accountIds.Distinct())
                {
                    removed = RemoveHistoryCore(accountId) && removed;
                }
            }

            if (!_enabled)
            {
                DisableRegistrationCore();
                _statusText =
                    removed
                        ? "Windows alerts are off. In-app notification history remains available."
                        : "Windows alerts are off. Windows history cleanup will retry next time.";
            }

            return removed;
        }
    }

    public bool Disable()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _enabled = false;
            var removed = false;
            if (TryRegisterCore())
            {
                removed = RemoveHistoryCore(accountId: null);
            }

            DisableRegistrationCore();
            _statusText = removed
                ? "Windows alerts are off. In-app notification history remains available."
                : "Windows alerts are off. Windows history cleanup will retry next time.";
            return removed;
        }
    }

    public WindowsAppNotificationActivatedEventArgs? TryGetCurrentActivation()
    {
        lock (_gate)
        {
            if (!_enabled || !_registered || _initialActivationRead)
            {
                return null;
            }

            _initialActivationRead = true;
            try
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                if (activation.Kind != ExtendedActivationKind.AppNotification
                    || activation.Data is not AppNotificationActivatedEventArgs args
                    || !TryParseActivationArguments(
                        args.Arguments,
                        out var accountId,
                        out var notificationId))
                {
                    return null;
                }

                return new WindowsAppNotificationActivatedEventArgs(
                    accountId,
                    notificationId);
            }
            catch (Exception exception) when (IsRecoverableNotificationFailure(exception))
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _enabled = false;
            DisableRegistrationCore();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private static string GetAccountGroup(Guid accountId) =>
        accountId.ToString("N")[..16];

    internal static string CreateReplacementTag(
        Guid accountId,
        string replacementTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementTag);
        var raw = Encoding.UTF8.GetBytes($"{accountId:N}\0{replacementTag}");
        return Convert.ToHexString(SHA256.HashData(raw).AsSpan(0, 8));
    }

    internal static bool TryParseActivationArguments(
        IDictionary<string, string> arguments,
        out Guid? accountId,
        out Guid? notificationId)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        accountId = null;
        notificationId = null;
        if (arguments.Count is < 1 or > 3
            || !arguments.TryGetValue("action", out var action)
            || !string.Equals(action, OpenAction, StringComparison.Ordinal))
        {
            return false;
        }

        if (!arguments.TryGetValue(AccountArgument, out var rawAccountId))
        {
            return arguments.Count == 1
                && !arguments.ContainsKey(NotificationArgument);
        }

        if (rawAccountId.Length != 32
            || !Guid.TryParseExact(rawAccountId, "N", out var parsedAccountId))
        {
            return false;
        }

        accountId = parsedAccountId;
        if (!arguments.TryGetValue(
                NotificationArgument,
                out var rawNotificationId))
        {
            return arguments.Count == 2;
        }

        if (arguments.Count != 3
            || rawNotificationId.Length != 32
            || !Guid.TryParseExact(
                rawNotificationId,
                "N",
                out var parsedNotificationId))
        {
            return false;
        }

        notificationId = parsedNotificationId;
        return true;
    }

    private static bool IsRecoverableNotificationFailure(Exception exception) =>
        exception is COMException
            or InvalidOperationException
            or NotSupportedException
            or UnauthorizedAccessException
            or ArgumentException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException;

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        if (Volatile.Read(ref _disposed)
            || !Volatile.Read(ref _enabled)
            || !Volatile.Read(ref _registered))
        {
            return;
        }

        if (!TryParseActivationArguments(
                args.Arguments,
                out var accountId,
                out var notificationId))
        {
            return;
        }

        Activated?.Invoke(
            this,
            new WindowsAppNotificationActivatedEventArgs(
                accountId,
                notificationId));
    }

    private bool RemoveHistoryCore(Guid? accountId)
    {
        if (_manager is null)
        {
            return false;
        }

        try
        {
            if (accountId is Guid id)
            {
                _manager.RemoveByGroupAsync(GetAccountGroup(id))
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                _manager.RemoveAllAsync()
                    .GetAwaiter()
                    .GetResult();
            }

            return true;
        }
        catch (Exception exception) when (IsRecoverableNotificationFailure(exception))
        {
            _statusText =
                "Windows notification history could not be cleared yet; Switchboard will retry next time.";
            return false;
        }
    }

    private void DisableRegistrationCore()
    {
        var manager = _manager;
        _manager = null;
        if (manager is null)
        {
            _registered = false;
            return;
        }

        manager.NotificationInvoked -= OnNotificationInvoked;
        if (_registered)
        {
            try
            {
                manager.Unregister();
            }
            catch (Exception exception) when (IsRecoverableNotificationFailure(exception))
            {
                // Registration cleanup is best effort during settings changes and shutdown.
            }
        }

        _registered = false;
    }
}

internal static class WindowsAppSdkRuntimeLoader
{
    private static readonly object Gate = new();
    private static bool _attempted;
    private static bool _loaded;

    internal static bool TryLoad()
    {
        lock (Gate)
        {
            if (_attempted)
            {
                return _loaded;
            }

            _attempted = true;
            try
            {
                Environment.SetEnvironmentVariable(
                    "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
                    AppContext.BaseDirectory);
                Environment.SetEnvironmentVariable(
                    "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY_PID",
                    Environment.ProcessId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                _ = WindowsAppRuntimeEnsureIsLoaded();
                _loaded = true;
            }
            catch (Exception exception) when (
                exception is DllNotFoundException
                    or EntryPointNotFoundException
                    or BadImageFormatException
                    or System.Security.SecurityException
                    or ArgumentException)
            {
                _loaded = false;
            }

            return _loaded;
        }
    }

    [DllImport(
        "Microsoft.WindowsAppRuntime.dll",
        EntryPoint = "WindowsAppRuntime_EnsureIsLoaded",
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int WindowsAppRuntimeEnsureIsLoaded();
}

public sealed class WindowsAppNotificationActivatedEventArgs(
    Guid? accountId,
    Guid? notificationId)
    : EventArgs
{
    public Guid? AccountId { get; } = accountId;

    public Guid? NotificationId { get; } = notificationId;
}

internal sealed class OrderedCommandQueue<TTarget> : IDisposable
    where TTarget : notnull
{
    private const int MaximumPendingCommands = 256;

    private readonly TTarget _target;
    private readonly Channel<Command> _channel;
    private readonly Task _worker;
    private int _disposed;

    public OrderedCommandQueue(TTarget target)
    {
        _target = target;
        _channel = Channel.CreateBounded<Command>(
            new BoundedChannelOptions(MaximumPendingCommands)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        _worker = Task.Run(ProcessAsync);
    }

    internal Task Completion => _worker;

    public async Task<TResult> EnqueueAsync<TResult>(
        Func<TTarget, TResult> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _channel.Writer.WriteAsync(
                new Command(target => action(target), completion),
                cancellationToken);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(GetType().Name);
        }

        var result = await completion.Task.WaitAsync(cancellationToken);
        return (TResult)result!;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        GC.SuppressFinalize(this);
    }

    private async Task ProcessAsync()
    {
        await foreach (var command in _channel.Reader.ReadAllAsync())
        {
            try
            {
                command.Completion.TrySetResult(command.Action(_target));
            }
            catch (Exception exception)
            {
                command.Completion.TrySetException(exception);
            }
        }
    }

    private sealed record Command(
        Func<TTarget, object?> Action,
        TaskCompletionSource<object?> Completion);
}
