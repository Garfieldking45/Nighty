using System.Windows.Controls;

namespace Nighty.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OwnerSignIn_Click(object sender, System.Windows.RoutedEventArgs e) => DoSignIn();

    private void OwnerPassword_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) DoSignIn();
    }

    private void DoSignIn()
    {
        if (DataContext is not Nighty.ViewModels.SettingsViewModel vm) return;
        vm.SignIn(OwnerPassword.Password);
        OwnerPassword.Clear();
    }

    private void OwnerSignOut_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is Nighty.ViewModels.SettingsViewModel vm) vm.SignOut();
    }
}
