using System.Diagnostics;
using System.Runtime.InteropServices;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// The shake motion: a closed loop of random points around the start, joined by a curve. A new point appears every
/// <c>period</c> milliseconds. Smoothness 0 jumps quickly between points and rests (a sharp jitter); smoothness 1 follows a
/// continuous spline through them (a smooth glide). The path always begins at zero, never reaches farther than 1.25 times the
/// strength, and is only ever used as an offset from where the mouse already was.
/// Pure maths (no Windows calls), so it is tested without a game.
/// </summary>
public sealed class ShakePath
{
    private ulong _state;
    private double _amp, _period, _smooth;
    private readonly double[] _p = new double[8];   // four control points, x then y: p0 p1 p2 p3
    private double _last;
    private bool _started;

    public ShakePath(ulong seed, double amplitude, double periodMs, double smoothness)
    {
        _state = seed;
        Configure(amplitude, periodMs, smoothness);
        Reset();
    }

    public void Configure(double amplitude, double periodMs, double smoothness)
    {
        _amp = Math.Clamp(amplitude, 0, 64);
        _period = Math.Clamp(periodMs, 4, 2000);
        _smooth = Math.Clamp(smoothness, 0, 1);
    }

    /// <summary>Strength 1-100 to pixels.</summary>
    public static double AmplitudeFor(int strength) { double x = Math.Clamp(strength, 1, 100) / 100.0; return Math.Clamp(15.6 * x * x + 0.4, 0, 64); }
    /// <summary>Speed 1-100 to the time between points in ms: 260 ms (slow sway) down to 18 ms (fast buzz).</summary>
    public static double PeriodFor(int speed) => Math.Clamp(260.0 * Math.Pow(18.0 / 260.0, (Math.Clamp(speed, 1, 100) - 1) / 99.0), 4, 2000);
    public static double SmoothnessFor(int smoothness) => Math.Clamp(smoothness, 0, 100) / 100.0;

    private ulong Next()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    private double NextUnit() => (Next() >> 11) * (1.0 / 9007199254740992.0);

    private (double X, double Y) RandomPoint()
    {
        double angle = NextUnit() * 2 * Math.PI;
        double r = (Math.Sqrt(NextUnit()) * 0.65 + 0.35) * _amp;
        return (Math.Cos(angle) * r, Math.Sin(angle) * r);
    }

    /// <summary>Starts again from zero (new random targets).</summary>
    public void Reset()
    {
        Array.Clear(_p);
        var a = RandomPoint(); _p[4] = a.X; _p[5] = a.Y;
        var b = RandomPoint(); _p[6] = b.X; _p[7] = b.Y;
        _started = false;
    }

    /// <summary>The offset from the start, in pixels, at time <paramref name="tMs"/> (any clock that only goes forwards).</summary>
    public (double X, double Y) Sample(double tMs)
    {
        if (!_started) { _last = tMs; _started = true; }
        if (tMs - _last > _period * 50) _last = tMs;   // a long pause: carry on from now instead of racing through old points
        while (tMs - _last >= _period)
        {
            _last += _period;
            _p[0] = _p[2]; _p[1] = _p[3]; _p[2] = _p[4]; _p[3] = _p[5]; _p[4] = _p[6]; _p[5] = _p[7];
            var n = RandomPoint(); _p[6] = n.X; _p[7] = n.Y;
        }
        double u = Math.Clamp((tMs - _last) / _period, 0, 1);
        double ease = Math.Min(u / 0.3, 1); ease = (3 - 2 * ease) * ease * ease;   // quick settle into the next point
        double x = Axis(0, u, ease), y = Axis(1, u, ease);
        double len = Math.Sqrt(x * x + y * y), cap = _amp * 1.25;
        if (len > cap && len > 0) { x *= cap / len; y *= cap / len; }
        return (x, y);
    }

    private double Axis(int a, double u, double ease)
    {
        double p0 = _p[0 + a], p1 = _p[2 + a], p2 = _p[4 + a], p3 = _p[6 + a];
        double linear = p1 + (p2 - p1) * ease;
        double u2 = u * u, u3 = u2 * u;
        double spline = 0.5 * ((2 * p1) + (p2 - p0) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u2 + (3 * p1 - p0 - 3 * p2 + p3) * u3);
        return linear + (spline - linear) * _smooth;
    }
}

/// <summary>
/// Runs the shake. It only acts while Roblox is the window in front and its mouse is locked (first person or shift lock):
/// Roblox keeps a locked mouse at the middle of its window, so the cursor sits dead centre and snaps back when nudged.
/// Everything is sent as small relative mouse moves, and when it stops the movement is undone so the camera ends where it began.
/// </summary>
public sealed class ShakeService
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out RECT r);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref NativeMethods.POINT p);

    private readonly ShakeSettings _s;
    private readonly ManualResetEventSlim _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private int _ix, _iy;   // the offset already sent (whole pixels)

    /// <summary>Set by the page: is the shake key (if one is used) active right now?</summary>
    public Func<bool> KeyActive { get; set; } = () => true;
    public Func<bool> Clicking { get; set; } = () => false;

    public volatile string Status = "Off";
    public volatile bool IsShaking;

    public ShakeService(ShakeSettings s) { _s = s; }

    public void Start()
    {
        if (_thread != null) return;
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Nighty shake", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _wake.Set();
    }

    public void Poke() => _wake.Set();
    public void Stop() { _stop = true; _wake.Set(); }

    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    private void SendOffset(int x, int y)
    {
        int dx = x - _ix, dy = y - _iy;
        if (dx == 0 && dy == 0) return;
        _ix = x; _iy = y;
        InputSender.MoveRelative(dx, dy);
    }

    private void Loop()
    {
        IntPtr timer = NativeMethods.CreateWaitableTimerExW(IntPtr.Zero, null, NativeMethods.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, NativeMethods.TIMER_ALL_ACCESS);
        var path = new ShakePath((ulong)Environment.ProcessId ^ ((ulong)Environment.TickCount64 * 0x9E3779B97F4A7C15UL), 3, 90, 0.6);
        bool locked = false, fgWasRoblox = false;
        double lastCheck = -1000, stableMs = 0, retryAt = 0;
        int lostCount = 0, lastX = -1, lastY = -1;
        NativeMethods.POINT center = default;
        double amp = 3;

        try
        {
            while (!_stop)
            {
                if (!_s.Enabled)
                {
                    EndShake(); locked = false; Status = "Off"; IsShaking = false;
                    _wake.Wait(); _wake.Reset();
                    continue;
                }

                double now = NowMs();
                amp = ShakePath.AmplitudeFor(_s.Strength);
                path.Configure(amp, ShakePath.PeriodFor(_s.Speed), ShakePath.SmoothnessFor(_s.Smoothness));

                if (now - lastCheck >= 50)
                {
                    lastCheck = now;
                    bool front = Svc.Roblox.IsForeground;
                    var hwnd = NativeMethods.GetForegroundWindow();
                    if (!front || hwnd == IntPtr.Zero)
                    {
                        if (locked) { _ix = _iy = 0; IsShaking = false; path.Reset(); }   // focus left Roblox: nothing to undo in another window
                        locked = false; fgWasRoblox = false; stableMs = 0; lostCount = 0;
                        Status = "Waiting for Roblox to be in front";
                    }
                    else if (GetClientRect(hwnd, out var cr) && cr.R - cr.L >= 32 && cr.B - cr.T >= 32)
                    {
                        center = new NativeMethods.POINT { X = (cr.L + cr.R) / 2, Y = (cr.T + cr.B) / 2 };
                        ClientToScreen(hwnd, ref center);
                        NativeMethods.GetCursorPos(out var cur);
                        if (!fgWasRoblox) { fgWasRoblox = true; locked = false; stableMs = 0; lostCount = 0; }
                        double dist = Math.Sqrt((double)(cur.X - center.X) * (cur.X - center.X) + (double)(cur.Y - center.Y) * (cur.Y - center.Y));

                        if (!locked)
                        {
                            bool clipped = GetClipCursor(out var clip) && clip.R - clip.L <= 6 && clip.B - clip.T <= 6
                                && center.X >= clip.L - 3 && center.X <= clip.R + 3 && center.Y >= clip.T - 3 && center.Y <= clip.B + 3;
                            if (clipped) { locked = true; path.Reset(); lostCount = 0; }
                            else
                            {
                                stableMs = dist > 3 ? 0 : stableMs + 50;
                                if (stableMs >= 150 && now >= retryAt)
                                {
                                    // Nudge the cursor 3 px: a locked Roblox puts it straight back in the middle, an unlocked one leaves it.
                                    NativeMethods.SetCursorPos(center.X + 3, center.Y);
                                    Thread.Sleep(90);
                                    NativeMethods.GetCursorPos(out var after);
                                    double back = Math.Sqrt((double)(after.X - center.X) * (after.X - center.X) + (double)(after.Y - center.Y) * (after.Y - center.Y));
                                    if (back <= 1.5) { locked = true; path.Reset(); lostCount = 0; }
                                    else
                                    {
                                        if (after.X == center.X + 3 && after.Y == center.Y) NativeMethods.SetCursorPos(center.X, center.Y);
                                        retryAt = NowMs() + 2000;
                                    }
                                    stableMs = 0;
                                }
                            }
                            Status = locked ? "Mouse lock found" : "Go into first person (mouse lock) in Roblox";
                        }
                        else
                        {
                            bool unchanged = cur.X == lastX && cur.Y == lastY;
                            if (dist <= amp * 1.25 + 12 || (!unchanged && dist <= 250)) lostCount = 0;
                            else if (++lostCount > 2)
                            {
                                EndShake(); locked = false; lostCount = 0; stableMs = 0; path.Reset();   // a menu opened or the mouse was released
                                Status = "Go into first person (mouse lock) in Roblox";
                            }
                        }
                        lastX = cur.X; lastY = cur.Y;
                    }
                }

                bool allowed = locked && fgWasRoblox && (!_s.OnlyWhileClicking || Clicking()) && (!_s.UseKey || KeyActive());
                if (allowed)
                {
                    var o = path.Sample(NowMs());
                    SendOffset((int)Math.Round(o.X), (int)Math.Round(o.Y));
                    IsShaking = true; Status = "Shaking";
                    if (timer != IntPtr.Zero && timer != new IntPtr(-1))
                    {
                        long due = -40000;   // 4 ms
                        NativeMethods.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                        NativeMethods.WaitForSingleObject(timer, 50);
                    }
                    else Thread.Sleep(4);
                }
                else
                {
                    if (IsShaking) { if (locked && fgWasRoblox) SendOffset(0, 0); else { _ix = _iy = 0; } path.Reset(); IsShaking = false; }
                    if (locked) Status = _s.UseKey ? "Ready: use your shake key" : (_s.OnlyWhileClicking ? "Ready: shakes while the Auto Clicker clicks" : "Ready");
                    _wake.Wait(locked ? 10 : 50); _wake.Reset();
                }
            }
        }
        finally
        {
            if (IsShaking) { try { SendOffset(0, 0); } catch { } }
            IsShaking = false;
            if (timer != IntPtr.Zero && timer != new IntPtr(-1)) NativeMethods.CloseHandle(timer);
        }

        void EndShake()
        {
            if (IsShaking) { try { SendOffset(0, 0); } catch { } IsShaking = false; }
            _ix = _iy = 0;
        }
    }
}
