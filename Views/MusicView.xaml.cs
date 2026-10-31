using System.Windows.Controls;
using System.Windows.Input;
using Nighty.ViewModels;

namespace Nighty.Views;

public partial class MusicView : UserControl
{
    public MusicView() => InitializeComponent();

    private void PosSlider_Down(object sender, MouseButtonEventArgs e) => (DataContext as MusicViewModel)?.BeginSeek();
    private void PosSlider_Up(object sender, MouseButtonEventArgs e) => (DataContext as MusicViewModel)?.EndSeek(PosSlider.Value);
}
