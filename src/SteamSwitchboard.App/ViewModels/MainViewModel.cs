using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using SteamSwitchboard.Models;
using SteamSwitchboard.Services;

namespace SteamSwitchboard.ViewModels;

public enum AppSection
{
    Chats,
    Library,
    Settings
}

public enum NativeSteamAccountState
{
    NoProfiles,
    SteamNotRunning,
    Unknown,
    Match,
    Mismatch
}

public sealed class MainViewModel : ObservableObject
{
    private const int MaximumNotificationHistory = 100;

    private readonly StateStore _stateStore;
    private readonly SteamInstallationService _steamInstallation;
    private readonly SteamLibraryService _steamLibrary;
    private PersistedState _state = new();
    private AccountViewModel? _selectedAccount;
    private AccountViewModel? _selectedPlayAccount;
    private AppSection _selectedSection = AppSection.Chats;
    private string _gameSearch = string.Empty;
    private string _statusMessage = "Getting things ready…";
    private bool _isBusy;
    private bool _isLaunchCheckInProgress;
    private string? _steamExecutablePath;
    private string _currentSteamAccountStatus = "Current in Steam: not detected";
    private NativeSteamAccountState _nativeSteamAccountState = NativeSteamAccountState.Unknown;
    private long _refreshGeneration;

    public MainViewModel(
        StateStore stateStore,
        SteamInstallationService steamInstallation,
        SteamLibraryService steamLibrary)
    {
        _stateStore = stateStore;
        _steamInstallation = steamInstallation;
        _steamLibrary = steamLibrary;

        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = FilterGame;
    }

    public ObservableCollection<AccountViewModel> Accounts { get; } = [];

    public ObservableCollection<InstalledGame> Games { get; } = [];

    public ObservableCollection<ChatNotificationViewModel> Notifications { get; } = [];

    public ICollectionView GamesView { get; }

    public AccountViewModel? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (EqualityComparer<AccountViewModel?>.Default.Equals(
                    _selectedAccount,
                    value))
            {
                return;
            }

            if (_selectedAccount is not null)
            {
                _selectedAccount.PropertyChanged -= OnSelectedAccountPropertyChanged;
            }

            _selectedAccount = value;
            if (_selectedAccount is not null)
            {
                _selectedAccount.PropertyChanged += OnSelectedAccountPropertyChanged;
            }

            OnPropertyChanged();
            if (value is not null)
            {
                value.Profile.LastUsedUtc = DateTimeOffset.UtcNow;
                _state.LastSelectedAccountId = value.Id;
            }

            OnPropertyChanged(nameof(HasSelectedAccount));
            OnPropertyChanged(nameof(IsSelectedAccountPendingDeletion));
            OnPropertyChanged(nameof(SelectedProfileDisplayName));
            OnPropertyChanged(nameof(SelectedProfileSteamLoginName));
            OnPropertyChanged(nameof(SelectedProfileAccentHex));
        }
    }

    public bool HasSelectedAccount => SelectedAccount is not null;

    public string SelectedProfileDisplayName => SelectedAccount?.DisplayName ?? string.Empty;

    public string SelectedProfileSteamLoginName =>
        SelectedAccount?.SteamLoginName ?? string.Empty;

    public string SelectedProfileAccentHex =>
        SelectedAccount?.AccentHex ?? "#66C0F4";

    public AccountViewModel? SelectedPlayAccount
    {
        get => _selectedPlayAccount;
        set
        {
            if (!SetProperty(ref _selectedPlayAccount, value))
            {
                return;
            }

            _state.LastPlayAccountId = value?.Id;
            OnPropertyChanged(nameof(HasSelectedPlayAccount));
            OnPropertyChanged(nameof(CanLaunchGames));
            OnPropertyChanged(nameof(SelectedPlayAccountStatus));
        }
    }

    public bool HasSelectedPlayAccount => SelectedPlayAccount is not null;

    public bool IsLaunchCheckInProgress
    {
        get => _isLaunchCheckInProgress;
        private set
        {
            if (SetProperty(ref _isLaunchCheckInProgress, value))
            {
                OnPropertyChanged(nameof(CanLaunchGames));
            }
        }
    }

    public bool CanLaunchGames =>
        HasSelectedPlayAccount && !IsLaunchCheckInProgress;

    public bool TryBeginLaunchCheck()
    {
        if (IsLaunchCheckInProgress)
        {
            return false;
        }

        IsLaunchCheckInProgress = true;
        return true;
    }

    public void EndLaunchCheck() => IsLaunchCheckInProgress = false;

    public bool IsSelectedAccountPendingDeletion =>
        SelectedAccount is { } selected
        && IsAccountDeletionPending(selected.Id);

    private void OnSelectedAccountPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, SelectedAccount))
        {
            return;
        }

        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(AccountViewModel.DisplayName))
        {
            OnPropertyChanged(nameof(SelectedProfileDisplayName));
        }

        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(AccountViewModel.SteamLoginName))
        {
            OnPropertyChanged(nameof(SelectedProfileSteamLoginName));
        }

        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(AccountViewModel.AccentHex))
        {
            OnPropertyChanged(nameof(SelectedProfileAccentHex));
        }
    }

    public bool HasAccounts => Accounts.Count > 0;

    public bool HasGames => Games.Count > 0;

    public bool HasNotifications => Notifications.Count > 0;

    public bool HasUnreadNotifications => Notifications.Any(item => item.IsUnread);

    public int NotificationCount => Notifications.Count(item => item.IsUnread);

    public string NotificationCountLabel => NotificationCount > 99
        ? "99+"
        : NotificationCount.ToString();

    public AppSection SelectedSection
    {
        get => _selectedSection;
        set => SetProperty(ref _selectedSection, value);
    }

    public string GameSearch
    {
        get => _gameSearch;
        set
        {
            if (SetProperty(ref _gameSearch, value))
            {
                GamesView.Refresh();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public string? SteamExecutablePath
    {
        get => _steamExecutablePath;
        set
        {
            var validatedPath = string.IsNullOrWhiteSpace(value)
                ? null
                : _steamInstallation.ValidateSteamExecutableCandidate(value);
            if (SetProperty(ref _steamExecutablePath, validatedPath))
            {
                _state.Settings.SteamExecutablePath = validatedPath;
                OnPropertyChanged(nameof(SteamPathStatus));
                OnPropertyChanged(nameof(IsSteamDetected));
            }
        }
    }

    public bool IsSteamDetected => !string.IsNullOrWhiteSpace(SteamExecutablePath);

    public string SteamPathStatus => IsSteamDetected
        ? "Signed Valve Steam detected"
        : "Steam needs attention";

    public string CurrentSteamAccountStatus
    {
        get => _currentSteamAccountStatus;
        set
        {
            if (SetProperty(ref _currentSteamAccountStatus, value))
            {
                OnPropertyChanged(nameof(SelectedPlayAccountStatus));
            }
        }
    }

    public string SelectedPlayAccountStatus => SelectedPlayAccount switch
    {
        null => "Choose the Steam account required for launches",
        _ when NativeSteamAccountState == NativeSteamAccountState.SteamNotRunning =>
            "Start Steam; Switchboard will verify the active login",
        _ when NativeSteamAccountState == NativeSteamAccountState.Unknown =>
            "Waiting for Steam to report its active login",
        { IsActiveInSteam: true } account when
            NativeSteamAccountState == NativeSteamAccountState.Match =>
            $"Match confirmed: login {account.SteamLoginName} is active in Steam",
        _ when NativeSteamAccountState == NativeSteamAccountState.Mismatch =>
            "Switch Steam to the required login before launch",
        _ => "Switchboard will verify the required login before launch"
    };

    public NativeSteamAccountState NativeSteamAccountState
    {
        get => _nativeSteamAccountState;
        set
        {
            if (SetProperty(ref _nativeSteamAccountState, value))
            {
                OnPropertyChanged(nameof(SelectedPlayAccountStatus));
            }
        }
    }

    public string VersionLabel
    {
        get
        {
            var version = typeof(MainViewModel).Assembly.GetName().Version;
            return version is null
                ? "Version 1"
                : $"Version {version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string DataSummary =>
        $"{Accounts.Count} account{(Accounts.Count == 1 ? string.Empty : "s")} · {Games.Count} local library item{(Games.Count == 1 ? string.Empty : "s")}";

    public AppSettings Settings => _state.Settings;

    public bool KeepAllChatsLive
    {
        get => _state.Settings.KeepAllChatsLive;
        set
        {
            if (_state.Settings.KeepAllChatsLive == value)
            {
                return;
            }

            _state.Settings.KeepAllChatsLive = value;
            OnPropertyChanged();
        }
    }

    public bool ShowNotificationPreviews
    {
        get => _state.Settings.ShowNotificationPreviews;
        set
        {
            if (_state.Settings.ShowNotificationPreviews == value)
            {
                return;
            }

            _state.Settings.ShowNotificationPreviews = value;
            if (!value)
            {
                foreach (var notification in Notifications)
                {
                    notification.RedactPreview();
                }
            }

            OnPropertyChanged();
        }
    }

    public bool EnableWindowsNotifications
    {
        get => _state.Settings.EnableWindowsNotifications;
        set
        {
            if (_state.Settings.EnableWindowsNotifications == value)
            {
                return;
            }

            _state.Settings.EnableWindowsNotifications = value;
            OnPropertyChanged();
        }
    }

    public string? ValidateSteamExecutableCandidate(string? candidate) =>
        _steamInstallation.ValidateSteamExecutableCandidate(candidate);

    public IReadOnlyList<AccountViewModel> AccountsPendingBrowserProfileDeletion =>
        Accounts
            .Where(account => IsAccountDeletionPending(account.Id))
            .ToArray();

    public bool IsAccountDeletionPending(Guid accountId) =>
        _state.PendingBrowserProfileDeletionIds.Contains(accountId);

    public bool HasPendingWindowsNotificationHistoryClear =>
        _state.PendingWindowsNotificationHistoryClear;

    public IReadOnlyList<Guid> AccountsPendingWindowsNotificationCleanup =>
        _state.PendingWindowsNotificationAccountCleanupIds.ToArray();

    public Guid? PendingWindowsNotificationCleanupRequestId =>
        _state.PendingWindowsNotificationCleanupRequestId;

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default,
        bool refreshGames = true)
    {
        IsBusy = true;
        string? stateLoadWarning = null;
        try
        {
            try
            {
                _state = await _stateStore.LoadAsync(cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                _state = new PersistedState();
                stateLoadWarning = exception.Message;
            }

            RaiseSettingsStateChanged();

            Accounts.Clear();
            foreach (var account in _state.Accounts.OrderByDescending(item => item.LastUsedUtc))
            {
                Accounts.Add(new AccountViewModel(account));
            }

            SelectedAccount = Accounts.FirstOrDefault(account => account.Id == _state.LastSelectedAccountId)
                ?? Accounts.FirstOrDefault();
            SelectedPlayAccount = Accounts.FirstOrDefault(
                account => account.Id == _state.LastPlayAccountId);

            SteamExecutablePath = _steamInstallation.FindSteamExecutable(
                _state.Settings.SteamExecutablePath);
            if (refreshGames)
            {
                await RefreshGamesAsync(cancellationToken);
            }
            StatusMessage = stateLoadWarning
                ?? (HasAccounts
                    ? _state.PendingBrowserProfileDeletionIds.Count > 0
                        ? "Finishing a previously requested account cleanup…"
                        : _state.PendingWindowsNotificationHistoryClear
                            || _state.PendingWindowsNotificationAccountCleanupIds.Count > 0
                            ? "Finishing a previous Windows alert cleanup…"
                        : "Ready"
                    : "Add your first account to begin");
            RaiseCollectionStateChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<AccountViewModel> AddAccountAsync(
        AccountProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        AccountValidator.Normalize(profile);
        var validation = AccountValidator.Validate(profile, _state.Accounts);
        if (validation is not null)
        {
            throw new ArgumentException(validation, nameof(profile));
        }

        var previousSelected = SelectedAccount;
        var previousSelectedId = _state.LastSelectedAccountId;
        var previousPlayAccount = SelectedPlayAccount;
        var previousPlayAccountId = _state.LastPlayAccountId;
        _state.Accounts.Add(profile);
        _state.LastSelectedAccountId = profile.Id;
        var viewModel = new AccountViewModel(profile);
        Accounts.Insert(0, viewModel);
        SelectedAccount = viewModel;
        if (previousPlayAccount is null)
        {
            SelectedPlayAccount = viewModel;
        }
        try
        {
            await SaveAsync(cancellationToken);
        }
        catch
        {
            Accounts.Remove(viewModel);
            _state.Accounts.Remove(profile);
            SelectedAccount = previousSelected;
            _state.LastSelectedAccountId = previousSelectedId;
            SelectedPlayAccount = previousPlayAccount;
            _state.LastPlayAccountId = previousPlayAccountId;
            RaiseCollectionStateChanged();
            throw;
        }

        RaiseCollectionStateChanged();
        StatusMessage = $"{profile.DisplayName} added. Sign in on Steam's page.";
        return viewModel;
    }

    public async Task RemoveAccountAsync(
        AccountViewModel account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var accountIndex = Accounts.IndexOf(account);
        var stateIndex = _state.Accounts.FindIndex(item => item.Id == account.Id);
        if (accountIndex < 0 || stateIndex < 0)
        {
            throw new InvalidOperationException("That account is no longer in Switchboard.");
        }

        var previousSelected = SelectedAccount;
        var previousSelectedId = _state.LastSelectedAccountId;
        var previousPlayAccount = SelectedPlayAccount;
        var previousPlayAccountId = _state.LastPlayAccountId;
        var wasSelected = ReferenceEquals(previousSelected, account);
        var pendingWasPresent = _state.PendingBrowserProfileDeletionIds.Remove(account.Id);
        OnPropertyChanged(nameof(IsSelectedAccountPendingDeletion));
        Accounts.Remove(account);
        var removedProfile = _state.Accounts[stateIndex];
        _state.Accounts.RemoveAt(stateIndex);
        if (wasSelected || SelectedAccount is null)
        {
            SelectedAccount = Accounts.FirstOrDefault();
        }

        var removedPlayAccount = ReferenceEquals(previousPlayAccount, account);
        if (removedPlayAccount)
        {
            SelectedPlayAccount = null;
        }

        _state.LastSelectedAccountId = SelectedAccount?.Id;
        _state.LastPlayAccountId = SelectedPlayAccount?.Id;
        try
        {
            await SaveAsync(cancellationToken);
        }
        catch
        {
            _state.Accounts.Insert(stateIndex, removedProfile);
            Accounts.Insert(accountIndex, account);
            if (pendingWasPresent)
            {
                _state.PendingBrowserProfileDeletionIds.Add(account.Id);
            }

            SelectedAccount = previousSelected;
            _state.LastSelectedAccountId = previousSelectedId;
            SelectedPlayAccount = previousPlayAccount;
            _state.LastPlayAccountId = previousPlayAccountId;
            OnPropertyChanged(nameof(IsSelectedAccountPendingDeletion));
            RaiseCollectionStateChanged();
            throw;
        }

        RaiseCollectionStateChanged();
        ClearNotifications(account.Id);
        StatusMessage = removedPlayAccount && Accounts.Count > 0
            ? $"{account.DisplayName} was forgotten. Choose a new required Steam account."
            : $"{account.DisplayName} was forgotten on this PC.";
    }

    public async Task RenameAccountAsync(
        AccountViewModel account,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!_state.Accounts.Any(item => item.Id == account.Id))
        {
            throw new InvalidOperationException("That account is no longer in Switchboard.");
        }

        var candidate = new AccountProfile
        {
            Id = account.Profile.Id,
            DisplayName = displayName,
            SteamLoginName = account.Profile.SteamLoginName,
            AccentHex = account.Profile.AccentHex,
            CreatedUtc = account.Profile.CreatedUtc,
            LastUsedUtc = account.Profile.LastUsedUtc
        };
        AccountValidator.Normalize(candidate);
        var validation = AccountValidator.Validate(
            candidate,
            _state.Accounts.Where(item => item.Id != account.Id));
        if (validation is not null)
        {
            throw new ArgumentException(validation, nameof(displayName));
        }

        var previousName = account.Profile.DisplayName;
        account.Profile.DisplayName = candidate.DisplayName;
        account.NotifyProfileChanged();
        try
        {
            await SaveAsync(cancellationToken);
        }
        catch
        {
            account.Profile.DisplayName = previousName;
            account.NotifyProfileChanged();
            throw;
        }

        foreach (var notification in Notifications.Where(
                     item => item.AccountId == account.Id))
        {
            notification.UpdateAccountDisplayName(account.DisplayName);
        }

        StatusMessage = $"Profile renamed to {account.DisplayName}";
    }

    public async Task RelinkSteamLoginAsync(
        AccountViewModel account,
        string steamLoginName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!_state.Accounts.Any(item => item.Id == account.Id))
        {
            throw new InvalidOperationException("That profile is no longer in Switchboard.");
        }

        var candidate = new AccountProfile
        {
            Id = account.Profile.Id,
            DisplayName = account.Profile.DisplayName,
            SteamLoginName = steamLoginName,
            AccentHex = account.Profile.AccentHex,
            CreatedUtc = account.Profile.CreatedUtc,
            LastUsedUtc = account.Profile.LastUsedUtc
        };
        AccountValidator.Normalize(candidate);
        var validation = AccountValidator.Validate(
            candidate,
            _state.Accounts.Where(item => item.Id != account.Id));
        if (validation is not null)
        {
            throw new ArgumentException(validation, nameof(steamLoginName));
        }

        var previousLogin = account.Profile.SteamLoginName;
        account.Profile.SteamLoginName = candidate.SteamLoginName;
        account.NotifyProfileChanged();
        try
        {
            await SaveAsync(cancellationToken);
        }
        catch
        {
            account.Profile.SteamLoginName = previousLogin;
            account.NotifyProfileChanged();
            throw;
        }

        foreach (var notification in Notifications.Where(
                     item => item.AccountId == account.Id))
        {
            notification.UpdateAccountLoginName(account.SteamLoginName);
        }

        NotifyCurrentPlayAccountChanged();
        StatusMessage =
            $"{account.DisplayName} is now linked to Steam login {account.SteamLoginName}";
    }

    public ChatNotificationViewModel AddNotification(
        AccountViewModel account,
        ChatNotificationPayload payload,
        Action? reportClicked = null,
        Action? reportClosed = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(payload);

        var preview = ShowNotificationPreviews
            ? payload.Preview
            : "New Steam Chat message";
        var notification = payload.ReplacesUnreadFallback
            ? Notifications.FirstOrDefault(item =>
                item.AccountId == account.Id && item.IsUnreadFallback)
            : null;
        notification ??= !string.IsNullOrWhiteSpace(payload.ReplacementTag)
            ? Notifications.FirstOrDefault(item =>
                item.AccountId == account.Id
                && string.Equals(
                    item.ReplacementTag,
                    payload.ReplacementTag,
                    StringComparison.Ordinal))
            : null;
        if (notification is null)
        {
            notification = new ChatNotificationViewModel(
                account.Id,
                account.DisplayName,
                account.SteamLoginName,
                payload.SteamTitle,
                preview,
                payload.ReceivedUtc,
                payload.ReplacementTag,
                payload.IsUnreadFallback,
                reportClicked,
                reportClosed);
            Notifications.Insert(0, notification);
        }
        else
        {
            notification.Replace(
                payload.SteamTitle,
                preview,
                payload.ReceivedUtc,
                payload.ReplacementTag,
                payload.IsUnreadFallback,
                reportClicked,
                reportClosed);
            var existingIndex = Notifications.IndexOf(notification);
            if (existingIndex > 0)
            {
                Notifications.Move(existingIndex, 0);
            }
        }

        while (Notifications.Count > MaximumNotificationHistory)
        {
            var expired = Notifications[^1];
            expired.CloseLifecycle();
            Notifications.RemoveAt(Notifications.Count - 1);
        }

        RaiseNotificationStateChanged();
        return notification;
    }

    public void ClearNotifications()
    {
        foreach (var notification in Notifications)
        {
            notification.CloseLifecycle();
        }

        Notifications.Clear();
        RaiseNotificationStateChanged();
    }

    public void ClearNotifications(Guid accountId) =>
        RemoveNotificationsForAccount(accountId);

    public void MarkNotificationsRead(Guid accountId)
    {
        foreach (var notification in Notifications.Where(
                     item => item.AccountId == accountId && item.IsUnread))
        {
            notification.MarkRead();
        }

        RaiseNotificationStateChanged();
    }

    public void NotifyCurrentPlayAccountChanged()
    {
        OnPropertyChanged(nameof(SelectedPlayAccountStatus));
    }

    public async Task MarkAccountDeletionPendingAsync(
        AccountViewModel account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!_state.Accounts.Any(item => item.Id == account.Id))
        {
            throw new InvalidOperationException("That account is no longer in Switchboard.");
        }

        var browserCleanupWasPending =
            _state.PendingBrowserProfileDeletionIds.Contains(account.Id);
        var previousNotificationHistoryClear =
            _state.PendingWindowsNotificationHistoryClear;
        var previousNotificationCleanupIds =
            _state.PendingWindowsNotificationAccountCleanupIds.ToList();
        var previousNotificationCleanupRequestId =
            _state.PendingWindowsNotificationCleanupRequestId;
        var notificationCleanupChanged =
            MarkWindowsNotificationCleanupPending(account.Id);
        var deletionCleanupRequestId =
            _state.PendingWindowsNotificationCleanupRequestId;
        if (browserCleanupWasPending && !notificationCleanupChanged)
        {
            return;
        }

        if (!browserCleanupWasPending)
        {
            _state.PendingBrowserProfileDeletionIds.Add(account.Id);
            OnPropertyChanged(nameof(IsSelectedAccountPendingDeletion));
        }

        try
        {
            await SaveAsync(cancellationToken);
            account.ConnectionState = ChatConnectionState.Failed;
            StatusMessage = $"Cleaning up {account.DisplayName}'s local sign-in data…";
        }
        catch
        {
            if (!browserCleanupWasPending)
            {
                _state.PendingBrowserProfileDeletionIds.Remove(account.Id);
            }

            if (_state.PendingWindowsNotificationCleanupRequestId
                == deletionCleanupRequestId)
            {
                _state.PendingWindowsNotificationHistoryClear =
                    previousNotificationHistoryClear;
                _state.PendingWindowsNotificationAccountCleanupIds =
                    previousNotificationCleanupIds;
                _state.PendingWindowsNotificationCleanupRequestId =
                    previousNotificationCleanupRequestId;
            }
            OnPropertyChanged(nameof(IsSelectedAccountPendingDeletion));
            throw;
        }
    }

    public bool MarkWindowsNotificationCleanupPending(Guid? accountId)
    {
        if (accountId is null)
        {
            _state.PendingWindowsNotificationHistoryClear = true;
            _state.PendingWindowsNotificationAccountCleanupIds.Clear();
            _state.PendingWindowsNotificationCleanupRequestId = Guid.NewGuid();
            return true;
        }

        if (accountId == Guid.Empty)
        {
            throw new ArgumentException(
                "A notification cleanup account identifier cannot be empty.",
                nameof(accountId));
        }

        if (_state.PendingWindowsNotificationHistoryClear
            || _state.PendingWindowsNotificationAccountCleanupIds.Contains(
                accountId.Value))
        {
            _state.PendingWindowsNotificationCleanupRequestId = Guid.NewGuid();
            return true;
        }

        if (_state.PendingWindowsNotificationAccountCleanupIds.Count
            >= AccountValidator.MaximumAccountProfiles)
        {
            _state.PendingWindowsNotificationHistoryClear = true;
            _state.PendingWindowsNotificationAccountCleanupIds.Clear();
            _state.PendingWindowsNotificationCleanupRequestId = Guid.NewGuid();
            return true;
        }

        _state.PendingWindowsNotificationAccountCleanupIds.Add(accountId.Value);
        _state.PendingWindowsNotificationCleanupRequestId = Guid.NewGuid();
        return true;
    }

    public bool RenewWindowsNotificationCleanupRequest()
    {
        if (!_state.PendingWindowsNotificationHistoryClear
            && _state.PendingWindowsNotificationAccountCleanupIds.Count == 0)
        {
            return false;
        }

        _state.PendingWindowsNotificationCleanupRequestId = Guid.NewGuid();
        return true;
    }

    public async Task CompleteWindowsNotificationCleanupAsync(
        bool removedAll,
        IReadOnlyCollection<Guid> accountIds,
        Guid cleanupRequestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        if (cleanupRequestId == Guid.Empty)
        {
            throw new ArgumentException(
                "A notification cleanup generation cannot be empty.",
                nameof(cleanupRequestId));
        }

        if (_state.PendingWindowsNotificationCleanupRequestId
            != cleanupRequestId)
        {
            return;
        }

        SynchronizeStateForSave();
        var candidate = CreateStateSnapshot(_state);
        if (removedAll)
        {
            candidate.PendingWindowsNotificationHistoryClear = false;
            foreach (var accountId in accountIds)
            {
                candidate.PendingWindowsNotificationAccountCleanupIds.Remove(accountId);
            }
        }
        else
        {
            foreach (var accountId in accountIds)
            {
                candidate.PendingWindowsNotificationAccountCleanupIds.Remove(accountId);
            }
        }

        if (!candidate.PendingWindowsNotificationHistoryClear
            && candidate.PendingWindowsNotificationAccountCleanupIds.Count == 0)
        {
            candidate.PendingWindowsNotificationCleanupRequestId = null;
        }

        if (_state.PendingWindowsNotificationHistoryClear
                == candidate.PendingWindowsNotificationHistoryClear
            && _state.PendingWindowsNotificationAccountCleanupIds.SequenceEqual(
                candidate.PendingWindowsNotificationAccountCleanupIds)
            && _state.PendingWindowsNotificationCleanupRequestId
                == candidate.PendingWindowsNotificationCleanupRequestId)
        {
            return;
        }

        await _stateStore.SaveAsync(candidate, cancellationToken);
        if (_state.PendingWindowsNotificationCleanupRequestId
            == cleanupRequestId)
        {
            _state.PendingWindowsNotificationHistoryClear =
                candidate.PendingWindowsNotificationHistoryClear;
            _state.PendingWindowsNotificationAccountCleanupIds =
                candidate.PendingWindowsNotificationAccountCleanupIds;
            _state.PendingWindowsNotificationCleanupRequestId =
                candidate.PendingWindowsNotificationCleanupRequestId;
        }
        else
        {
            // The completed generation reached disk first, but a newer privacy
            // request arrived while that atomic save was in flight. Reassert
            // the newer immutable snapshot before returning.
            SynchronizeStateForSave();
            await _stateStore.SaveAsync(
                CreateStateSnapshot(_state),
                cancellationToken);
        }
    }

    public async Task RefreshGamesAsync(CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var steamRoot = _steamInstallation.FindSteamRoot(SteamExecutablePath);
        IReadOnlyList<InstalledGame> games = [];
        if (string.IsNullOrWhiteSpace(steamRoot))
        {
            games = [];
        }
        else
        {
            games = await _steamLibrary.LoadInstalledGamesAsync(
                steamRoot,
                cancellationToken);
        }

        if (generation != Volatile.Read(ref _refreshGeneration))
        {
            return;
        }

        Games.Clear();
        foreach (var game in games)
        {
            Games.Add(game);
        }

        GamesView.Refresh();
        RaiseCollectionStateChanged();
    }

    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        SynchronizeStateForSave();
        return _stateStore.SaveAsync(
            CreateStateSnapshot(_state),
            cancellationToken);
    }

    private void SynchronizeStateForSave()
    {
        _state.Settings.SteamExecutablePath = SteamExecutablePath;
        _state.LastSelectedAccountId = SelectedAccount?.Id;
        _state.LastPlayAccountId = SelectedPlayAccount?.Id;
    }

    private static PersistedState CreateStateSnapshot(PersistedState state) =>
        new()
        {
            SchemaVersion = state.SchemaVersion,
            Accounts = state.Accounts.Select(account => new AccountProfile
            {
                Id = account.Id,
                DisplayName = account.DisplayName,
                SteamLoginName = account.SteamLoginName,
                AccentHex = account.AccentHex,
                CreatedUtc = account.CreatedUtc,
                LastUsedUtc = account.LastUsedUtc
            }).ToList(),
            Settings = new AppSettings
            {
                SteamExecutablePath = state.Settings.SteamExecutablePath,
                LaunchAtWindowsSignIn = state.Settings.LaunchAtWindowsSignIn,
                ShowNotificationPreviews = state.Settings.ShowNotificationPreviews,
                EnableWindowsNotifications = state.Settings.EnableWindowsNotifications,
                KeepAllChatsLive = state.Settings.KeepAllChatsLive
            },
            LastSelectedAccountId = state.LastSelectedAccountId,
            LastPlayAccountId = state.LastPlayAccountId,
            PendingBrowserProfileDeletionIds =
                state.PendingBrowserProfileDeletionIds.ToList(),
            PendingWindowsNotificationHistoryClear =
                state.PendingWindowsNotificationHistoryClear,
            PendingWindowsNotificationAccountCleanupIds =
                state.PendingWindowsNotificationAccountCleanupIds.ToList(),
            PendingWindowsNotificationCleanupRequestId =
                state.PendingWindowsNotificationCleanupRequestId
        };

    private bool FilterGame(object item)
    {
        if (item is not InstalledGame game)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(GameSearch)
            || game.Name.Contains(GameSearch.Trim(), StringComparison.CurrentCultureIgnoreCase)
            || game.AppId.ToString().Contains(GameSearch.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void RaiseCollectionStateChanged()
    {
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(DataSummary));
    }

    private void RemoveNotificationsForAccount(Guid accountId)
    {
        for (var index = Notifications.Count - 1; index >= 0; index--)
        {
            if (Notifications[index].AccountId == accountId)
            {
                Notifications[index].CloseLifecycle();
                Notifications.RemoveAt(index);
            }
        }

        RaiseNotificationStateChanged();
    }

    private void RaiseNotificationStateChanged()
    {
        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(HasUnreadNotifications));
        OnPropertyChanged(nameof(NotificationCount));
        OnPropertyChanged(nameof(NotificationCountLabel));
    }

    private void RaiseSettingsStateChanged()
    {
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(KeepAllChatsLive));
        OnPropertyChanged(nameof(ShowNotificationPreviews));
        OnPropertyChanged(nameof(EnableWindowsNotifications));
    }
}
