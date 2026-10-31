using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nighty.Mvvm;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Nighty.Services;

public enum EditKind { Video, Picture, Title, Music }

/// <summary>One thing on the timeline: a video, a picture held for a few seconds, a title card, or a music track under the picture.</summary>
public sealed class EditItem : ObservableObject
{
    private double _start, _end, _volume = 1, _delay;
    private string _text = "";
    public Guid Id { get; } = Guid.NewGuid();
    public EditKind Kind { get; init; }
    public string Path { get; init; } = "";
    /// <summary>Length of the original file in seconds (pictures and titles can be any length).</summary>
    public double Source { get; init; }
    public string Text { get => _text; set { if (Set(ref _text, value)) OnPropertyChanged(nameof(Label)); } }
    /// <summary>Seconds cut from the start / where the clip ends, in seconds into the original.</summary>
    public double Start { get => _start; set { if (Set(ref _start, Math.Clamp(Math.Round(value, 2), 0, Math.Max(0, _end - 0.1)))) Changed(); } }
    public double End { get => _end; set { if (Set(ref _end, Math.Clamp(Math.Round(value, 2), _start + 0.1, Max))) Changed(); } }
    public double Volume { get => _volume; set => Set(ref _volume, Math.Clamp(value, 0, 1)); }
    /// <summary>Music only: seconds into the video where the track begins.</summary>
    public double Delay { get => _delay; set => Set(ref _delay, Math.Max(0, Math.Round(value, 2))); }
    public double Length => Math.Max(0, _end - _start);
    public double Max => Kind is EditKind.Picture or EditKind.Title ? 60 : Math.Max(0.1, Source);
    public string Label => Kind == EditKind.Title ? (string.IsNullOrWhiteSpace(Text) ? "Title" : "Title: " + Text) : System.IO.Path.GetFileName(Path);
    public string Glyph => Kind switch { EditKind.Video => "", EditKind.Picture => "", EditKind.Title => "", _ => "" };
    public string LengthText => $"{Length:0.0}s";
    private void Changed() { OnPropertyChanged(nameof(Length)); OnPropertyChanged(nameof(LengthText)); }
}

public sealed record ExportOptions(int Height, int Fps, int Quality);

/// <summary>Cuts, joins and exports with Windows' own MediaComposition, so edits render with the PC's hardware video encoder and need no extra software.</summary>
public static class EditorService
{
    public static readonly string[] VideoExt = { ".mp4", ".mov", ".wmv", ".avi", ".mkv", ".m4v" };
    public static readonly string[] PictureExt = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
    public static readonly string[] MusicExt = { ".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac" };

    public static async Task<EditItem?> Probe(string path, EditKind kind)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        double dur = 0;
        if (kind == EditKind.Video)
        {
            var clip = await MediaClip.CreateFromFileAsync(file);
            dur = clip.OriginalDuration.TotalSeconds;
        }
        else if (kind == EditKind.Music)
        {
            var track = await BackgroundAudioTrack.CreateFromFileAsync(file);
            dur = track.OriginalDuration.TotalSeconds;
        }
        var item = new EditItem { Kind = kind, Path = path, Source = dur };
        item.End = kind is EditKind.Picture ? 4 : dur;
        return item;
    }

    public static EditItem NewTitle(string text) => new() { Kind = EditKind.Title, Source = 60, Text = text, End = 3 };

    /// <summary>Draws a title card as a 1920x1080 picture (white text on black). Must run on the UI thread.</summary>
    private static string RenderTitle(string text, string dir)
    {
        const int W = 1920, H = 1080;
        var vis = new DrawingVisual();
        using (var dc = vis.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, W, H));
            var ft = new FormattedText(string.IsNullOrWhiteSpace(text) ? " " : text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 96, Brushes.White, 1.0) { MaxTextWidth = W - 240, TextAlignment = TextAlignment.Center };
            dc.DrawText(ft, new Point(120, (H - ft.Height) / 2));
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(vis);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        var path = System.IO.Path.Combine(dir, $"title-{Guid.NewGuid():N}.png");
        using (var fs = File.Create(path)) enc.Save(fs);
        return path;
    }

    public static async Task<string> Export(IReadOnlyList<EditItem> items, string outPath, ExportOptions opt, IProgress<double> progress, CancellationToken ct)
    {
        var visual = items.Where(i => i.Kind != EditKind.Music).ToList();
        if (visual.Count == 0) throw new InvalidOperationException("There's nothing on the timeline to export yet.");
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NightyEdit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var comp = new MediaComposition();
            foreach (var it in visual)
            {
                MediaClip clip;
                if (it.Kind == EditKind.Video)
                {
                    clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(it.Path));
                    clip.TrimTimeFromStart = TimeSpan.FromSeconds(it.Start);
                    clip.TrimTimeFromEnd = TimeSpan.FromSeconds(Math.Max(0, clip.OriginalDuration.TotalSeconds - it.End));
                    clip.Volume = it.Volume;
                }
                else
                {
                    string img = it.Kind == EditKind.Title ? RenderTitle(it.Text, temp) : it.Path;
                    clip = await MediaClip.CreateFromImageFileAsync(await StorageFile.GetFileFromPathAsync(img), TimeSpan.FromSeconds(it.Length));
                }
                comp.Clips.Add(clip);
            }
            foreach (var m in items.Where(i => i.Kind == EditKind.Music))
            {
                var track = await BackgroundAudioTrack.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(m.Path));
                track.TrimTimeFromStart = TimeSpan.FromSeconds(m.Start);
                track.TrimTimeFromEnd = TimeSpan.FromSeconds(Math.Max(0, track.OriginalDuration.TotalSeconds - m.End));
                track.Delay = TimeSpan.FromSeconds(m.Delay);
                track.Volume = m.Volume;
                comp.BackgroundAudioTracks.Add(track);
            }

            var quality = opt.Height switch { <= 480 => VideoEncodingQuality.Vga, <= 720 => VideoEncodingQuality.HD720p, <= 1080 => VideoEncodingQuality.HD1080p, _ => VideoEncodingQuality.Uhd2160p };
            var profile = MediaEncodingProfile.CreateMp4(quality);
            profile.Video.FrameRate.Numerator = (uint)opt.Fps; profile.Video.FrameRate.Denominator = 1;
            profile.Video.Bitrate = (uint)(profile.Video.Width * (double)profile.Video.Height * opt.Fps * opt.Quality switch { 0 => 0.05, 1 => 0.09, _ => 0.14 });

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath)!);
            var folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetDirectoryName(outPath)!);
            var outFile = await folder.CreateFileAsync(System.IO.Path.GetFileName(outPath), CreationCollisionOption.GenerateUniqueName);

            var op = comp.RenderToFileAsync(outFile, MediaTrimmingPreference.Fast, profile);
            op.Progress = (_, p) => progress.Report(p);
            using var reg = ct.Register(() => op.Cancel());
            TranscodeFailureReason reason;
            try { reason = await op; }
            catch (OperationCanceledException) { try { await outFile.DeleteAsync(); } catch { } throw; }
            if (reason != TranscodeFailureReason.None)
            {
                try { await outFile.DeleteAsync(); } catch { }
                throw new InvalidOperationException(reason switch
                {
                    TranscodeFailureReason.CodecNotFound => "Windows has no codec for one of these files.",
                    TranscodeFailureReason.InvalidProfile => "Windows can't make a video with these export settings. Try a lower resolution.",
                    _ => "The export failed (" + reason + ").",
                });
            }
            return outFile.Path;
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }
}
