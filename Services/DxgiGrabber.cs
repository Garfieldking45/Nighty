using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Nighty.Services;

/// <summary>
/// Grabs the primary monitor with DXGI Desktop Duplication: the copy happens on the graphics card, so it stays cheap even on a
/// very large display (a GDI screen copy of the same image costs ~55 ms). Frames come back as top-down BGRA. When nothing on screen
/// changed Windows reports no new frame, and the previous one is simply kept.
/// </summary>
internal sealed class DxgiGrabber : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _staging;
    private int _staleCount;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool Ready => _dup != null && _staging != null;

    public bool Init()
    {
        try
        {
            Dispose();
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 }, out _device, out _ctx).CheckError();
            using var dxgi = _device!.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            adapter.EnumOutputs(0, out var output).CheckError();
            using (output)
            using (var output1 = output!.QueryInterface<IDXGIOutput1>())
            {
                _dup = output1.DuplicateOutput(_device);
                var d = _dup.Description.ModeDescription;
                Width = (int)d.Width; Height = (int)d.Height;
            }
            var desc = new Texture2DDescription
            {
                Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
            };
            _staging = _device.CreateTexture2D(desc);
            return true;
        }
        catch (Exception ex)
        {
            Log.Info("Desktop Duplication isn't available (" + ex.Message + "); using the slower screen copy");
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// Fills <paramref name="dest"/> (Width x Height x 4, top-down BGRA) with the newest frame. Returns false when the capture
    /// has to be restarted (display mode change, secure desktop).
    /// </summary>
    public bool Grab(byte[] dest, int timeoutMs = 0)
    {
        if (_dup == null || _staging == null || _ctx == null) return false;
        var r = _dup.AcquireNextFrame((uint)timeoutMs, out _, out var resource);
        if (r.Failure)
        {
            // timeout = nothing changed, keep the last frame; anything else (lost access) needs a restart
            if (r.Code == unchecked((int)0x887A0027)) { _staleCount++; return true; }   // DXGI_ERROR_WAIT_TIMEOUT
            return false;
        }
        try
        {
            using var tex = resource!.QueryInterface<ID3D11Texture2D>();
            _ctx.CopyResource(_staging, tex);
        }
        finally { resource?.Dispose(); _dup.ReleaseFrame(); }

        var map = _ctx.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowBytes = Width * 4;
            if (map.RowPitch == rowBytes) Marshal.Copy(map.DataPointer, dest, 0, rowBytes * Height);
            else for (int y = 0; y < Height; y++) Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, dest, y * rowBytes, rowBytes);
        }
        finally { _ctx.Unmap(_staging, 0); }
        _staleCount = 0;
        return true;
    }

    public void Dispose()
    {
        _staging?.Dispose(); _staging = null;
        _dup?.Dispose(); _dup = null;
        _ctx?.Dispose(); _ctx = null;
        _device?.Dispose(); _device = null;
    }
}
