using System.Runtime.InteropServices;

namespace Nighty.Native;

/// <summary>Copies a rectangle of the screen into a reusable 32-bit buffer (BGRA) with plain GDI. No per-frame allocations.</summary>
internal sealed class ScreenGrabber : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPels, biYPels, biClrUsed, biClrImportant;
    }

    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    public readonly int Width, Height;
    public readonly byte[] Pixels;   // BGRA, top-down
    private readonly IntPtr _screen, _mem, _bmp, _old, _bits;

    public ScreenGrabber(int width, int height)
    {
        Width = width; Height = height;
        Pixels = new byte[width * height * 4];
        _screen = GetDC(IntPtr.Zero);
        _mem = CreateCompatibleDC(_screen);
        var bmi = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        _bmp = CreateDIBSection(_screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        _old = SelectObject(_mem, _bmp);
    }

    /// <summary>Grabs the screen rectangle whose top-left is (x, y) into <see cref="Pixels"/>.</summary>
    public bool Grab(int x, int y)
    {
        if (_bits == IntPtr.Zero) return false;
        if (!BitBlt(_mem, 0, 0, Width, Height, _screen, x, y, SRCCOPY | CAPTUREBLT)) return false;
        Marshal.Copy(_bits, Pixels, 0, Pixels.Length);
        return true;
    }

    public void Dispose()
    {
        SelectObject(_mem, _old);
        DeleteObject(_bmp);
        DeleteDC(_mem);
        ReleaseDC(IntPtr.Zero, _screen);
    }
}
