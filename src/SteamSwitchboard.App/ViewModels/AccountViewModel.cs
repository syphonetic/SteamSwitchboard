using SteamSwitchboard.Models;

namespace SteamSwitchboard.ViewModels;

public enum ChatConnectionState
{
    Starting,
    SignInRequired,
    Ready,
    Reconnecting,
    Failed
}

public sealed class AccountViewModel : ObservableObject
{
    private ChatConnectionState _connectionState = ChatConnectionState.Starting;
    private int _unreadCount;
    private bool _isCurrentPlayAccount;

    public AccountViewModel(AccountProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public AccountProfile Profile { get; }

    public Guid Id => Profile.Id;

    public string DisplayName => Profile.DisplayName;

    public string SteamLoginName => Profile.SteamLoginName;

    public string AccentHex => Profile.AccentHex;

    public string Initial => string.IsNullOrWhiteSpace(DisplayName)
        ? "?"
        : DisplayName.Trim()[0].ToString().ToUpperInvariant();

    public ChatConnectionState ConnectionState
    {
        get => _connectionState;
        set
        {
            if (SetProperty(ref _connectionState, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsReady));
            }
        }
    }

    public string StatusText => ConnectionState switch
    {
        ChatConnectionState.Starting => "Starting chat",
        ChatConnectionState.SignInRequired => "Sign in needed",
        ChatConnectionState.Ready => "Steam page ready — verify account",
        ChatConnectionState.Reconnecting => "Reconnecting",
        ChatConnectionState.Failed => "Needs attention",
        _ => "Unknown"
    };

    public bool IsReady => ConnectionState == ChatConnectionState.Ready;

    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            var normalized = Math.Max(0, value);
            if (SetProperty(ref _unreadCount, normalized))
            {
                OnPropertyChanged(nameof(UnreadLabel));
                OnPropertyChanged(nameof(HasUnread));
            }
        }
    }

    public bool HasUnread => UnreadCount > 0;

    public string UnreadLabel => UnreadCount > 99 ? "99+" : UnreadCount.ToString();

    public bool IsCurrentPlayAccount
    {
        get => _isCurrentPlayAccount;
        set => SetProperty(ref _isCurrentPlayAccount, value);
    }
}
