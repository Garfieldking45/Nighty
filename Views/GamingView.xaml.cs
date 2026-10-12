using System.Windows.Controls;
using Nighty.ViewModels;

namespace Nighty.Views;

public partial class GamingView : UserControl
{
    public GamingView()
    {
        InitializeComponent();
        // Live measurements run only while this page is on screen.
        Loaded += (_, _) => (DataContext as GamingViewModel)?.SetVisible(true);
        Unloaded += (_, _) => (DataContext as GamingViewModel)?.SetVisible(false);
    }
}
