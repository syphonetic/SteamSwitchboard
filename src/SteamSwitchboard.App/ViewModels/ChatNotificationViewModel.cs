using System.Globalization;

namespace SteamSwitchboard.ViewModels;

public sealed class ChatNotificationViewModel : ObservableObject
{
    private string _accountDisplayName;
    private string _accountLoginName;
    private string _steamTitle;
    private string _preview;
    private DateTimeOffset _receivedUtc;
    private bool _isUnread = true;
    private bool _isUnreadFallback;
    private NotificationLifecycle? _lifecycle;

    public ChatNotificationViewModel(
        Guid accountId,
        string accountDisplayName,
        string accountLoginName,
        string steamTitle,
        string preview,
        DateTimeOffset receivedUtc,
        string? replacementTag = null,
        bool isUnreadFallback = false,
        Action? reportClicked = null,
        Action? reportClosed = null)
    {
        AccountId = accountId;
        _accountDisplayName = accountDisplayName;
        _accountLoginName = accountLoginName;
        _steamTitle = steamTitle;
        _preview = preview;
        _receivedUtc = receivedUtc;
        ReplacementTag = replacementTag;
        _isUnreadFallback = isUnreadFallback;
        _lifecycle = NotificationLifecycle.Create(reportClicked, reportClosed);
    }

    public Guid Id { get; } = Guid.NewGuid();

    public Guid AccountId { get; }

    public string AccountDisplayName => _accountDisplayName;

    public string AccountLoginName => _accountLoginName;

    public string SteamTitle => _steamTitle;

    public string Preview => _preview;

    public DateTimeOffset ReceivedUtc => _receivedUtc;

    public string? ReplacementTag { get; private set; }

    public bool IsUnreadFallback => _isUnreadFallback;

    public bool IsUnread
    {
        get => _isUnread;
        private set => SetProperty(ref _isUnread, value);
    }

    public string Heading =>
        $"{AccountDisplayName} — Steam login: {AccountLoginName}  •  {SteamTitle}";

    public string ReceivedTime => ReceivedUtc
        .ToLocalTime()
        .ToString("t", CultureInfo.CurrentCulture);

    public void UpdateAccountDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (SetProperty(ref _accountDisplayName, displayName, nameof(AccountDisplayName)))
        {
            OnPropertyChanged(nameof(Heading));
        }
    }

    public void UpdateAccountLoginName(string loginName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginName);
        if (SetProperty(ref _accountLoginName, loginName, nameof(AccountLoginName)))
        {
            OnPropertyChanged(nameof(Heading));
        }
    }

    public void MarkRead() => IsUnread = false;

    public void Replace(
        string steamTitle,
        string preview,
        DateTimeOffset receivedUtc,
        string? replacementTag,
        bool isUnreadFallback,
        Action? reportClicked,
        Action? reportClosed)
    {
        CloseLifecycle();
        _lifecycle = NotificationLifecycle.Create(reportClicked, reportClosed);
        ReplacementTag = replacementTag;
        _isUnreadFallback = isUnreadFallback;
        IsUnread = true;
        _ = SetProperty(ref _steamTitle, steamTitle, nameof(SteamTitle));
        _ = SetProperty(ref _preview, preview, nameof(Preview));
        _ = SetProperty(ref _receivedUtc, receivedUtc, nameof(ReceivedUtc));
        OnPropertyChanged(nameof(IsUnreadFallback));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(ReceivedTime));
    }

    public void ReportClickedAndClose() => _lifecycle?.ReportClickedAndClose();

    public void CloseLifecycle()
    {
        _lifecycle?.Close();
        _lifecycle = null;
    }

    public void RedactPreview()
    {
        _ = SetProperty(
            ref _preview,
            "New Steam Chat message",
            nameof(Preview));
    }

    private sealed class NotificationLifecycle
    {
        private Action? _reportClicked;
        private Action? _reportClosed;
        private int _closed;

        private NotificationLifecycle(
            Action? reportClicked,
            Action? reportClosed)
        {
            _reportClicked = reportClicked;
            _reportClosed = reportClosed;
        }

        public static NotificationLifecycle? Create(
            Action? reportClicked,
            Action? reportClosed) =>
            reportClicked is null && reportClosed is null
                ? null
                : new NotificationLifecycle(reportClicked, reportClosed);

        public void ReportClickedAndClose()
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _reportClicked, null)?.Invoke();
            Close();
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            _reportClicked = null;
            Interlocked.Exchange(ref _reportClosed, null)?.Invoke();
        }
    }
}
