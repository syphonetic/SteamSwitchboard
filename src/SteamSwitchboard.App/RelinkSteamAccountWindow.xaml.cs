using System.Windows;
using System.Windows.Controls;
using SteamSwitchboard.Models;
using SteamSwitchboard.Services;

namespace SteamSwitchboard;

public partial class RelinkSteamAccountWindow : Window
{
    public RelinkSteamAccountWindow(
        AccountProfile profile,
        IEnumerable<SteamClientAccount> detectedAccounts)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(detectedAccounts);

        InitializeComponent();
        WindowSizing.ClampToCurrentWorkArea(this);
        ProfileIdentityText.Text =
            $"Profile nickname: {profile.DisplayName}  •  currently linked: {profile.SteamLoginName}";
        var choices = detectedAccounts
            .Select(account => new SteamLoginChoice(
                account,
                $"{account.AccountName}  —  {account.PersonaName}"))
            .OrderBy(choice => choice.Account.AccountName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        DetectedAccountsComboBox.ItemsSource = choices;
        DetectedAccountsComboBox.SelectedItem = choices.FirstOrDefault(choice =>
            string.Equals(
                choice.Account.AccountName,
                profile.SteamLoginName,
                StringComparison.OrdinalIgnoreCase));
    }

    public string? ResultLoginName { get; private set; }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RelinkButton is not null)
        {
            RelinkButton.IsEnabled =
                DetectedAccountsComboBox.SelectedItem is SteamLoginChoice;
        }

        if (ValidationText is not null)
        {
            ValidationText.Text = string.Empty;
        }
    }

    private void OnRelinkClicked(object sender, RoutedEventArgs e)
    {
        if (DetectedAccountsComboBox.SelectedItem is not SteamLoginChoice choice
            || !AccountValidator.IsSafeSteamLoginName(choice.Account.AccountName))
        {
            ValidationText.Text = "Choose a valid Steam login.";
            return;
        }

        ResultLoginName = choice.Account.AccountName;
        DialogResult = true;
    }

    private sealed record SteamLoginChoice(
        SteamClientAccount Account,
        string DisplayText);
}
