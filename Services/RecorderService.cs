using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Nighty.Models;
using Nighty.Native;
using Vortice.MediaFoundation;

namespace Nighty.Services;

/// <summary>Writes frames to an H.264 .mp4 with Windows' own Media Foundation encoder (hardware when the graphics card has one).</summary>
internal sealed class Mp4Writer : IDisposable
{
    private static readonly object InitGate = new();
    private static bool _started;
    private IMFSinkWriter? _writer;
    private int _stream;
    private readonly int _w, _h, _size;
    private bool _open;
    public bool HardwareRequested { get; }

    public static void Startup()
    {
        lock (InitGate) { if (_started) return; MediaFactory.MFStartup(); _started = true; }
    }
    public static void ShutdownAll() { lock (InitGate) { if (!_started) return; try { MediaFactory.MFShutdown(); } catch { } _started = false; } }

    public Mp4Writer(string path, int w, int h, int fps, int bitrate, bool hardware)
    {
        Startup();
        _w = w; _h = h; _size = w * h * 4;
        HardwareRequested = hardware;
        using var attrs = MediaFactory.MFCreateAttributes(2);
        attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, hardware ? 1u : 0u);
        _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attrs);

        using var output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
        output.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        output.Set(MediaTypeAttributeKeys.FrameSize, Pack(w, h));
        output.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
        output.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        _stream = _writer.AddStream(output);

        using var input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);   // bottom-up BGRX, the default for RGB in Media Foundation
        input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        input.Set(MediaTypeAttributeKeys.FrameSize, Pack(w, h));
        input.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
        input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        _writer.SetInputMediaType(_stream, input, null!);
        _writer.BeginWriting();
        _open = true;
    }

    private static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    /// <summary>Writes one frame of bottom-up BGRX pixels. Times are in 100 ns units.</summary>
    public void Write(byte[] bgra, long timeHns, long durationHns)
    {
        if (!_open || _writer == null) return;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(_size);
        buffer.Lock(out var ptr, out _, out _);
        try { Marshal.Copy(bgra, 0, ptr, _size); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = _size;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timeHns;
        sample.SampleDuration = durationHns;
        _writer.WriteSample(_stream, sample);
    }

    public void Dispose()
    {
        if (_writer == null) return;
        try { if (_open) _writer.Finalize(); } catch (Exception ex) { Log.Warn("Finalizing the video failed", ex); }
        _open = false;
        _writer.Dispose();
        _writer = null;
    }
}

/// <summary>
/// Screen and Roblox-window recording. A capture loop grabs frames (GDI, scaled to the chosen resolution). With Instant Replay on
/// it keeps the last N seconds as compressed frames in memory only (never on disk) and encodes them to an .mp4 when you save a clip.
/// A full recording is encoded as it goes. Nothing runs while both are off.
/// </summary>
public sealed class RecorderService
{
    private sealed record Frame(long Ms, int W, int H, byte[] Jpeg);

    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int Size, Flags; public IntPtr Cursor; public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int dx, int dy, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    private readonly object _gate = new();
    private readonly LinkedList<Frame> _ring = new();
    private long _ringBytes;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _replay, _recording;
    private Mp4Writer? _writer;
    private string? _recordPath;
    private long _recordStartMs, _recordFrames, _recordDropped;
    private string _activeApp = "";

    public event Action? Changed;
    public bool ReplayOn => _replay;
    public bool IsRecording => _recording;
    public bool IsRunning => _thread != null;
    public string Encoder { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public double BufferedSeconds { get { lock (_gate) return _ring.Count == 0 ? 0 : (_ring.Last!.Value.Ms - _ring.First!.Value.Ms) / 1000.0; } }
    public long BufferedBytes { get { lock (_gate) return _ringBytes; } }
    public TimeSpan RecordingTime => _recording ? TimeSpan.FromMilliseconds(Environment.TickCount64 - _recordStartMs) : TimeSpan.Zero;
    public long DroppedFrames => _recordDropped;

    private RecordSettings S => Svc.S.Record;

    // ---------------- folders ----------------
    public static string BaseFolder => Svc.S.Record.Folder.Length > 0 ? Svc.S.Record.Folder : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Nighty");
    public static string ClipsFolder => Path.Combine(BaseFolder, "Clips");
    public static string RecordingsFolder => Path.Combine(BaseFolder, "Recordings");

    private string TargetFolder(string root)
    {
        var dir = root;
        if (S.SortByApp && _activeApp.Length > 0) dir = Path.Combine(root, string.Concat(_activeApp.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------------- control ----------------
    public void SetReplay(bool on)
    {
        _replay = on;
        if (!on) lock (_gate) { _ring.Clear(); _ringBytes = 0; }
        Sync();
    }

    public (bool Ok, string Message) StartRecording()
    {
        if (_recording) return (false, "Already recording.");
        try
        {
            Directory.CreateDirectory(RecordingsFolder);
            _activeApp = ForegroundApp();
            _recordPath = Path.Combine(TargetFolder(RecordingsFolder), $"Recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            _recordStartMs = Environment.TickCount64; _recordFrames = 0; _recordDropped = 0;
            _writer = null;   // created by the capture loop once the frame size is known
            _recording = true;
            LastError = "";
            Sync();
            return (true, "Recording started.");
        }
        catch (Exception ex) { _recording = false; return (false, "Recording couldn't start. " + ex.Message); }
    }

    public Task<(bool Ok, string Message, string? Path)> StopRecordingAsync()
    {
        if (!_recording) return Task.FromResult<(bool, string, string?)>((false, "Nothing is being recorded.", null));
        _recording = false;
        var path = _recordPath;
        return Task.Run<(bool, string, string?)>(() =>
        {
            Thread.Sleep(150);   // let the loop write its last frame
            lock (_gate) { _writer?.Dispose(); _writer = null; }
            Sync();
            if (path != null && File.Exists(path) && new FileInfo(path).Length > 0) return (true, "Recording saved", path);
            return (false, LastError.Length > 0 ? LastError : "The recording couldn't be saved.", null);
        });
    }

    /// <summary>Encodes what Instant Replay has buffered into a clip.</summary>
    public Task<(bool Ok, string Message, string? Path)> SaveClipAsync()
    {
        List<Frame> frames;
        lock (_gate)
        {
            if (_ring.Count == 0) return Task.FromResult<(bool, string, string?)>((false, "Nothing to save yet. The replay fills up as you play.", null));
            long cutoff = _ring.Last!.Value.Ms - S.ClipSeconds * 1000L;
            var last = _ring.Last.Value;
            frames = _ring.Where(f => f.Ms >= cutoff && f.W == last.W && f.H == last.H).ToList();
        }
        var app = ForegroundApp();
        return Task.Run<(bool, string, string?)>(() =>
        {
            try
            {
                _activeApp = app;
                var path = Path.Combine(TargetFolder(ClipsFolder), $"Clip {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
                int w = frames[0].W, h = frames[0].H, fps = Math.Clamp(S.Fps, 10, 60);
                double spanS = Math.Max(0.5, (frames[^1].Ms - frames[0].Ms) / 1000.0);
                using (var wr = new Mp4Writer(path, w, h, fps, Bitrate(w, h, fps), hardware: true))
                {
                    var buf = new byte[w * h * 4];
                    long t0 = frames[0].Ms;
                    for (int i = 0; i < frames.Count; i++)
                    {
                        DecodeBottomUp(frames[i].Jpeg, w, h, buf);
                        long start = (frames[i].Ms - t0) * 10_000;
                        long dur = i + 1 < frames.Count ? (frames[i + 1].Ms - frames[i].Ms) * 10_000 : 10_000_000L / fps;
                        wr.Write(buf, start, Math.Max(dur, 1));
                    }
                }
                Log.Info($"Clip saved: {path} ({frames.Count} frames, {spanS:0.0} s)");
                return (true, "Clip saved", path);
            }
            catch (Exception ex)
            {
                Log.Error("Saving the clip failed", ex);
                return (false, "The clip couldn't be saved: " + ex.Message, null);
            }
        });
    }

    public void Shutdown()
    {
        _replay = false; _recording = false;
        Stop();
        lock (_gate) { _writer?.Dispose(); _writer = null; }
        Mp4Writer.ShutdownAll();
    }

    private void Sync()
    {
        bool need = _replay || _recording;
        if (need && _thread == null) Start();
        else if (!need && _thread != null) Stop();
        Changed?.Invoke();
    }

    private void Start()
    {
        var cts = _cts = new CancellationTokenSource();
        _thread = new Thread(() => Loop(cts.Token)) { IsBackground = true, Name = "Nighty recorder", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Stop()
    {
        _cts?.Cancel(); _cts = null;
        var t = _thread; _thread = null;
        t?.Join(1500);
        Changed?.Invoke();
    }

    // ---------------- capture ----------------
    private static string ForegroundApp()
    {
        try
        {
            var h = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(h, out var pid);
            using var p = Process.GetProcessById((int)pid);
            var n = p.ProcessName;
            return n.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase) ? "Roblox" : n.Equals("Nighty", StringComparison.OrdinalIgnoreCase) ? "" : n;
        }
        catch { return ""; }
    }

    private static int Bitrate(int w, int h, int fps)
    {
        double bpp = Svc.S.Record.Quality switch { 0 => 0.12, 2 => 0.40, _ => 0.22 };
        return (int)Math.Clamp(w * (double)h * fps * bpp, 1_000_000, 60_000_000);
    }

    private (int X, int Y, int W, int H) SourceRect()
    {
        if (S.Source == RecordSource.RobloxWindow)
        {
            foreach (var p in Process.GetProcessesByName(RobloxService.ProcessName))
            {
                try
                {
                    var hwnd = p.MainWindowHandle;
                    if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var r) && r.R - r.L > 64 && r.B - r.T > 64)
                        return (r.L, r.T, r.R - r.L, r.B - r.T);
                }
                finally { p.Dispose(); }
            }
        }
        return (0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
    }

    private static (int W, int H) Fit(int sw, int sh, int target)
    {
        int h = target <= 0 || target >= sh ? sh : target;
        int w = (int)Math.Round(sw * (h / (double)sh));
        return (Math.Max(2, w & ~1), Math.Max(2, h & ~1));   // H.264 needs even sizes
    }

    private void Loop(CancellationToken ct)
    {
        // The screen is copied at its own size (a plain BitBlt is much cheaper than a scaled one on a big display)
        // and then shrunk on the CPU, spread over all cores.
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), dib = IntPtr.Zero, old = IntPtr.Zero, bits = IntPtr.Zero;
        int natW = 0, natH = 0, curW = 0, curH = 0;
        byte[] native = Array.Empty<byte>(), pixels = Array.Empty<byte>();
        long next = Wait.Now;
        long lastRect = 0;
        double capMs = 0, jpgMs = 0, scaleMs = 0; int nFrames = 0;
        // Preferred path: the graphics card copies the primary monitor (DXGI). Anything it can't do falls back to the GDI copy below.
        using var dxgi = new DxgiGrabber();
        bool useDxgi = dxgi.Init();
        byte[] monitor = useDxgi ? new byte[dxgi.Width * dxgi.Height * 4] : Array.Empty<byte>();
        IntPtr tDc = IntPtr.Zero, tDib = IntPtr.Zero, tOld = IntPtr.Zero, tBits = IntPtr.Zero; int tW = 0, tH = 0;
        bool gotDxgiFrame = false;
        Encoder = useDxgi ? "GPU screen capture" : "Screen copy (GDI)";
        (int X, int Y, int W, int H) src = SourceRect();
        var jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var jpegParams = new EncoderParameters(1);
        NativeMethods.timeBeginPeriod(1);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int fps = Math.Clamp(S.Fps, 10, 60);
                long period = Wait.FromMs(1000.0 / fps);
                Wait.Until(next, ct, 1.0);
                if (ct.IsCancellationRequested) break;
                long frameStart = Wait.Now;
                next += period;
                if (next < frameStart) { next = frameStart + period; if (_recording) _recordDropped++; }   // fell behind: skip instead of bursting

                if (Wait.ToMs(frameStart - lastRect) > 500) { src = SourceRect(); lastRect = frameStart; }
                src = (src.X, src.Y, Math.Max(2, src.W), Math.Max(2, src.H));
                var (tw, th) = Fit(src.W, src.H, S.Resolution);
                if (src.W != natW || src.H != natH)
                {
                    if (dib != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(dib); }
                    var bi = new BITMAPINFOHEADER { Size = 40, Width = src.W, Height = src.H, Planes = 1, BitCount = 32 };   // positive height: bottom-up, what Media Foundation expects
                    dib = CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
                    old = SelectObject(mem, dib);
                    natW = src.W; natH = src.H; native = new byte[natW * natH * 4];
                }
                if (tw != curW || th != curH)
                {
                    curW = tw; curH = th; pixels = new byte[tw * th * 4];
                    lock (_gate) { _ring.Clear(); _ringBytes = 0; }   // a clip can't mix sizes
                }

                long t1 = Wait.Now;
                bool inPrimary = useDxgi && src.X >= 0 && src.Y >= 0 && src.X + src.W <= dxgi.Width && src.Y + src.H <= dxgi.Height;
                long t2;
                if (inPrimary)
                {
                    if (!dxgi.Grab(monitor, gotDxgiFrame ? 0 : 250))
                    {
                        // display mode changed or access lost: restart the duplication, and use the GDI copy meanwhile
                        useDxgi = dxgi.Init();
                        if (useDxgi && monitor.Length != dxgi.Width * dxgi.Height * 4) monitor = new byte[dxgi.Width * dxgi.Height * 4];
                        gotDxgiFrame = false;
                        continue;
                    }
                    gotDxgiFrame = true;
                    capMs += Wait.ToMs(Wait.Now - t1);
                    t2 = Wait.Now;
                    Downscale(monitor, dxgi.Width, src.X, src.Y, src.W, src.H, pixels, tw, th, topDown: true);
                    if (S.ShowCursor) DrawCursorOnPixels(pixels, tw, th, src, ref tDc, ref tDib, ref tOld, ref tBits, ref tW, ref tH, screen);
                }
                else
                {
                    if (!BitBlt(mem, 0, 0, natW, natH, screen, src.X, src.Y, SRCCOPY)) continue;
                    if (S.ShowCursor) DrawCursor(mem, src, natW, natH);
                    capMs += Wait.ToMs(Wait.Now - t1);
                    t2 = Wait.Now;
                    if (tw == natW && th == natH) Marshal.Copy(bits, pixels, 0, pixels.Length);
                    else { Marshal.Copy(bits, native, 0, native.Length); Downscale(native, natW, 0, 0, natW, natH, pixels, tw, th, topDown: false); }
                }
                scaleMs += Wait.ToMs(Wait.Now - t2);
                nFrames++;

                long nowMs = Environment.TickCount64;
                long t3 = Wait.Now;
                if (_replay)
                {
                    jpegParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, S.Quality == 0 ? 55L : S.Quality == 2 ? 88L : 72L);
                    var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                    try
                    {
                        // a bottom-up image: a negative stride starting at the last row reads it top-down
                        using var bmp = new Bitmap(tw, th, -tw * 4, PixelFormat.Format32bppRgb, pin.AddrOfPinnedObject() + (th - 1) * tw * 4);
                        using var ms = new MemoryStream(64 * 1024);
                        bmp.Save(ms, jpeg, jpegParams);
                        var f = new Frame(nowMs, tw, th, ms.ToArray());
                        lock (_gate)
                        {
                            _ring.AddLast(f); _ringBytes += f.Jpeg.Length;
                            long cutoff = nowMs - (S.ClipSeconds + 2) * 1000L;
                            while (_ring.First != null && _ring.First.Value.Ms < cutoff) { _ringBytes -= _ring.First.Value.Jpeg.Length; _ring.RemoveFirst(); }
                        }
                    }
                    finally { pin.Free(); }
                }
                jpgMs += Wait.ToMs(Wait.Now - t3);
                if (_recording)
                {
                    try
                    {
                        lock (_gate)
                        {
                            if (_writer == null && _recordPath != null)
                            {
                                _writer = new Mp4Writer(_recordPath, tw, th, fps, Bitrate(tw, th, fps), hardware: true);
                                Encoder = "H.264 (Media Foundation)";
                            }
                            _writer?.Write(pixels, (nowMs - _recordStartMs) * 10_000, 10_000_000L / fps);
                            _recordFrames++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Recording failed", ex);
                        LastError = "The video encoder stopped working. Try a lower resolution.";
                        _recording = false;
                        lock (_gate) { try { _writer?.Dispose(); } catch { } _writer = null; }
                        Svc.Dispatch(() => { Changed?.Invoke(); Svc.Toast.Show("Recording", LastError, false); });
                    }
                }
            }
        }
        catch (Exception ex) { Log.Error("Capture loop failed", ex); LastError = "Capture stopped: " + ex.Message; }
        finally
        {
            if (nFrames > 0) Log.Info($"Recorder loop: {nFrames} frames, copy {capMs / nFrames:0.0} ms, shrink {scaleMs / nFrames:0.0} ms, replay-encode {jpgMs / nFrames:0.0} ms per frame");
            NativeMethods.timeEndPeriod(1);
            if (dib != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(dib); }
            if (tDib != IntPtr.Zero) { SelectObject(tDc, tOld); DeleteObject(tDib); }
            if (tDc != IntPtr.Zero) DeleteDC(tDc);
            DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
            _ = gotDxgiFrame;
        }
    }

    /// <summary>The GPU path has no pointer in the picture, so draw it onto the finished (small) frame with GDI.</summary>
    private static void DrawCursorOnPixels(byte[] pixels, int tw, int th, (int X, int Y, int W, int H) src, ref IntPtr dc, ref IntPtr dib, ref IntPtr old, ref IntPtr bits, ref int w, ref int h, IntPtr screen)
    {
        var ci = new CURSORINFO { Size = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.Flags & 1) == 0) return;
        if (ci.X < src.X || ci.Y < src.Y || ci.X > src.X + src.W || ci.Y > src.Y + src.H) return;
        if (dib == IntPtr.Zero || w != tw || h != th)
        {
            if (dib != IntPtr.Zero) { SelectObject(dc, old); DeleteObject(dib); }
            if (dc == IntPtr.Zero) dc = CreateCompatibleDC(screen);
            var bi = new BITMAPINFOHEADER { Size = 40, Width = tw, Height = th, Planes = 1, BitCount = 32 };
            dib = CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
            old = SelectObject(dc, dib);
            w = tw; h = th;
        }
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        double kx = tw / (double)src.W, ky = th / (double)src.H;
        DrawIconEx(dc, (int)((ci.X - src.X) * kx), (int)((ci.Y - src.Y) * ky), ci.Cursor, 0, 0, 0, IntPtr.Zero, 3);
        Marshal.Copy(bits, pixels, 0, pixels.Length);
    }

    /// <summary>
    /// Box-filter shrink of BGRX pixels from a region of the source (stride = srcStride pixels) into the bottom-up destination, one row
    /// per task so every core helps. A top-down source is flipped on the way.
    /// </summary>
    private static void Downscale(byte[] src, int srcStride, int ox, int oy, int sw, int sh, byte[] dst, int tw, int th, bool topDown)
    {
        double fx = sw / (double)tw, fy = sh / (double)th;
        int bx = Math.Max(1, (int)Math.Round(fx)), by = Math.Max(1, (int)Math.Round(fy));
        Parallel.For(0, th, y =>
        {
            int sy0 = oy + Math.Min(sh - by, (int)(y * fy));
            int o = (topDown ? th - 1 - y : y) * tw * 4;
            for (int x = 0; x < tw; x++)
            {
                int sx0 = ox + Math.Min(sw - bx, (int)(x * fx));
                int r = 0, g = 0, b = 0;
                for (int j = 0; j < by; j++)
                {
                    int i = ((sy0 + j) * srcStride + sx0) * 4;
                    for (int k = 0; k < bx; k++, i += 4) { b += src[i]; g += src[i + 1]; r += src[i + 2]; }
                }
                int n = bx * by;
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(r / n); dst[o + 3] = 255;
                o += 4;
            }
        });
    }

    private static void DrawCursor(IntPtr dc, (int X, int Y, int W, int H) src, int tw, int th)
    {
        var ci = new CURSORINFO { Size = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.Flags & 1) == 0) return;
        if (ci.X < src.X || ci.Y < src.Y || ci.X > src.X + src.W || ci.Y > src.Y + src.H) return;
        // GDI draws with a top-left origin whatever the DIB's memory order, so no flipping here
        DrawIconEx(dc, ci.X - src.X, ci.Y - src.Y, ci.Cursor, 0, 0, 0, IntPtr.Zero, 3);
    }

    private static void DecodeBottomUp(byte[] jpeg, int w, int h, byte[] dest)
    {
        using var ms = new MemoryStream(jpeg);
        using var bmp = new Bitmap(ms);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int stride = w * 4;
            for (int y = 0; y < h; y++)   // top-down in the JPEG, bottom-up in the output
                Marshal.Copy(data.Scan0 + y * data.Stride, dest, (h - 1 - y) * stride, stride);
        }
        finally { bmp.UnlockBits(data); }
    }

    // ---------------- library ----------------
    public sealed record LibraryItem(string Path, string Name, string Kind, DateTime When, long Size, string App);

    public static List<LibraryItem> Library()
    {
        var list = new List<LibraryItem>();
        void Scan(string root, string kind)
        {
            if (!Directory.Exists(root)) return;
            foreach (var f in Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(f);
                var rel = System.IO.Path.GetRelativePath(root, f);
                list.Add(new LibraryItem(f, fi.Name, kind, fi.LastWriteTime, fi.Length, rel.Contains(System.IO.Path.DirectorySeparatorChar) ? rel.Split(System.IO.Path.DirectorySeparatorChar)[0] : ""));
            }
        }
        try { Scan(ClipsFolder, "Clip"); Scan(RecordingsFolder, "Recording"); } catch (Exception ex) { Log.Warn("Listing clips failed", ex); }
        return list.OrderByDescending(i => i.When).ToList();
    }
}
