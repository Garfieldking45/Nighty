using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Share macros as a short code (to paste in chat), a .nightymacro file, or a link. Only the macro's steps and settings travel:
/// no keys of yours, no paths. Imported macros always arrive with their hotkey switched off.
/// </summary>
public static class MacroShare
{
    public const string Prefix = "NM1:";
    public const int MaxSteps = 5000;
    private const int MaxBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private sealed class ShareStep { public MacroStepType Type { get; set; } public int Value { get; set; } public int Value2 { get; set; } public int Value3 { get; set; } public string Text { get; set; } = ""; }
    private sealed class ShareFile
    {
        public string Format { get; set; } = "nighty-macro";
        public int Version { get; set; } = 1;
        public string Name { get; set; } = "Shared macro";
        public int Repeat { get; set; } = 1;
        public int RepeatDelayMs { get; set; }
        public double Speed { get; set; } = 1;
        public bool OnlyInRoblox { get; set; }
        public bool HoldMode { get; set; }
        public List<ShareStep> Steps { get; set; } = new();
    }

    private static ShareFile ToFile(MacroDef m) => new()
    {
        Name = m.Name, Repeat = m.Repeat, RepeatDelayMs = m.RepeatDelayMs, Speed = m.Speed, OnlyInRoblox = m.OnlyInRoblox, HoldMode = m.HoldMode,
        Steps = m.Steps.Select(s => new ShareStep { Type = s.Type, Value = s.Value, Value2 = s.Value2, Value3 = s.Value3, Text = s.Text }).ToList(),
    };

    public static string ToJson(MacroDef m) => JsonSerializer.Serialize(ToFile(m), new JsonSerializerOptions(Json) { WriteIndented = true });

    /// <summary>A compact code: "NM1:" plus the deflated JSON as URL-safe base64.</summary>
    public static string ToCode(MacroDef m)
    {
        var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ToFile(m), Json));
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
        return Prefix + Convert.ToBase64String(ms.ToArray()).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>Reads a code, a pasted JSON file body, or text that contains a code. Returns a new macro, or an error.</summary>
    public static (MacroDef? Macro, string? Error) Parse(string text)
    {
        try
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return (null, "The clipboard is empty. Copy a macro code first.");
            if (text.Length > MaxBytes) return (null, "That is too big to be a macro.");
            string json;
            var codeMatch = Regex.Match(text, Regex.Escape(Prefix) + @"[A-Za-z0-9_\-+/=]+");
            if (codeMatch.Success)
            {
                var b64 = codeMatch.Value[Prefix.Length..].Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                using var input = new MemoryStream(Convert.FromBase64String(b64));
                using var z = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buf = new byte[8192]; int n; long total = 0;
                while ((n = z.Read(buf, 0, buf.Length)) > 0) { total += n; if (total > MaxBytes) return (null, "That is too big to be a macro."); output.Write(buf, 0, n); }
                json = Encoding.UTF8.GetString(output.ToArray());
            }
            else if (text.StartsWith('{')) json = text;
            else return (null, "That doesn't look like a Nighty macro.");

            var f = JsonSerializer.Deserialize<ShareFile>(json, Json);
            if (f == null || f.Format != "nighty-macro") return (null, "That doesn't look like a Nighty macro.");
            if (f.Steps.Count == 0) return (null, "That macro has no steps.");
            if (f.Steps.Count > MaxSteps) return (null, $"A macro can have up to {MaxSteps} steps.");

            var m = new MacroDef
            {
                Name = Clean(f.Name, 40, "Shared macro"), Repeat = Math.Clamp(f.Repeat, 0, 1_000_000), RepeatDelayMs = Math.Clamp(f.RepeatDelayMs, 0, 600000),
                Speed = Math.Clamp(f.Speed, 0.1, 10), OnlyInRoblox = f.OnlyInRoblox, HoldMode = f.HoldMode,
                HotkeyEnabled = false, HotkeyVk = 0, HotkeyMods = 0,   // a shared macro never claims one of your keys
            };
            foreach (var s in f.Steps)
            {
                if (!Enum.IsDefined(s.Type)) return (null, "That macro uses a step this version doesn't know.");
                m.Steps.Add(new MacroStep
                {
                    Type = s.Type, Value = Math.Clamp(s.Value, -100000, 100000), Value2 = Math.Clamp(s.Value2, -100000, 100000), Value3 = Math.Clamp(s.Value3, 0, 100000),
                    Text = Clean(s.Text, 200, ""),
                });
            }
            return (m, null);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException or InvalidOperationException)
        {
            return (null, "That file couldn't be read as a macro.");
        }
    }

    private static string Clean(string? s, int max, string fallback)
    {
        s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length == 0 ? fallback : s.Length > max ? s[..max] : s;
    }

    /// <summary>Downloads an https link to a .nightymacro file or to a page (raw paste, Discord attachment) that contains a macro code.</summary>
    public static async Task<(MacroDef? Macro, string? Error)> DownloadAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return (null, "That isn't a valid https:// link.");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Nighty-macro-import");
            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return (null, $"The site answered {(int)resp.StatusCode} instead of the file. Check the link.");
            if (resp.Content.Headers.ContentLength > MaxBytes) return (null, "That file is too big to be a macro.");
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buf = new byte[8192]; int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0) { ms.Write(buf, 0, n); if (ms.Length > MaxBytes) return (null, "That file is too big to be a macro."); }
            return Parse(Encoding.UTF8.GetString(ms.ToArray()));
        }
        catch (TaskCanceledException) { return (null, "The site didn't answer in time."); }
        catch (HttpRequestException) { return (null, "Couldn't reach the site. Check your internet and the link."); }
    }
}

/// <summary>
/// Records what you type and click, with its timing, into macro steps. Keys and buttons pressed and released quickly become one
/// Press / Click step, longer holds become Hold + Release, and gaps become Waits. Movement is only kept at clicks.
/// Input sent by programs (including Nighty's own macros) and anything while Nighty is in front is ignored.
/// </summary>
public sealed class MacroRecorder
{
    private enum Kind { KeyDown, KeyUp, MouseDown, MouseUp, Wheel }
    private readonly record struct Ev(long T, Kind K, int Code, int X, int Y);

    private readonly List<Ev> _events = new();
    private readonly object _gate = new();
    private IntPtr _kb, _ms;
    private NativeMethods.LowLevelProc? _kbProc, _msProc;
    private uint _threadId;
    private bool _running;
    private long _t0;

    public bool IsRecording => _running;
    public event Action? StopRequested;   // Esc was pressed

    public bool Start()
    {
        if (_running) return true;
        lock (_gate) _events.Clear();
        _t0 = Wait.Now;
        var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            _kbProc = KeyCallback; _msProc = MouseCallback;
            var mod = NativeMethods.GetModuleHandle(null);
            _kb = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _kbProc, mod, 0);
            _ms = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _msProc, mod, 0);
            _threadId = NativeMethods.GetCurrentThreadId();
            ready.Set();
            if (_kb == IntPtr.Zero || _ms == IntPtr.Zero) { if (_kb != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_kb); if (_ms != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_ms); return; }
            while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            NativeMethods.UnhookWindowsHookEx(_kb); NativeMethods.UnhookWindowsHookEx(_ms);
        }) { IsBackground = true, Name = "Nighty macro recorder" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait(2000);
        _running = _kb != IntPtr.Zero && _ms != IntPtr.Zero;
        if (!_running) Log.Warn("Macro recording hooks could not be installed");
        return _running;
    }

    /// <summary>Stops recording and returns the steps (at most <see cref="MacroShare.MaxSteps"/>).</summary>
    public List<MacroStep> Stop()
    {
        if (_running) { _running = false; NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero); }
        List<Ev> ev;
        lock (_gate) ev = _events.ToList();
        return Convert(ev);
    }

    private IntPtr KeyCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _running)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam, 0);
            uint flags = (uint)Marshal.ReadInt32(lParam, 8);
            bool injected = (flags & NativeMethods.LLKHF_INJECTED) != 0;
            bool down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            if (!injected && !RobloxService.IsOwnWindowForeground())
            {
                if (vk == 0x1B && down) StopRequested?.Invoke();   // Esc ends the recording and is not recorded
                else lock (_gate) if (_events.Count < MacroShare.MaxSteps * 3) _events.Add(new Ev(Wait.Now, down ? Kind.KeyDown : Kind.KeyUp, vk, 0, 0));
            }
        }
        return NativeMethods.CallNextHookEx(_kb, code, wParam, lParam);
    }

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _running)
        {
            int msg = (int)wParam;
            int x = Marshal.ReadInt32(lParam, 0), y = Marshal.ReadInt32(lParam, 4);
            int data = Marshal.ReadInt32(lParam, 8);
            uint flags = (uint)Marshal.ReadInt32(lParam, 12);
            bool injected = (flags & 1) != 0;
            if (!injected && !RobloxService.IsOwnWindowForeground())
            {
                Ev? e = msg switch
                {
                    0x201 => new Ev(Wait.Now, Kind.MouseDown, 0, x, y), 0x202 => new Ev(Wait.Now, Kind.MouseUp, 0, x, y),
                    0x204 => new Ev(Wait.Now, Kind.MouseDown, 1, x, y), 0x205 => new Ev(Wait.Now, Kind.MouseUp, 1, x, y),
                    0x207 => new Ev(Wait.Now, Kind.MouseDown, 2, x, y), 0x208 => new Ev(Wait.Now, Kind.MouseUp, 2, x, y),
                    0x20A => new Ev(Wait.Now, Kind.Wheel, (short)(data >> 16) / 120, x, y),
                    _ => null,
                };
                if (e is { } ev) lock (_gate) if (_events.Count < MacroShare.MaxSteps * 3) _events.Add(ev);
            }
        }
        return NativeMethods.CallNextHookEx(_ms, code, wParam, lParam);
    }

    private static List<MacroStep> Convert(List<Ev> ev)
    {
        const double ShortPressMs = 200;
        var steps = new List<MacroStep>();
        var shortDown = new HashSet<int>();
        long prevT = ev.Count > 0 ? ev[0].T : 0;
        var heldKey = new Dictionary<int, int>();       // vk -> index in ev of its (still unmatched) down
        var heldBtn = new Dictionary<int, int>();
        var skip = new HashSet<int>();                  // up events that were folded into a Press / Click
        // pass 1: decide which downs are short presses
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            if (e.K == Kind.KeyDown) { if (!heldKey.ContainsKey(e.Code)) heldKey[e.Code] = i; }
            else if (e.K == Kind.KeyUp && heldKey.Remove(e.Code, out int d)) { if (Wait.ToMs(e.T - ev[d].T) < ShortPressMs) { skip.Add(i); shortDown.Add(d); } }
            else if (e.K == Kind.MouseDown) { if (!heldBtn.ContainsKey(e.Code)) heldBtn[e.Code] = i; }
            else if (e.K == Kind.MouseUp && heldBtn.Remove(e.Code, out int md)) { if (Wait.ToMs(e.T - ev[md].T) < ShortPressMs) { skip.Add(i); shortDown.Add(md); } }
        }
        int lastX = int.MinValue, lastY = int.MinValue;
        var seenDown = new HashSet<int>();
        for (int i = 0; i < ev.Count && steps.Count < MacroShare.MaxSteps - 2; i++)
        {
            var e = ev[i];
            if (skip.Contains(i)) { shortDown.Remove(i); continue; }
            if (e.K == Kind.KeyDown && seenDown.Contains(e.Code) && !shortDown.Contains(i)) { continue; }   // auto-repeat while held
            double gap = Wait.ToMs(e.T - prevT);
            if (gap >= 20 && steps.Count > 0) steps.Add(new MacroStep { Type = MacroStepType.Wait, Value = (int)(Math.Round(gap / 5) * 5) });
            prevT = e.T;
            switch (e.K)
            {
                case Kind.KeyDown:
                    seenDown.Add(e.Code);
                    steps.Add(new MacroStep { Type = shortDown.Contains(i) ? MacroStepType.KeyPress : MacroStepType.KeyDown, Value = e.Code });
                    if (shortDown.Contains(i)) seenDown.Remove(e.Code);
                    break;
                case Kind.KeyUp:
                    seenDown.Remove(e.Code);
                    steps.Add(new MacroStep { Type = MacroStepType.KeyUp, Value = e.Code });
                    break;
                case Kind.MouseDown:
                case Kind.MouseUp:
                    if (e.K == Kind.MouseDown && (Math.Abs(e.X - lastX) > 3 || Math.Abs(e.Y - lastY) > 3))
                    {
                        steps.Add(new MacroStep { Type = MacroStepType.MoveTo, Value = e.X, Value2 = e.Y });
                        lastX = e.X; lastY = e.Y;
                    }
                    if (e.K == Kind.MouseDown) steps.Add(new MacroStep { Type = shortDown.Contains(i) ? MacroStepType.Click : MacroStepType.MouseDown, Value = e.Code });
                    else steps.Add(new MacroStep { Type = MacroStepType.MouseUp, Value = e.Code });
                    break;
                case Kind.Wheel:
                    if (e.Code != 0) steps.Add(new MacroStep { Type = MacroStepType.Scroll, Value = e.Code });
                    break;
            }
            shortDown.Remove(i);
        }
        return steps;
    }

}
