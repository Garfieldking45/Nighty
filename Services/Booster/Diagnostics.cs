using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nighty.Services.Booster;

/// <summary>Removes personal details from text before it is copied or saved for someone else to read.</summary>
public sealed class Redactor
{
    private readonly List<(Regex Pattern, string Replacement)> _rules = new();

    public Redactor(string userName, string machineName, string userProfile)
    {
        if (!string.IsNullOrWhiteSpace(userProfile)) _rules.Add((new Regex(Regex.Escape(userProfile.TrimEnd('\\')), RegexOptions.IgnoreCase), @"C:\Users\<user>"));
        _rules.Add((new Regex(@"(?<=[A-Za-z]:\\Users\\)[^\\/""<>|:*?\r\n ]+", RegexOptions.IgnoreCase), "<user>"));
        if (userName.Length >= 3) _rules.Add((new Regex(@"(?<![A-Za-z0-9])" + Regex.Escape(userName) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase), "<user>"));
        if (machineName.Length >= 3) _rules.Add((new Regex(@"(?<![A-Za-z0-9])" + Regex.Escape(machineName) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase), "<computer>"));
        _rules.Add((new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}"), "<email>"));
        _rules.Add((new Regex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b"), "<ip>"));
        _rules.Add((new Regex(@"\b[0-9A-Fa-f]{2}(?:[:-][0-9A-Fa-f]{2}){5}\b"), "<mac>"));
        _rules.Add((new Regex(@"(?i)\b(password|token|secret|apikey|api_key)\b\s*[:=]\s*\S+"), "$1=<removed>"));
    }

    public static Redactor ForThisPc() => new(Environment.UserName, Environment.MachineName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        foreach (var (p, r) in _rules) text = p.Replace(text, r);
        return text;
    }
}

public sealed record DiagnosticsInput(
    string AppVersion,
    HardwareProfile? Hardware,
    IReadOnlyList<DataSourceStatus> TelemetrySources,
    TelemetrySample? Latest,
    OptimizationPlan? Plan,
    IReadOnlyList<JournalEntry> Journal,
    string? LastRestoreSummary,
    IReadOnlyList<BenchmarkRun> Benchmarks,
    FrameRateConfig? FrameRate,
    IReadOnlyList<string> LogTail,
    IReadOnlyList<string> Notes);

public static class DiagnosticsExporter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A readable report as JSON, with names, paths, addresses and similar details removed. Contains no files from the PC other than the log tail given.</summary>
    public static string Build(DiagnosticsInput i, Redactor redactor, DateTimeOffset now)
    {
        var doc = new
        {
            Generated = now,
            Note = "Made by Nighty. Names, folders, e-mail addresses and network addresses were removed. Nothing here is sent anywhere by Nighty.",
            App = i.AppVersion,
            Hardware = i.Hardware,
            HardwareFingerprint = i.Hardware?.Fingerprint,
            DataSources = (i.Hardware?.Sources ?? Array.Empty<DataSourceStatus>()).Concat(i.TelemetrySources).ToList(),
            LatestMeasurement = i.Latest,
            Plan = i.Plan == null ? null : new
            {
                i.Plan.CreatedAt,
                Recommendations = i.Plan.Items.Select(r => new { r.Id, r.Title, r.Applicability, r.Reason, r.CurrentValue, r.ProposedValue, r.Confidence, r.Risk, r.Reversible, Evidence = r.Evidence.Select(e => $"[{e.Kind}] {e.Text}") }),
                Findings = i.Plan.Findings.Select(f => new { f.Subject, f.Level, f.Statement, f.Confidence, f.Evidence }),
                i.Plan.Notes,
            },
            ChangeJournal = i.Journal.Select(e => new { e.ActionId, e.Description, e.State, e.Original, e.Intended, e.Note, e.CreatedUtc, e.FinishedUtc }),
            LastRestore = i.LastRestoreSummary,
            FrameRateConfiguration = i.FrameRate == null ? null : new
            {
                i.FrameRate.Launcher, Version = i.FrameRate.VersionFolder == null ? null : Path.GetFileName(i.FrameRate.VersionFolder), i.FrameRate.FramerateCapSetting,
                i.FrameRate.GraphicsQualityLevel, i.FrameRate.Fullscreen, ClientFlags = i.FrameRate.ClientFlags, i.FrameRate.LauncherManagesFlags, i.FrameRate.LowDetailFlags, i.FrameRate.Notes,
            },
            Benchmarks = i.Benchmarks.Select(b => new { b.Label, b.Group, b.StartedUtc, b.PlannedSeconds, b.Frames, b.ManualAvgFps, b.ManualOnePercentLowFps, b.System, b.Context, b.Warnings }),
            Notes = i.Notes,
            LogTail = i.LogTail,
        };
        return redactor.Redact(JsonSerializer.Serialize(doc, Json));
    }

    public static IReadOnlyList<string> ReadLogTail(string logsFolder, int lines = 120)
    {
        try
        {
            var newest = new DirectoryInfo(logsFolder).GetFiles("nighty-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest == null) return Array.Empty<string>();
            using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var q = new Queue<string>();
            string? l;
            while ((l = sr.ReadLine()) != null) { q.Enqueue(l); if (q.Count > lines) q.Dequeue(); }
            return q.ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}
