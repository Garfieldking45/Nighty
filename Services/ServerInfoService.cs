using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Nighty.Services;

/// <summary>
/// Which Roblox server you are in. Roblox writes the server's address into its own log file when you join a game; this
/// reads the newest log (read-only, shared) and asks a free IP lookup (ip-api.com) for the region once per address.
/// Nothing is sent to Roblox, and the lookup sends only the server's address, never yours.
/// </summary>
public sealed class ServerInfoService
{
    private static readonly Regex[] AddressPatterns =
    {
        new(@"UDMUX Address = (?<ip>\d{1,3}(?:\.\d{1,3}){3})", RegexOptions.Compiled),
        new(@"Connection accepted from (?<ip>\d{1,3}(?:\.\d{1,3}){3})", RegexOptions.Compiled),
        new(@"Connecting to UDMUX server (?<ip>\d{1,3}(?:\.\d{1,3}){3})", RegexOptions.Compiled),
        new(@"Server IP:?\s*(?<ip>\d{1,3}(?:\.\d{1,3}){3})", RegexOptions.Compiled),
    };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    private readonly object _gate = new();
    private DateTime _lastScan = DateTime.MinValue;
    private string? _lastFile; private long _lastLen = -1;
    private string _lookedUp = "";

    public string? Address { get; private set; }
    public string Region { get; private set; } = "";
    public int? PingMs { get; private set; }
    public string Status { get; private set; } = "Roblox isn't running";

    /// <summary>Called a few times a second by the overlay; does its work at most every few seconds and never blocks.</summary>
    public void Poll()
    {
        if (!Svc.Roblox.IsRunning) { Address = null; Region = ""; PingMs = null; _lookedUp = ""; _lastFile = null; _lastLen = -1; Status = "Roblox isn't running"; return; }
        if ((DateTime.UtcNow - _lastScan).TotalSeconds < 4) return;
        _lastScan = DateTime.UtcNow;
        _ = Task.Run(Scan);
    }

    private async Task Scan()
    {
        try
        {
            var ip = FindAddress();
            if (ip == null) { lock (_gate) { Status = "Waiting for a game server (join a game)"; } return; }
            lock (_gate) { Address = ip; Status = ""; }
            using (var ping = new Ping())
            {
                var r = await ping.SendPingAsync(ip, 1500);
                PingMs = r.Status == IPStatus.Success ? (int)r.RoundtripTime : null;
            }
            if (_lookedUp != ip)
            {
                _lookedUp = ip;
                Region = "looking up…";
                try
                {
                    using var doc = JsonDocument.Parse(await Http.GetStringAsync($"http://ip-api.com/json/{ip}?fields=status,city,regionName,country"));
                    var root = doc.RootElement;
                    Region = root.TryGetProperty("status", out var st) && st.GetString() == "success"
                        ? string.Join(", ", new[] { "city", "regionName", "country" }.Select(k => root.TryGetProperty(k, out var v) ? v.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)))
                        : "region unknown";
                }
                catch { Region = "region unavailable"; }
            }
        }
        catch (Exception ex) { Log.Error("Server info scan failed", ex); }
    }

    /// <summary>The newest address in the newest Roblox log, or null when there is none yet.</summary>
    private string? FindAddress()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
        if (!Directory.Exists(dir)) return null;
        var newest = new DirectoryInfo(dir).EnumerateFiles("*.log").Where(f => f.Name.Contains("Player", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest == null) return null;
        if (newest.FullName == _lastFile && newest.Length == _lastLen) return Address;   // nothing new written
        _lastFile = newest.FullName; _lastLen = newest.Length;
        using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Only the end of the log matters: a later join overrides an earlier one.
        long start = Math.Max(0, fs.Length - 600_000);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        string? found = null;
        string? line;
        while ((line = sr.ReadLine()) != null)
            foreach (var re in AddressPatterns)
            {
                var m = re.Match(line);
                if (m.Success && IsPublic(m.Groups["ip"].Value)) found = m.Groups["ip"].Value;
            }
        return found;
    }

    private static bool IsPublic(string ip) =>
        System.Net.IPAddress.TryParse(ip, out var a) && !System.Net.IPAddress.IsLoopback(a) &&
        a.GetAddressBytes() is { Length: 4 } b && !(b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || b[0] == 0);

    public string Text()
    {
        if (Address == null) return $"SERVER  —  {Status}";
        string ping = PingMs is int p ? $"{p} ms" : "no reply";
        return $"SERVER  {Address}\n{(Region.Length > 0 ? Region + "  ·  " : "")}{ping}";
    }
}
