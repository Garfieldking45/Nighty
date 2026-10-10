using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace Nighty.Services;

/// <summary>
/// Optional Discord Rich Presence: "Using Nighty" (or what you're doing) on your Discord profile. Talks to the running
/// Discord app over its local IPC pipe, so nothing goes through any server. Needs a Discord application id (free, from the
/// Discord developer portal), entered in Settings. Off by default.
/// </summary>
public sealed class DiscordService
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private NamedPipeClientStream? _pipe;
    private string _connectedId = "";
    private long _start = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private string _lastSent = "";
    private DateTime _lastSendAt = DateTime.MinValue;
    private bool _busy;
    private int _nonce;

    /// <summary>Human-readable state shown in Settings.</summary>
    public string Status { get; private set; } = "Off. Your Discord profile doesn't show Nighty.";
    public event Action? StatusChanged;

    public DiscordService() { _timer.Tick += (_, _) => Tick(); }

    /// <summary>Call after the setting changes (or at start-up).</summary>
    public void Sync()
    {
        var g = Svc.S.General;
        if (!g.DiscordPresence) { Disconnect(); SetStatus("Off. Your Discord profile doesn't show Nighty."); _timer.Stop(); return; }
        if (string.IsNullOrWhiteSpace(g.DiscordAppId)) { Disconnect(); SetStatus("Enter your Discord application id to turn this on."); _timer.Stop(); return; }
        if (!_timer.IsEnabled) { _timer.Start(); SetStatus("Connecting to Discord…"); Tick(); }
    }

    public void Shutdown() { _timer.Stop(); Disconnect(); }

    private void SetStatus(string s) { if (Status == s) return; Status = s; StatusChanged?.Invoke(); }

    private void Tick()
    {
        if (_busy) return;
        var g = Svc.S.General;
        if (!g.DiscordPresence || string.IsNullOrWhiteSpace(g.DiscordAppId)) return;

        string details = !g.DiscordShowActivity ? "Using Nighty"
            : Svc.Clicker.IsClicking ? $"Clicking at {Svc.Clicker.MeasuredCps:0} CPS"
            : Svc.GameMode.IsActive ? "Game Mode on"
            : "Using Nighty";
        string state = g.DiscordShowTime ? "" : "";
        string key = details + "|" + g.DiscordShowTime + "|" + g.DiscordAppId;
        bool stale = (DateTime.UtcNow - _lastSendAt).TotalSeconds > 20;
        if (_pipe is { IsConnected: true } && key == _lastSent && !stale) return;

        _busy = true;
        var id = g.DiscordAppId;
        bool showTime = g.DiscordShowTime;
        Task.Run(() =>
        {
            try
            {
                if (_pipe is not { IsConnected: true } || _connectedId != id) Connect(id);
                if (_pipe is { IsConnected: true })
                {
                    SendActivity(details, state, showTime);
                    _lastSent = key; _lastSendAt = DateTime.UtcNow;
                    Svc.Dispatch(() => SetStatus("Showing on Discord as “" + details + "”."));
                }
            }
            catch (Exception ex)
            {
                Disconnect();
                Svc.Dispatch(() => SetStatus("Waiting for the Discord app. Open Discord and it shows up by itself."));
                Log.Warn("Discord presence failed", ex);
            }
            finally { _busy = false; }
        });
    }

    private void Connect(string clientId)
    {
        Disconnect();
        for (int i = 0; i < 10; i++)
        {
            try
            {
                var p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                p.Connect(250);
                _pipe = p;
                break;
            }
            catch { /* try the next pipe */ }
        }
        if (_pipe == null) throw new IOException("Discord is not running");
        Write(0, JsonSerializer.Serialize(new { v = 1, client_id = clientId }));
        var (op, reply) = Read();
        if (op == 2 || reply.Contains("\"evt\":\"ERROR\"") || reply.Contains("Invalid Client"))
        {
            Disconnect();
            Svc.Dispatch(() => SetStatus("Discord didn't accept that application id."));
            throw new IOException("Discord rejected the application id");
        }
        _connectedId = clientId;
        _start = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private void SendActivity(string details, string state, bool showTime)
    {
        var activity = new Dictionary<string, object> { ["details"] = details };
        if (state.Length > 0) activity["state"] = state;
        if (showTime) activity["timestamps"] = new { start = _start };
        var payload = new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId, activity },
            nonce = (++_nonce).ToString(),
        };
        Write(1, JsonSerializer.Serialize(payload));
        Read();   // Discord answers each command; drain it so the pipe never fills
    }

    private void Write(int op, string json)
    {
        var data = Encoding.UTF8.GetBytes(json);
        var buf = new byte[8 + data.Length];
        BitConverter.GetBytes(op).CopyTo(buf, 0);
        BitConverter.GetBytes(data.Length).CopyTo(buf, 4);
        data.CopyTo(buf, 8);
        _pipe!.Write(buf, 0, buf.Length);
        _pipe.Flush();
    }

    private (int Op, string Json) Read()
    {
        var head = new byte[8];
        ReadExactly(head, 2000);
        int op = BitConverter.ToInt32(head, 0), len = BitConverter.ToInt32(head, 4);
        if (len < 0 || len > 1 << 20) throw new IOException("Bad Discord frame");
        var body = new byte[len];
        ReadExactly(body, 2000);
        return (op, Encoding.UTF8.GetString(body));
    }

    private void ReadExactly(byte[] buf, int timeoutMs)
    {
        int got = 0;
        using var cts = new CancellationTokenSource(timeoutMs);
        while (got < buf.Length)
        {
            int n = _pipe!.ReadAsync(buf, got, buf.Length - got, cts.Token).GetAwaiter().GetResult();
            if (n <= 0) throw new IOException("Discord closed the connection");
            got += n;
        }
    }

    private void Disconnect()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null; _connectedId = ""; _lastSent = "";
    }
}
