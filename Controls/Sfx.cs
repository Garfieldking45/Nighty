using System.IO;
using Nighty.Services;

namespace Nighty.Controls;

/// <summary>
/// Little synthesized UI sounds (no audio files shipped). Only play when Settings > Sound effects is on.
/// The Halloween and Christmas themes bring their own sound packs: eerie tones and a tolling bell for Halloween,
/// sleigh-bell chimes and a jingle for Christmas. Every other theme uses the plain tones.
/// </summary>
public static class Sfx
{
    private const int Rate = 22050;
    private static readonly Dictionary<string, System.Media.SoundPlayer> Cache = new();
    private static System.Media.SoundPlayer? _clip;

    private static bool Enabled { get { try { return Svc.Settings != null && Svc.S.General.SoundEffects; } catch { return false; } } }
    private static string Pack
    {
        get
        {
            try { var t = Svc.S.General.Theme; return t is "halloween" or "christmas" && Seasons.IsAvailable(t) ? t : "plain"; }
            catch { return "plain"; }
        }
    }

    public static void Click() { if (Enabled) Play("click"); }
    public static void Toggle(bool on) { if (Enabled) Play(on ? "on" : "off"); }
    public static void Start() { if (Enabled) Play("start"); }
    public static void Stop() { if (Enabled) Play("stop"); }

    /// <summary>The themed start-up sound, played with the loading screen. Plain themes have none.</summary>
    public static void Launch(string pack) { if (Enabled && pack is "halloween" or "christmas") PlayPack(pack, "launch"); }

    /// <summary>Plays a theme's sound regardless of the sound setting (the owner's preview).</summary>
    public static void Preview(string pack, string kind) => PlayPack(pack, kind);

    /// <summary>The clip-saved chime. Controlled by its own setting on the Record page, not the general sound switch.</summary>
    public static void Clip()
    {
        try { _clip ??= Wrap(Tone(1568, 150, 0.28)); _clip.Play(); } catch { }
    }

    private static void Play(string kind) => PlayPack(Pack, kind);

    private static long _lastPlayTick;

    private static void PlayPack(string pack, string kind)
    {
        try
        {
            // Never stack or restart sounds in a burst (a page full of controls changing at once): one sound per short gap.
            long now = Environment.TickCount64;
            bool special = kind == "launch";
            if (!special && now - _lastPlayTick < 120) return;
            _lastPlayTick = now;
            string key = pack + "/" + kind;
            lock (Cache)
            {
                if (!Cache.TryGetValue(key, out var p)) Cache[key] = p = Wrap(Render(pack, kind));
                p.Play();
            }
        }
        catch { /* sound is optional */ }
    }

    private static short[] Render(string pack, string kind) => pack switch
    {
        "halloween" => Spooky(kind),
        "christmas" => Bells(kind),
        _ => kind switch
        {
            "click" => Tone(880, 28, 0.18),
            "on" => Tone(1175, 45, 0.2),
            "off" => Tone(740, 45, 0.2),
            "start" => Tone(1320, 80, 0.22),
            _ => Tone(660, 90, 0.22),
        },
    };

    /// <summary>16-bit mono PCM wrapped in a WAV header.</summary>
    private static System.Media.SoundPlayer Wrap(short[] samples)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, true))
        {
            int n = samples.Length;
            w.Write("RIFF"u8.ToArray()); w.Write(36 + n * 2); w.Write("WAVEfmt "u8.ToArray());
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(n * 2);
            foreach (var v in samples) w.Write(v);
        }
        ms.Position = 0;
        var p = new System.Media.SoundPlayer(ms);
        p.Load();
        return p;
    }

    private static short S(double v) => (short)(Math.Clamp(v, -1, 1) * short.MaxValue);
    private static short[] Finish(double[] buf) => buf.Select(S).ToArray();

    /// <summary>A sine with a short attack and decay.</summary>
    private static short[] Tone(double hz, int ms, double volume)
    {
        int n = Rate * ms / 1000;
        var d = new short[n];
        for (int i = 0; i < n; i++)
        {
            double env = Math.Min(1, i / (Rate * 0.004)) * Math.Pow(1 - (double)i / n, 1.6);
            d[i] = S(Math.Sin(2 * Math.PI * hz * i / Rate) * env * volume);
        }
        return d;
    }

    // ---- Christmas: bells ----
    private static readonly (double Ratio, double Amp, double Decay)[] BellPartials = { (1, 1, 5), (2.76, 0.55, 7), (5.4, 0.28, 10), (8.93, 0.14, 14) };

    private static void AddBell(double[] buf, double hz, double startSec, double vol, double lengthSec)
    {
        int s0 = (int)(startSec * Rate), n = (int)(lengthSec * Rate);
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double t = (double)i / Rate, v = 0;
            foreach (var (r, a, dec) in BellPartials) v += a * Math.Sin(2 * Math.PI * hz * r * t) * Math.Exp(-dec * t);
            buf[s0 + i] += v * vol * Math.Min(1, i / (Rate * 0.002));
        }
    }

    private static short[] Bells(string kind)
    {
        double[] b;
        switch (kind)
        {
            case "click": b = new double[Rate * 14 / 100]; AddBell(b, 1760, 0, 0.16, 0.14); break;
            case "on": b = new double[Rate * 3 / 10]; AddBell(b, 1319, 0, 0.2, 0.3); break;
            case "off": b = new double[Rate * 3 / 10]; AddBell(b, 988, 0, 0.2, 0.3); break;
            case "start": b = new double[Rate * 45 / 100]; AddBell(b, 1568, 0, 0.2, 0.3); AddBell(b, 2093, 0.09, 0.2, 0.36); break;
            case "stop": b = new double[Rate * 4 / 10]; AddBell(b, 784, 0, 0.22, 0.4); break;
            default:   // launch: the opening of a well-known carol
                b = new double[Rate * 5 / 2];
                double[] notes = { 659, 659, 659, 659, 659, 659, 659, 784, 523, 587, 659 };
                double[] at = { 0, 0.18, 0.36, 0.62, 0.8, 0.98, 1.24, 1.42, 1.6, 1.72, 1.84 };
                for (int i = 0; i < notes.Length; i++) AddBell(b, notes[i], at[i], 0.18, 0.5);
                break;
        }
        return Finish(b);
    }

    // ---- Halloween: eerie tones, creaks and a tolling bell ----
    private static short[] Spooky(string kind)
    {
        var rnd = new Random(5);
        double[] b;
        switch (kind)
        {
            case "click":   // a soft wooden knock
                b = new double[Rate * 7 / 100];
                for (int i = 0; i < b.Length; i++)
                {
                    double t = (double)i / Rate;
                    b[i] = (Math.Sin(2 * Math.PI * 150 * t) * 0.6 + (rnd.NextDouble() - 0.5) * 0.25) * Math.Exp(-60 * t) * 0.5;
                }
                break;
            case "on": b = Ghost(new[] { 330.0, 392.0 }, 0.16, 0.18); break;
            case "off": b = Ghost(new[] { 392.0, 294.0 }, 0.16, 0.18); break;
            case "start": b = Ghost(new[] { 247.0, 262.0, 370.0 }, 0.13, 0.2); break;
            case "stop": b = Ghost(new[] { 220.0, 165.0 }, 0.2, 0.2); break;
            default:   // launch: a creaking door, then a bell toll
                b = new double[(int)(Rate * 1.8)];
                int creakLen = (int)(Rate * 0.8);
                for (int i = 0; i < creakLen; i++)
                {
                    double t = (double)i / Rate, f = 90 + 70 * Math.Sin(t * 2.2) + 40 * t;
                    double creak = Math.Sin(2 * Math.PI * f * t + 6 * Math.Sin(2 * Math.PI * 23 * t)) * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 11 * t));
                    b[i] += creak * 0.16 * Math.Min(1, i / (Rate * 0.1)) * Math.Min(1, (creakLen - i) / (Rate * 0.2));
                }
                AddBell(b, 311, 0.75, 0.22, 0.85);
                AddBell(b, 233, 0.95, 0.14, 0.65);
                break;
        }
        return Finish(b);
    }

    /// <summary>A wavering, hollow "ghost" tone gliding through the given notes.</summary>
    private static double[] Ghost(double[] notes, double secPerNote, double vol)
    {
        int per = (int)(secPerNote * Rate), tail = (int)(0.05 * Rate), n = per * notes.Length + tail;
        var b = new double[n];
        for (int k = 0; k < notes.Length; k++)
            for (int i = 0; i < per + tail && k * per + i < n; i++)
            {
                double t = (double)i / Rate, vib = 1 + 0.012 * Math.Sin(2 * Math.PI * 6 * t);
                double v = Math.Sin(2 * Math.PI * notes[k] * vib * t) + 0.4 * Math.Sin(2 * Math.PI * notes[k] * 2.01 * t);
                double env = Math.Min(1, i / (Rate * 0.015)) * Math.Exp(-5.5 * t);
                b[k * per + i] += v * env * vol;
            }
        return b;
    }
}
