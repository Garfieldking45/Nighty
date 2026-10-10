using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nighty.Services;

namespace Nighty.Controls;

/// <summary>
/// The optional picture behind the window. Static pictures are shown as-is; GIFs are decoded into frames and played with
/// their own delays (WPF's Image does not animate GIFs). Reads its settings from <see cref="Svc"/> and refreshes on change.
/// </summary>
public sealed class BackdropImage : Grid
{
    private readonly Image _image = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer _timer = new();
    private List<(BitmapSource Frame, TimeSpan Delay)> _frames = new();
    private int _index;
    private string _loadedPath = "";

    public BackdropImage()
    {
        IsHitTestVisible = false;
        Children.Add(_image);
        _timer.Tick += (_, _) => NextFrame();
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>Call whenever the picture, fit or strength changes.</summary>
    public void Refresh()
    {
        var g = Svc.S.General;
        var path = g.BackgroundImage;
        _image.Stretch = g.BackgroundFit ? Stretch.Uniform : Stretch.UniformToFill;
        Opacity = g.BackgroundStrength;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _timer.Stop(); _frames = new(); _loadedPath = ""; _image.Source = null; Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        if (path == _loadedPath && _frames.Count > 0) return;
        _loadedPath = path;
        _timer.Stop();
        _frames = Load(path);
        _index = 0;
        if (_frames.Count == 0) { _image.Source = null; return; }
        _image.Source = _frames[0].Frame;
        if (_frames.Count > 1) { _timer.Interval = _frames[0].Delay; _timer.Start(); }
    }

    private void NextFrame()
    {
        if (_frames.Count < 2) { _timer.Stop(); return; }
        _index = (_index + 1) % _frames.Count;
        _image.Source = _frames[_index].Frame;
        _timer.Interval = _frames[_index].Delay;
    }

    /// <summary>Decodes a picture. GIF frames are composited onto a full-size canvas so partial frames render correctly.</summary>
    public static List<(BitmapSource, TimeSpan)> Load(string path)
    {
        var list = new List<(BitmapSource, TimeSpan)>();
        try
        {
            using var fs = File.OpenRead(path);
            var ms = new MemoryStream();
            fs.CopyTo(ms); ms.Position = 0;
            if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
            {
                var dec = new GifBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                int w = dec.Frames[0].PixelWidth, h = dec.Frames[0].PixelHeight;
                if (dec.Frames.Count > 1 && (long)w * h * dec.Frames.Count < 400_000_000)
                {
                    var delays = ReadGifDelays(ms.ToArray(), dec.Frames.Count);
                    var canvas = new DrawingVisual();
                    for (int i = 0; i < dec.Frames.Count; i++)
                    {
                        var f = dec.Frames[i];
                        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                        var dv = new DrawingVisual();
                        using (var dc = dv.RenderOpen())
                        {
                            if (list.Count > 0) dc.DrawImage(list[^1].Item1, new Rect(0, 0, w, h));   // previous frame underneath
                            dc.DrawImage(f, new Rect(0, 0, f.PixelWidth, f.PixelHeight));
                        }
                        rtb.Render(dv); rtb.Freeze();
                        list.Add((rtb, TimeSpan.FromMilliseconds(Math.Max(40, delays[i]))));
                    }
                    return list;
                }
            }
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            list.Add((bmp, TimeSpan.FromMilliseconds(100)));
        }
        catch (Exception ex) { Log.Warn("Background image could not be loaded", ex); list.Clear(); }
        return list;
    }

    /// <summary>Reads each frame's delay (graphic control extension, 1/100 s) straight from the GIF bytes.</summary>
    private static int[] ReadGifDelays(byte[] d, int count)
    {
        var res = new List<int>();
        for (int i = 0; i + 7 < d.Length && res.Count < count; i++)
            if (d[i] == 0x21 && d[i + 1] == 0xF9 && d[i + 2] == 0x04)
                res.Add((d[i + 4] | d[i + 5] << 8) * 10);
        while (res.Count < count) res.Add(100);
        return res.ToArray();
    }
}
