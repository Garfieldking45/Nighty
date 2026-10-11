using Nighty.Native;

namespace Nighty.Services;

/// <summary>A rectangle in screen pixels, inclusive of its edges.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

/// <summary>
/// The mouse failsafe: throwing the pointer into a corner of the whole desktop stops the clicker. Corners (not edges)
/// because with two monitors an edge is crossed constantly in normal play, while a corner takes a deliberate throw.
/// </summary>
public static class StopZones
{
    public const int CornerMarginPx = 6;

    /// <summary>Edge-triggered, and starting counts as an edge so a clicker started while the pointer is already in a corner stops too.</summary>
    public static bool ShouldStop(bool inZone, bool wasInZone, bool running, bool wasRunning) =>
        running && inZone && (!wasInZone || !wasRunning);

    public static bool InCorner(ScreenRect desktop, int x, int y, int margin = CornerMarginPx)
    {
        bool nearLeftOrRight = x - desktop.Left <= margin || desktop.Right - x <= margin;
        bool nearTopOrBottom = y - desktop.Top <= margin || desktop.Bottom - y <= margin;
        return nearLeftOrRight && nearTopOrBottom;
    }

    /// <summary>Whether the live pointer is in a corner of the virtual desktop (all monitors).</summary>
    public static bool PointerInCorner()
    {
        try
        {
            // SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN
            int l = ScreenGrabber.GetSystemMetrics(76), t = ScreenGrabber.GetSystemMetrics(77);
            int w = ScreenGrabber.GetSystemMetrics(78), h = ScreenGrabber.GetSystemMetrics(79);
            if (w <= 0 || h <= 0 || !NativeMethods.GetCursorPos(out var p)) return false;
            return InCorner(new ScreenRect(l, t, l + w - 1, t + h - 1), p.X, p.Y);
        }
        catch { return false; }
    }
}
