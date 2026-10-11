using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>Round-trip time to a configurable host using ICMP. Runs only while something needs it.</summary>
public sealed class PingService
{
    private CancellationTokenSource? _cts;
    public int? LastMs { get; private set; }
    public string Status { get; private set; } = "Idle";
    public bool IsRunning => _cts != null;

    public void Start()
    {
        if (_cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using var ping = new Ping();
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var set = Svc.S.Overlays;
                    if (set.PingAdapterId.Length > 0)
                    {
                        // Through a chosen adapter: .NET's Ping cannot pick a source address, Windows' ping.exe can.
                        var src = SourceAddressOf(set.PingAdapterId);
                        if (src == null) { LastMs = null; Status = "Adapter not connected"; }
                        else
                        {
                            var ms = await PingFromAsync(src, set.PingHost, cts.Token);
                            if (ms is int v) { LastMs = v; Status = "OK"; } else { LastMs = null; Status = "Timed out"; }
                        }
                    }
                    else
                    {
                        var r = await ping.SendPingAsync(set.PingHost, 1000);
                        if (r.Status == IPStatus.Success) { LastMs = (int)r.RoundtripTime; Status = "OK"; }
                        else { LastMs = null; Status = r.Status == IPStatus.TimedOut ? "Timed out" : r.Status.ToString(); }
                    }
                }
                catch (Exception ex) { LastMs = null; Status = ex.InnerException?.Message ?? ex.Message; }
                try { await Task.Delay(1000, cts.Token); } catch { break; }
            }
        });
    }

    public void Stop() { _cts?.Cancel(); _cts = null; LastMs = null; Status = "Idle"; }

    /// <summary>The adapter's first IPv4 address, or null when it is missing or not connected.</summary>
    internal static string? SourceAddressOf(string adapterId)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.Id != adapterId || nic.OperationalStatus != OperationalStatus.Up) continue;
            var a = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return a?.Address.ToString();
        }
        return null;
    }

    /// <summary>Reads the round-trip time out of ping.exe's reply line ("time=12ms" or "time&lt;1ms"), in any Windows language.</summary>
    internal static int? ParsePingMs(string output)
    {
        var m = System.Text.RegularExpressions.Regex.Match(output, @"[=<]\s*(\d+)\s*ms", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return Math.Max(1, int.Parse(m.Groups[1].Value));   // "<1ms" reads as 1
    }

    private static async Task<int?> PingFromAsync(string sourceIp, string host, CancellationToken ct)
    {
        if (!System.Net.IPAddress.TryParse(sourceIp, out _) || host.Any(ch => char.IsWhiteSpace(ch) || ch == '"')) return null;   // nothing odd reaches the command line
        var psi = new System.Diagnostics.ProcessStartInfo("ping.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-n", "1", "-w", "1000", "-S", sourceIp, host }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi);
        if (p == null) return null;
        string text = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 ? ParsePingMs(text) : null;
    }
}

/// <summary>
/// Real Roblox frame rate by counting DXGI Present events for the Roblox process via ETW (the technique PresentMon
/// uses). ETW sessions need administrator rights or membership of "Performance Log Users", so without either this reports "unavailable" instead of a number.
/// </summary>
public sealed class FpsService
{
    private const string SessionName = "NightyFpsSession";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private TraceEventSession? _session;
    private CancellationTokenSource? _cts;
    private HashSet<int> _pids = new();
    private long _frames;

    public double? Fps { get; private set; }
    public string Status { get; private set; } = "Idle";
    public bool IsRunning => _cts != null;

    public void Start()
    {
        if (_cts != null) return;
        if (!Elevation.CanTraceEtw) { Status = "Needs one-time setup"; return; }
        var cts = _cts = new CancellationTokenSource();
        Status = "Starting";
        _ = Task.Run(() => RunSession());
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(1000, cts.Token); } catch { break; }
                _pids = Process.GetProcessesByName(RobloxService.ProcessName).Select(p => { var id = p.Id; p.Dispose(); return id; }).ToHashSet();
                long frames = Interlocked.Exchange(ref _frames, 0);
                if (_pids.Count == 0) { Fps = null; Status = "Roblox not running"; }
                else if (frames == 0) { Fps = null; Status = "No frames detected"; }
                else { Fps = frames; Status = "Measuring"; }
            }
        });
    }

    private void RunSession()
    {
        try
        {
            TraceEventSession.GetActiveSession(SessionName)?.Stop();
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue);
            _session.Source.Dynamic.All += (TraceEvent e) =>
            {
                if (e.ProviderGuid == DxgiProvider && (int)e.ID == 42 && _pids.Contains(e.ProcessID))
                    Interlocked.Increment(ref _frames);
            };
            _session.Source.Process();
        }
        catch (Exception ex)
        {
            Log.Error("FPS tracing failed", ex);
            Status = ex is UnauthorizedAccessException ? "Needs one-time setup" : "Tracing unavailable";
            Fps = null;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try { _session?.Dispose(); } catch { }
        _session = null;
        Fps = null;
        Status = "Idle";
    }
}

