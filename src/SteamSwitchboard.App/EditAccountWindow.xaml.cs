using System.Windows;
using System.Windows.Controls;
using SteamSwitchboard.Models;
using SteamSwitchboard.Services;

namespace SteamSwitchboard;

public partial class EditAccountWindow : Window
{
    private readonly AccountProfile _account;
    private readonly IReadOnlyList<AccountProfile> _otherAccounts;

    public EditAccountWindow(
        AccountProfile account,
        IEnumerable<AccountProfile> existingAccounts)
    {
        InitializeComponent();
        WindowSizing.ClampToCurrentWorkArea(this);
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _otherAccounts = existingAccounts?
            .Where(item => item.Id != account.Id)
            .ToArray()
            ?? throw new ArgumentNullException(nameof(existingAccounts));
        DisplayNameTextBox.Text = account.DisplayName;
        AccountIdentityText.Text =
            $"Steam login name: {account.SteamLoginName} (unchanged here)";
        Loaded += (_, _) =>
        {
            DisplayNameTextBox.Focus();
            DisplayNameTextBox.SelectAll();
        };
    }

    public string? ResultName { get; private set; }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (ValidationText is not null)
        {
            ValidationText.Text = string.Empty;
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var candidate = new AccountProfile
        {
            Id = _account.Id,
            DisplayName = DisplayNameTextBox.Text,
            SteamLoginName = _account.SteamLoginName,
            AccentHex = _account.AccentHex,
            CreatedUtc = _account.CreatedUtc,
            LastUsedUtc = _account.LastUsedUtc
        };
        AccountValidator.Normalize(candidate);
        var validation = AccountValidator.Validate(candidate, _otherAccounts);
        if (validation is not null)
        {
            ValidationText.Text = validation;
            return;
        }

        ResultName = candidate.DisplayName;
        DialogResult = true;
    }
}
