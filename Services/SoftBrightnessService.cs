using System.Runtime.InteropServices;

namespace Nighty.Services;

/// <summary>
/// Software brightness through the display's gamma ramp. Below 100% it dims; above 100% it lifts dark and mid tones
/// (the same idea as a gamma slider), which is what makes dark areas easier to see. Windows and some drivers limit how far
/// a ramp may stray from normal, so the boost steps down until the driver accepts it and reports the cap it found.
/// The original ramp is saved the first time and put back on Reset and when Nighty closes.
/// </summary>
public sealed class SoftBrightnessService
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr dc, [In] ushort[] ramp);
    [DllImport("gdi32.dll")] private static extern bool GetDeviceGammaRamp(IntPtr dc, [Out] ushort[] ramp);

    private ushort[]? _original;

    public int Level { get; private set; } = 100;
    /// <summary>Highest boost the driver accepted this session (150 until a limit is found).</summary>
    public int Cap { get; private set; } = 150;
    public bool IsActive => Level != 100;

    public (bool Ok, string Message) Set(int level)
    {
        level = Math.Clamp(level, 40, 150);
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return (false, "Windows didn't give access to the display.");
        try
        {
            if (_original == null)
            {
                var orig = new ushort[768];
                if (!GetDeviceGammaRamp(dc, orig)) return (false, "This display doesn't allow brightness changes.");
                _original = orig;
            }
            if (level == 100) return Reset(dc);

            int tryLevel = level;
            while (true)
            {
                if (SetDeviceGammaRamp(dc, Build(tryLevel)))
                {
                    Level = tryLevel;
                    if (tryLevel < level) { Cap = tryLevel; return (true, $"Windows caps the software boost on this display at {tryLevel}%."); }
                    return (true, level > 100 ? $"Software boost +{level - 100}%." : $"Dimmed to {level}%.");
                }
                if (tryLevel <= 100) return (false, level > 100 ? "Software boost isn't available on this display." : "Windows limits software dimming on this display.");
                tryLevel -= 5;
            }
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    public (bool Ok, string Message) Reset()
    {
        var dc = GetDC(IntPtr.Zero);
        try { return Reset(dc); }
        finally { if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc); }
    }

    private (bool Ok, string Message) Reset(IntPtr dc)
    {
        if (_original == null) { Level = 100; return (true, "Brightness is normal."); }
        bool ok = dc != IntPtr.Zero && SetDeviceGammaRamp(dc, _original);
        if (ok) Level = 100;
        return ok ? (true, "Software brightness reset.") : (false, "Windows didn't accept the reset. It resets when you sign out.");
    }

    /// <summary>Restores the display on exit. Safe to call when nothing was changed.</summary>
    public void RestoreOnExit() { if (IsActive) Reset(); }

    private static ushort[] Build(int level)
    {
        var ramp = new ushort[768];
        for (int i = 0; i < 256; i++)
        {
            double x = i / 255.0;
            double y = level >= 100 ? Math.Pow(x, 100.0 / level) : x * (level / 100.0);
            ushort v = (ushort)Math.Clamp(Math.Round(y * 65535), 0, 65535);
            ramp[i] = ramp[256 + i] = ramp[512 + i] = v;
        }
        return ramp;
    }
}
