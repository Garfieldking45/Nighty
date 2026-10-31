using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using Nighty.Mvvm;

namespace Nighty.Services;

public sealed class Track : ObservableObject
{
    private string _title = "", _artist = "", _state = "";
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Artist { get => _artist; set => Set(ref _artist, value); }
    /// <summary>A file path on this PC, or "yt:VIDEOID" for a YouTube song.</summary>
    public string Source { get; set; } = "";
    public int Seconds { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsYouTube => Source.StartsWith("yt:", StringComparison.Ordinal);
    [System.Text.Json.Serialization.JsonIgnore] public string VideoId => IsYouTube ? Source[3..] : "";
    [System.Text.Json.Serialization.JsonIgnore] public string Length => Seconds <= 0 ? "" : $"{Seconds / 60}:{Seconds % 60:00}";
    [System.Text.Json.Serialization.JsonIgnore] public string Subtitle => string.Join("  ·  ", new[] { Artist, IsYouTube ? "YouTube" : "On this PC" }.Where(s => s.Length > 0));
    /// <summary>"" normally, or "Downloading…" / an error while the song is being fetched.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string State { get => _state; set => Set(ref _state, value); }
}

public enum RepeatMode { Off, All, One }

/// <summary>
/// Music player. Songs come from files on this PC or from YouTube. A YouTube song is downloaded as audio into a cache
/// folder (using yt-dlp, fetched once, checked against the SHA-256 the yt-dlp project publishes) and then played like
/// any file, so nothing is streamed through Nighty and playback does not depend on the network after the download.
/// Nighty plays and stores nothing but audio for songs you choose; whether you may do that with a given song is yours to check.
/// </summary>
public sealed class MusicService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly string[] AudioExt = { ".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac", ".ogg", ".opus", ".mp4" };

    private static string Dir => Path.Combine(AppPaths.Root, "music");
    private static string CacheDir => Path.Combine(Dir, "cache");
    private static string ToolDir => Path.Combine(Dir, "tools");
    private static string LibraryFile => Path.Combine(Dir, "library.json");
    private static string YtDlpExe => Path.Combine(ToolDir, "yt-dlp.exe");
    private static string DenoExe => Path.Combine(ToolDir, "deno.exe");

    private MediaPlayer? _player;
    private readonly Random _rng = new();
    private CancellationTokenSource? _loadCts;

    public ObservableCollection<Track> Library { get; } = new();
    public Track? Current { get; private set; }
    public bool IsPlaying { get; private set; }
    public double DurationSeconds { get; private set; }
    public double PositionSeconds => _player == null ? 0 : _player.Position.TotalSeconds;
    public string Message { get; private set; } = "";
    public event Action? Changed;

    public bool Shuffle { get => Svc.S.Music.Shuffle; set { Svc.S.Music.Shuffle = value; Changed?.Invoke(); } }
    public RepeatMode Repeat { get => Svc.S.Music.Repeat; set { Svc.S.Music.Repeat = value; Changed?.Invoke(); } }
    public double Volume { get => Svc.S.Music.Volume; set { Svc.S.Music.Volume = Math.Clamp(value, 0, 1); if (_player != null) _player.Volume = Svc.S.Music.Volume; } }
    public bool HasYtDlp => File.Exists(YtDlpExe);

    // ------------------------------------------------------------------ library

    public void Load()
    {
        try
        {
            if (!File.Exists(LibraryFile)) return;
            var list = JsonSerializer.Deserialize<List<Track>>(File.ReadAllText(LibraryFile));
            if (list == null) return;
            foreach (var t in list) Library.Add(t);
        }
        catch (Exception ex)
        {
            Log.Error("Music library unreadable", ex);
            try { File.Move(LibraryFile, Path.Combine(Dir, "library.broken.json"), true); } catch { }
            Message = "Your music library file was damaged, so a new one was started. The old one is library.broken.json.";
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = LibraryFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Library.ToList()));
            File.Move(tmp, LibraryFile, true);
        }
        catch (Exception ex) { Log.Error("Music library save failed", ex); }
    }

    public int AddFiles(IEnumerable<string> paths)
    {
        int n = 0;
        foreach (var p in paths)
        {
            var files = Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories) : new[] { p };
            foreach (var f in files)
            {
                if (!AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()) || !File.Exists(f)) continue;
                if (Library.Any(t => string.Equals(t.Source, f, StringComparison.OrdinalIgnoreCase))) continue;
                Library.Add(new Track { Title = Path.GetFileNameWithoutExtension(f), Source = f });
                n++;
            }
        }
        if (n > 0) Save();
        return n;
    }

    public void Add(Track t)
    {
        if (Library.Any(x => x.Source == t.Source)) return;
        Library.Add(t); Save();
    }

    public void Remove(Track t)
    {
        if (Current == t) Stop();
        Library.Remove(t); Save();
    }

    public long CacheBytes()
    {
        try { return Directory.Exists(CacheDir) ? new DirectoryInfo(CacheDir).EnumerateFiles().Sum(f => f.Length) : 0; } catch { return 0; }
    }

    public void ClearCache()
    {
        try { if (Directory.Exists(CacheDir)) foreach (var f in Directory.EnumerateFiles(CacheDir)) { try { if (Current == null || !f.Contains(Current.VideoId)) File.Delete(f); } catch { } } } catch { }
    }

    // ------------------------------------------------------------------ playback

    private MediaPlayer Player
    {
        get
        {
            if (_player != null) return _player;
            var p = new MediaPlayer { Volume = Svc.S.Music.Volume };
            p.MediaOpened += (_, _) => { DurationSeconds = p.NaturalDuration.HasTimeSpan ? p.NaturalDuration.TimeSpan.TotalSeconds : 0; if (Current != null && Current.Seconds <= 0 && DurationSeconds > 0) { Current.Seconds = (int)DurationSeconds; Save(); } Changed?.Invoke(); };
            p.MediaEnded += (_, _) => Advance(auto: true);
            p.MediaFailed += (_, e) => { SetMessage("That song couldn't be played: " + (e.ErrorException?.Message ?? "unknown error")); IsPlaying = false; if (Current != null) Current.State = "Can't play"; Changed?.Invoke(); };
            return _player = p;
        }
    }

    public async Task Play(Track t)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        Current = t; IsPlaying = false; DurationSeconds = 0; Message = "";
        Changed?.Invoke();
        string? path = t.Source;
        if (t.IsYouTube)
        {
            t.State = "Downloading…";
            try { path = await DownloadAudio(t.VideoId, cts.Token); }
            catch (OperationCanceledException) { t.State = ""; return; }
            catch (Exception ex) { t.State = "Unavailable"; SetMessage(ex.Message); return; }
            if (cts.IsCancellationRequested) return;
            t.State = "";
        }
        if (!File.Exists(path)) { t.State = "Missing"; SetMessage("That file is no longer on this PC."); return; }
        Player.Open(new Uri(path));
        Player.Play();
        IsPlaying = true;
        Changed?.Invoke();
    }

    public void TogglePause()
    {
        if (Current == null) { var first = Library.FirstOrDefault(); if (first != null) _ = Play(first); return; }
        if (IsPlaying) { Player.Pause(); IsPlaying = false; } else { Player.Play(); IsPlaying = true; }
        Changed?.Invoke();
    }

    public void Stop()
    {
        _loadCts?.Cancel();
        _player?.Stop(); _player?.Close();
        IsPlaying = false; Current = null; DurationSeconds = 0;
        Changed?.Invoke();
    }

    public void Seek(double seconds) { if (_player != null && DurationSeconds > 0) _player.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, DurationSeconds)); }

    public void Next() => Advance(auto: false);
    public void Previous()
    {
        if (Library.Count == 0) return;
        if (PositionSeconds > 4 && Current != null) { Seek(0); return; }
        int i = Current == null ? 0 : Library.IndexOf(Current);
        _ = Play(Library[(i - 1 + Library.Count) % Library.Count]);
    }

    private void Advance(bool auto)
    {
        if (Library.Count == 0) return;
        if (auto && Repeat == RepeatMode.One && Current != null) { _ = Play(Current); return; }
        int i = Current == null ? -1 : Library.IndexOf(Current);
        int next;
        if (Shuffle && Library.Count > 1) { do next = _rng.Next(Library.Count); while (next == i); }
        else next = i + 1;
        if (next >= Library.Count)
        {
            if (auto && Repeat == RepeatMode.Off) { Stop(); return; }
            next = 0;
        }
        _ = Play(Library[next]);
    }

    private void SetMessage(string m) { Message = m; Changed?.Invoke(); }

    // ------------------------------------------------------------------ YouTube (yt-dlp)

    /// <summary>Downloads yt-dlp the first time. The file is checked against the project's published SHA-256 list before it is kept.</summary>
    public async Task EnsureYtDlp(IProgress<string>? progress = null, bool force = false, CancellationToken ct = default)
    {
        if (File.Exists(YtDlpExe) && !force) return;
        Directory.CreateDirectory(ToolDir);
        progress?.Report("Downloading the music engine (yt-dlp)…");
        const string baseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
        var exeTmp = YtDlpExe + ".download";
        try
        {
            var bytes = await Http.GetByteArrayAsync(baseUrl + "yt-dlp.exe", ct);
            var sums = await Http.GetStringAsync(baseUrl + "SHA2-256SUMS", ct);
            string want = sums.Split('\n').Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 2 && p[^1].TrimStart('*') == "yt-dlp.exe").Select(p => p[0]).FirstOrDefault() ?? "";
            string got = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(want, got, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The music engine download didn't match its published checksum, so it was discarded.");
            await File.WriteAllBytesAsync(exeTmp, bytes, ct);
            File.Move(exeTmp, YtDlpExe, true);
        }
        finally { try { File.Delete(exeTmp); } catch { } }
        progress?.Report("Music engine ready.");
    }

    /// <summary>Some YouTube answers need a JavaScript runtime; Deno is fetched only when yt-dlp asks for one.</summary>
    private async Task EnsureDeno(CancellationToken ct)
    {
        if (File.Exists(DenoExe)) return;
        Directory.CreateDirectory(ToolDir);
        var zip = Path.Combine(ToolDir, "deno.zip");
        try
        {
            var bytes = await Http.GetByteArrayAsync("https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip", ct);
            await File.WriteAllBytesAsync(zip, bytes, ct);
            using var za = System.IO.Compression.ZipFile.OpenRead(zip);
            var entry = za.GetEntry("deno.exe") ?? throw new InvalidDataException("deno.exe wasn't in the download.");
            System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, DenoExe, true);
        }
        finally { try { File.Delete(zip); } catch { } }
    }

    private async Task<(int Code, string Out, string Err)> RunYtDlp(IEnumerable<string> args, CancellationToken ct, bool withDeno = false)
    {
        var psi = new ProcessStartInfo(YtDlpExe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--no-warnings"); psi.ArgumentList.Add("--no-config");
        if (withDeno && File.Exists(DenoExe)) { psi.ArgumentList.Add("--js-runtimes"); psi.ArgumentList.Add("deno:" + DenoExe); }
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("The music engine couldn't start.");
        var o = p.StandardOutput.ReadToEndAsync(ct);
        var e = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await o, await e);
    }

    /// <summary>Runs yt-dlp; if it asks for a JavaScript runtime, fetches Deno once and retries.</summary>
    private async Task<(int Code, string Out, string Err)> RunWithRetry(IEnumerable<string> args, CancellationToken ct)
    {
        var list = args.ToList();
        var r = await RunYtDlp(list, ct, withDeno: File.Exists(DenoExe));
        if (r.Code != 0 && !File.Exists(DenoExe) && System.Text.RegularExpressions.Regex.IsMatch(r.Err, "(?i)javascript runtime|n challenge|player response|Signature solving"))
        {
            await EnsureDeno(ct);
            r = await RunYtDlp(list, ct, withDeno: true);
        }
        return r;
    }

    private static string Friendly(string err)
    {
        if (err.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase)) return "YouTube wants to check this PC isn't a bot. Try again in a few minutes.";
        if (err.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase)) return "YouTube is limiting requests from this network. Try again soon.";
        if (err.Contains("age", StringComparison.OrdinalIgnoreCase) && err.Contains("confirm", StringComparison.OrdinalIgnoreCase)) return "YouTube only plays this one for signed-in adults, so Nighty can't play it.";
        if (err.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || err.Contains("Private video", StringComparison.OrdinalIgnoreCase)) return "YouTube says this song is unavailable.";
        var line = err.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) ?? err.Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    public async Task<string> DownloadAudio(string videoId, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(videoId, "^[A-Za-z0-9_-]{6,20}$")) throw new ArgumentException("That isn't a YouTube song id.");
        Directory.CreateDirectory(CacheDir);
        var existing = Directory.EnumerateFiles(CacheDir, videoId + ".*").FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        await EnsureYtDlp(null, false, ct);
        var r = await RunWithRetry(new[]
        {
            "-f", "bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]/bestaudio", "--no-playlist",
            "-o", Path.Combine(CacheDir, "%(id)s.%(ext)s"), "--", "https://www.youtube.com/watch?v=" + videoId,
        }, ct);
        var file = Directory.EnumerateFiles(CacheDir, videoId + ".*").FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
        if (r.Code != 0 || file == null) throw new InvalidOperationException(Friendly(r.Err));
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is ".webm" or ".opus" or ".ogg") { try { File.Delete(file); } catch { } throw new InvalidOperationException("YouTube offered no audio Windows can play."); }
        return file;
    }

    public async Task<List<Track>> SearchYouTube(string query, CancellationToken ct)
    {
        await EnsureYtDlp(null, false, ct);
        var r = await RunWithRetry(new[] { $"ytsearch12:{query}", "--flat-playlist", "--dump-json" }, ct);
        if (r.Code != 0 && string.IsNullOrWhiteSpace(r.Out)) throw new InvalidOperationException(Friendly(r.Err));
        return ParseEntries(r.Out);
    }

    /// <summary>A single song, album or playlist link.</summary>
    public async Task<List<Track>> ImportLink(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || !(u.Host.EndsWith("youtube.com") || u.Host.EndsWith("youtu.be") || u.Host.EndsWith("music.youtube.com")))
            throw new ArgumentException("That isn't a YouTube or YouTube Music link.");
        await EnsureYtDlp(null, false, ct);
        var r = await RunWithRetry(new[] { u.AbsoluteUri, "--flat-playlist", "--dump-json", "--yes-playlist", "--playlist-end", "200" }, ct);
        if (r.Code != 0 && string.IsNullOrWhiteSpace(r.Out)) throw new InvalidOperationException(Friendly(r.Err));
        return ParseEntries(r.Out);
    }

    private static List<Track> ParseEntries(string json)
    {
        var list = new List<Track>();
        foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var d = JsonDocument.Parse(line);
                var root = d.RootElement;
                string? id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (string.IsNullOrEmpty(id) || id.Length > 20) continue;
                string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? id : id;
                string artist = new[] { "artist", "channel", "uploader" }.Select(k => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
                int secs = root.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? (int)du.GetDouble() : 0;
                list.Add(new Track { Title = title, Artist = artist.Replace(" - Topic", ""), Source = "yt:" + id, Seconds = secs });
            }
            catch { /* skip a line that isn't a result */ }
        }
        return list;
    }
}
