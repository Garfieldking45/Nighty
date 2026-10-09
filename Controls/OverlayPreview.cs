using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nighty.Models;

namespace Nighty.Controls;

/// <summary>
/// A miniature of the primary display. Enabled overlays appear as draggable tiles; dragging updates the same
/// X / Y percentages the position steppers use.
/// </summary>
public class OverlayPreview : Border
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(ObservableCollection<OverlayConfig>), typeof(OverlayPreview),
        new PropertyMetadata(null, (d, e) => ((OverlayPreview)d).OnItemsChanged((ObservableCollection<OverlayConfig>?)e.OldValue, (ObservableCollection<OverlayConfig>?)e.NewValue)));
    public ObservableCollection<OverlayConfig>? Items { get => (ObservableCollection<OverlayConfig>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Dictionary<OverlayConfig, Border> _tiles = new();
    private OverlayConfig? _drag;
    private Point _grab;

    public OverlayPreview()
    {
        Background = (Brush)Application.Current.FindResource("Panel2Brush");
        BorderBrush = (Brush)Application.Current.FindResource("BorderStrongBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        ClipToBounds = true;
        Child = _canvas;
        SizeChanged += (_, e) =>
        {
            // keep a 16:9 aspect ratio
            double h = e.NewSize.Width * 9.0 / 16.0;
            if (double.IsNaN(Height) || Math.Abs(Height - h) > 0.5) Height = h;
            LayoutAll();
        };
    }

    private void OnItemsChanged(ObservableCollection<OverlayConfig>? old, ObservableCollection<OverlayConfig>? now)
    {
        if (old != null) old.CollectionChanged -= OnCollectionChanged;
        foreach (var t in _tiles.Keys) t.PropertyChanged -= OnItemChanged;
        _tiles.Clear(); _canvas.Children.Clear();
        if (now == null) return;
        now.CollectionChanged += OnCollectionChanged;
        foreach (var i in now) Add(i);
        LayoutAll();
    }

    private void OnCollectionChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (OverlayConfig i in e.OldItems) Remove(i);
        if (e.NewItems != null) foreach (OverlayConfig i in e.NewItems) Add(i);
        LayoutAll();
    }

    private void Add(OverlayConfig cfg)
    {
        var tile = new Border
        {
            CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Cursor = Cursors.SizeAll,
            Background = (Brush)Application.Current.FindResource("AccentSoftBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("AccentBrush"),
            Child = new TextBlock { Text = cfg.Title, FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
            ToolTip = cfg.Title + " — drag to move",
        };
        tile.MouseLeftButtonDown += (_, e) => { _drag = cfg; _grab = e.GetPosition(tile); tile.CaptureMouse(); e.Handled = true; };
        tile.MouseMove += (_, e) => { if (_drag == cfg) Drag(cfg, tile, e.GetPosition(_canvas)); };
        tile.MouseLeftButtonUp += (_, _) => { _drag = null; tile.ReleaseMouseCapture(); };
        _tiles[cfg] = tile;
        _canvas.Children.Add(tile);
        cfg.PropertyChanged += OnItemChanged;
    }

    private void Remove(OverlayConfig cfg)
    {
        if (_tiles.Remove(cfg, out var tile)) _canvas.Children.Remove(tile);
        cfg.PropertyChanged -= OnItemChanged;
    }

    private void OnItemChanged(object? s, PropertyChangedEventArgs e)
    {
        if (s is OverlayConfig c && _tiles.TryGetValue(c, out var tile) && tile.Child is TextBlock tb) tb.Text = c.Title;
        LayoutAll();
    }

    private (double W, double H) TileSize(OverlayConfig c)
    {
        double sw = SystemParameters.PrimaryScreenWidth, k = Math.Max(1, ActualWidth - 2) / sw;
        var (w, h) = c.Kind switch
        {
            OverlayKind.Wasd => (122.0, 84.0), OverlayKind.Mouse => (146.0, 46.0), OverlayKind.Key => (50.0, 46.0),
            OverlayKind.Ping => (130.0, 34.0), _ => (112.0, 34.0),
        };
        return (Math.Max(22, w * c.Scale * k), Math.Max(12, h * c.Scale * k));
    }

    private void LayoutAll()
    {
        if (Items == null) return;
        double cw = Math.Max(1, ActualWidth - 2), ch = Math.Max(1, ActualHeight - 2);
        foreach (var (cfg, tile) in _tiles)
        {
            tile.Visibility = cfg.Enabled ? Visibility.Visible : Visibility.Collapsed;
            var (w, h) = TileSize(cfg);
            tile.Width = w; tile.Height = h;
            Canvas.SetLeft(tile, cfg.X / 100.0 * Math.Max(0, cw - w));
            Canvas.SetTop(tile, cfg.Y / 100.0 * Math.Max(0, ch - h));
            tile.Opacity = Math.Max(0.5, cfg.Opacity);
        }
    }

    private void Drag(OverlayConfig cfg, Border tile, Point p)
    {
        double cw = Math.Max(1, ActualWidth - 2), ch = Math.Max(1, ActualHeight - 2);
        var (w, h) = TileSize(cfg);
        double left = Math.Clamp(p.X - _grab.X, 0, Math.Max(0, cw - w));
        double top = Math.Clamp(p.Y - _grab.Y, 0, Math.Max(0, ch - h));
        cfg.X = cw - w <= 0 ? 0 : left / (cw - w) * 100;
        cfg.Y = ch - h <= 0 ? 0 : top / (ch - h) * 100;
    }
}
