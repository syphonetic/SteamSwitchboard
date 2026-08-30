using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using SteamSwitchboard.Models;
using SteamSwitchboard.Services;

namespace SteamSwitchboard.ViewModels;

public enum AppSection
{
    Chats,
    Games,
    Settings
}

public sealed class MainViewModel : ObservableObject
{
    private readonly StateStore _stateStore;
    private readonly SteamInstallationService _steamInstallation;
    private readonly SteamLibraryService _steamLibrary;
    private PersistedState _state = new();
    private AccountViewModel? _selectedAccount;
    private AppSection _selectedSection = AppSection.Chats;
    private string _gameSearch = string.Empty;
    private string _statusMessage = "Getting things ready…";
    private bool _isBusy;
    private string? _steamExecutablePath;
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

    public ICollectionView GamesView { get; }

    public AccountViewModel? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value))
            {
                return;
            }

            if (value is not null)
            {
                value.Profile.LastUsedUtc = DateTimeOffset.UtcNow;
                value.UnreadCount = 0;
                _state.LastSelectedAccountId = value.Id;
            }

            OnPropertyChanged(nameof(HasSelectedAccount));
            OnPropertyChanged(nameof(IsSelectedAccountPendingDeletion));
        }
    }

    public bool HasSelectedAccount => SelectedAccount is not null;

    public bool IsSelectedAccountPendingDeletion =>
        SelectedAccount is { } selected
        && IsAccountDeletionPending(selected.Id);

    public bool HasAccounts => Accounts.Count > 0;

    public bool HasGames => Games.Count > 0;

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

    public string DataSummary =>
        $"{Accounts.Count} account{(Accounts.Count == 1 ? string.Empty : "s")} · {Games.Count} installed game{(Games.Count == 1 ? string.Empty : "s")}";

    public AppSettings Settings => _state.Settings;

    public string? ValidateSteamExecutableCandidate(string? candidate) =>
        _steamInstallation.ValidateSteamExecutableCandidate(candidate);

    public IReadOnlyList<AccountViewModel> AccountsPendingBrowserProfileDeletion =>
        Accounts
            .Where(account => IsAccountDeletionPending(account.Id))
            .ToArray();

    public bool IsAccountDeletionPending(Guid accountId) =>
        _state.PendingBrowserProfileDeletionIds.Contains(accountId);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            try
            {
                _state = await _stateStore.LoadAsync(cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                _state = new PersistedState();
                StatusMessage = exception.Message;
            }

            Accounts.Clear();
            foreach (var account in _state.Accounts.OrderByDescending(item => item.LastUsedUtc))
            {
                Accounts.Add(new AccountViewModel(account));
            }

            SelectedAccount = Accounts.FirstOrDefault(account => account.Id == _state.LastSelectedAccountId)
                ?? Accounts.FirstOrDefault();

            SteamExecutablePath = _steamInstallation.FindSteamExecutable(
                _state.Settings.SteamExecutablePath);
            await RefreshGamesAsync(cancellationToken);
            StatusMessage = HasAccounts
                ? _state.PendingBrowserProfileDeletionIds.Count > 0
                    ? "Finishing a previously requested account cleanup…"
                    : "Ready"
                : "Add your first account to begin";
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
        _state.Accounts.Add(profile);
        _state.LastSelectedAccountId = profile.Id;
        var viewModel = new AccountViewModel(profile);
        Accounts.Insert(0, viewModel);
        SelectedAccount = viewModel;
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

        _state.LastSelectedAccountId = SelectedAccount?.Id;
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
            OnPropertyChanged(nameof(IsSelectedAccountPendingDeletion));
            RaiseCollectionStateChanged();
            throw;
        }

        RaiseCollectionStateChanged();
        StatusMessage = $"{account.DisplayName} was forgotten on this PC.";
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

        if (_state.PendingBrowserProfileDeletionIds.Contains(account.Id))
        {
            return;
        }

        _state.PendingBrowserProfileDeletionIds.Add(account.Id);
        OnPropertyChanged(nameof(IsSelectedAccountPendingDeletion));

        try
        {
            await SaveAsync(cancellationToken);
            account.ConnectionState = ChatConnectionState.Failed;
            StatusMessage = $"Cleaning up {account.DisplayName}'s local sign-in data…";
        }
        catch
        {
            _state.PendingBrowserProfileDeletionIds.Remove(account.Id);
            OnPropertyChanged(nameof(IsSelectedAccountPendingDeletion));
            throw;
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
        _state.Settings.SteamExecutablePath = SteamExecutablePath;
        _state.LastSelectedAccountId = SelectedAccount?.Id;
        return _stateStore.SaveAsync(_state, cancellationToken);
    }

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
}
