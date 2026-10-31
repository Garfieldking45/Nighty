using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nighty.Services;
using Nighty.ViewModels;

namespace Nighty.Views;

public partial class EditorView : UserControl
{
    private readonly DispatcherTimer _stopAt = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private EditItem? _playing;
    private EditorViewModel? _vm;

    public EditorView()
    {
        InitializeComponent();
        _stopAt.Tick += (_, _) => { if (_playing != null && Player.Position.TotalSeconds >= _playing.End) StopPlayback(); };
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) { _vm.Previewed -= Show; _vm.PreviewPlayRequested -= Play; }
            _vm = DataContext as EditorViewModel;
            if (_vm != null) { _vm.Previewed += Show; _vm.PreviewPlayRequested += Play; }
        };
        Unloaded += (_, _) => StopPlayback();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void StopPlayback()
    {
        _stopAt.Stop(); _playing = null;
        try { Player.Pause(); } catch { }
    }

    private void Show(EditItem? it)
    {
        StopPlayback();
        Still.Visibility = Caption.Visibility = Visibility.Collapsed;
        Still.Source = null;
        try { Player.Close(); Player.Source = null; } catch { }
        if (it == null) return;
        switch (it.Kind)
        {
            case EditKind.Picture:
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.UriSource = new Uri(it.Path); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 960; bi.EndInit(); bi.Freeze();
                    Still.Source = bi; Still.Visibility = Visibility.Visible;
                }
                catch { }
                break;
            case EditKind.Title:
                Caption.Text = it.Text; Caption.Visibility = Visibility.Visible;
                break;
            case EditKind.Video:
                Player.Source = new Uri(it.Path);
                Player.Play(); Player.Position = TimeSpan.FromSeconds(it.Start); Player.Pause();   // show the first frame of the trimmed clip
                break;
        }
    }

    private void Play(EditItem it)
    {
        if (it.Kind is EditKind.Picture or EditKind.Title) { Show(it); return; }
        StopPlayback();
        Still.Visibility = Caption.Visibility = Visibility.Collapsed;
        Player.Source = new Uri(it.Path);
        Player.Volume = it.Volume;
        Player.Position = TimeSpan.FromSeconds(it.Start);
        Player.Play();
        _playing = it;
        _stopAt.Start();
    }
}
