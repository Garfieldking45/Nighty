using System.Runtime.InteropServices;

namespace Nighty.Services;

public sealed record DisplayMode(int Width, int Height, int Hz)
{
    public string Ratio
    {
        get
        {
            double r = (double)Width / Height;
            if (Math.Abs(r - 4.0 / 3) < 0.01) return "4:3";
            if (Math.Abs(r - 5.0 / 4) < 0.01) return "5:4";
            if (Math.Abs(r - 16.0 / 9) < 0.01) return "16:9";
            if (Math.Abs(r - 16.0 / 10) < 0.01) return "16:10";
            if (Math.Abs(r - 21.0 / 9) < 0.03) return "21:9";
            return $"{r:0.##}:1";
        }
    }
    public string Label => $"{Width} x {Height}  ({Ratio}, {Hz} Hz)";
}

/// <summary>
/// Stretched resolution for the main display. A lower or different-shaped resolution is applied with the driver's
/// "fill the screen" scaling (DMDFO_STRETCH), so a 4:3 or 5:4 picture covers the whole panel with no black bars.
/// The change is made with CDS_FULLSCREEN, which Windows only keeps for the life of this process: closing Nighty,
/// signing out or rebooting puts the normal resolution back by itself. Restore also puts it back at any time.
/// </summary>
public sealed class ResolutionService
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CDS_TEST = 0x2, CDS_FULLSCREEN = 0x4;
    private const uint DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000, DM_DISPLAYFIXEDOUTPUT = 0x20000000;
    private const uint DMDFO_DEFAULT = 0, DMDFO_STRETCH = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsExW(string? device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsExW(string? device, IntPtr dm, IntPtr hwnd, int flags, IntPtr param);

    public bool IsChanged { get; private set; }

    private static DEVMODE New() => new() { dmDeviceName = "", dmFormName = "", dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

    public DisplayMode? Current()
    {
        var dm = New();
        return EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref dm) ? new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency) : null;
    }

    /// <summary>Every resolution the main display offers, at the current refresh rate when it has it, otherwise its best rate.</summary>
    public List<DisplayMode> Modes()
    {
        var cur = Current();
        var best = new Dictionary<(int, int), int>();
        var dm = New();
        for (int i = 0; EnumDisplaySettingsW(null, i, ref dm); i++)
        {
            if (dm.dmBitsPerPel < 32) continue;
            var key = ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight);
            int hz = (int)dm.dmDisplayFrequency;
            if (!best.TryGetValue(key, out var old)) best[key] = hz;
            else if (cur != null && hz == cur.Hz) best[key] = hz;
            else if ((cur == null || old != cur.Hz) && hz > old) best[key] = hz;
        }
        return best.Select(kv => new DisplayMode(kv.Key.Item1, kv.Key.Item2, kv.Value))
                   .OrderByDescending(m => m.Width).ThenByDescending(m => m.Height).ToList();
    }

    public (bool Ok, string Message) Apply(DisplayMode mode, bool stretch)
    {
        var dm = New();
        if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref dm)) return (false, "Windows didn't report the current display mode.");
        dm.dmPelsWidth = (uint)mode.Width; dm.dmPelsHeight = (uint)mode.Height; dm.dmDisplayFrequency = (uint)mode.Hz;
        dm.dmDisplayFixedOutput = stretch ? DMDFO_STRETCH : DMDFO_DEFAULT;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_DISPLAYFIXEDOUTPUT;

        if (ChangeDisplaySettingsExW(null, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero) != 0 && stretch)
        {
            // Some drivers reject the stretch flag; try the plain mode and say so.
            dm.dmFields &= ~DM_DISPLAYFIXEDOUTPUT;
            if (ChangeDisplaySettingsExW(null, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero) != 0)
                return (false, "Windows or the graphics driver doesn't allow that resolution.");
            int r2 = ChangeDisplaySettingsExW(null, ref dm, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero);
            if (r2 != 0) return (false, $"Windows refused the change (code {r2}).");
            IsChanged = true;
            return (true, $"Resolution set to {mode.Width} x {mode.Height}. The driver doesn't offer stretching, so the picture may have black bars (try GPU scaling in your graphics control panel).");
        }
        int r = ChangeDisplaySettingsExW(null, ref dm, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero);
        if (r != 0) return (false, r == -2 ? "Windows or the graphics driver doesn't allow that resolution." : $"Windows refused the change (code {r}).");
        IsChanged = true;
        return (true, stretch ? $"Resolution set to {mode.Width} x {mode.Height}, stretched to fill the screen." : $"Resolution set to {mode.Width} x {mode.Height}.");
    }

    public (bool Ok, string Message) Restore()
    {
        int r = ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (r != 0) return (false, $"Windows didn't accept the reset (code {r}). It resets when Nighty closes.");
        IsChanged = false;
        return (true, "Back to your normal resolution.");
    }

    public void RestoreOnExit() { if (IsChanged) Restore(); }
}
