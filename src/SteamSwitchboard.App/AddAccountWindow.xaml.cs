using System.Windows;
using System.Windows.Controls;
using SteamSwitchboard.Models;
using SteamSwitchboard.Services;

namespace SteamSwitchboard;

public partial class AddAccountWindow : Window
{
    private readonly IReadOnlyList<AccountProfile> _existingAccounts;

    public AddAccountWindow(IEnumerable<AccountProfile> existingAccounts)
    {
        InitializeComponent();
        _existingAccounts = existingAccounts?.ToArray()
            ?? throw new ArgumentNullException(nameof(existingAccounts));
        Loaded += (_, _) => DisplayNameTextBox.Focus();
    }

    public AccountProfile? Result { get; private set; }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        ValidationText.Text = string.Empty;
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var selectedAccent = FindVisualChildren<RadioButton>(this)
            .FirstOrDefault(button => button.IsChecked == true && button.Tag is string)
            ?.Tag as string
            ?? "#66C0F4";

        var account = new AccountProfile
        {
            DisplayName = DisplayNameTextBox.Text,
            SteamLoginName = SteamLoginNameTextBox.Text,
            AccentHex = selectedAccent
        };

        AccountValidator.Normalize(account);
        var validation = AccountValidator.Validate(account, _existingAccounts);
        if (validation is not null)
        {
            ValidationText.Text = validation;
            return;
        }

        Result = account;
        DialogResult = true;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
