using System.Text.Json;
using Microsoft.Win32;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Native;

namespace Nighty.Services;

public sealed record TweakInfo(string Id, string Title, string Description);

/// <summary>
/// PC tweaks that help frame rate and smoothness. Every tweak is per-user (no administrator rights), remembers the
/// exact original value of everything it touches, and can be switched back individually or all at once.
/// </summary>
public sealed class TweaksService
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042, SPI_SETCLIENTAREAANIMATION = 0x1043;
    /// <summary>Older versions could turn window animations off; kept so that setting can still be put back.</summary>
    private const string AnimSubKey = "<spi>";
    private const string FlagSubKey = "<flag>", FpsFlag = "DFIntTaskSchedulerTargetFps";

    private enum EditKind { Registry, RobloxFlag }
    private sealed record Edit(EditKind Kind, string SubKey, string Name, object Value);

    private SystemBackups B => Svc.S.Backups;

    public IReadOnlyList<TweakInfo> All { get; } = new TweakInfo[]
    {
        new("gpu", "Run Roblox on the high-performance graphics card",
            "On PCs with two graphics chips (most gaming laptops), tells Windows to always use the fast one for Roblox."),
        new("fpscap", "Remove Roblox's 60 FPS cap",
            "Roblox renders at 60 FPS unless told otherwise. This raises its frame-rate target (a setting on Roblox's published allowed list)."),
        new("gamedvr", "Turn off Game DVR background recording",
            "Stops Windows from recording gameplay in the background, which costs GPU and disk time."),
    };

    public bool IsApplied(string id) => B.TweaksApplied.Contains(id);

    /// <summary>Applies or reverts one tweak. On failure nothing is marked as applied.</summary>
    public OptionResult Set(string id, bool on)
    {
        try
        {
            if (!on)
            {
                var kept = Revert(id);
                return new(StatusKind.Neutral, kept.Count == 0 ? "" : "Turned off. Left your newer value alone for: " + string.Join(", ", kept));
            }
            var edits = EditsFor(id, out string? problem);
            if (edits == null) return new(StatusKind.Warning, problem ?? "Not available on this PC");
            foreach (var e in edits) ApplyEdit(id, e);
            B.TweaksApplied.Add(id);
            Svc.Settings.Save();
            Log.Info("Tweak applied: " + id);
            return new(StatusKind.Success, "Applied");
        }
        catch (Exception ex)
        {
            Log.Error("Tweak failed: " + id, ex);
            try { Revert(id); } catch { /* best effort: put back whatever we already changed */ }
            return new(StatusKind.Error, ex.Message);
        }
    }

    public void RevertAll()
    {
        foreach (var id in B.TweaksApplied.ToList()) { try { Revert(id); } catch (Exception ex) { Log.Error("Reverting tweak failed: " + id, ex); } }
    }

    /// <summary>
    /// At start-up, puts back the boost's settings if the boost was on when Nighty last closed (Roblox lives in a new folder after
    /// every update, so the per-path ones are written again). Older versions offered comfort tweaks that did nothing for frame rate;
    /// they are gone, so anything of theirs still switched on is put back to normal.
    /// </summary>
    public void Reapply()
    {
        bool boost = Svc.S.Game.AutoGameMode;
        foreach (var id in B.TweaksToReapply.ToList())
            if (boost && BoostService.TweakIds.Contains(id) && !IsApplied(id)) Set(id, true);
        B.TweaksToReapply.Clear();
        foreach (var id in B.TweaksApplied.Where(i => !BoostService.TweakIds.Contains(i)).ToList()) { try { Revert(id); } catch (Exception ex) { Log.Error("Reverting old tweak failed: " + id, ex); } }
        if (IsApplied("gpu")) Set("gpu", true);
        if (IsApplied("fpscap")) Set("fpscap", true);
    }

    /// <summary>Reverts every tweak but remembers which were on, so the next start puts them back.</summary>
    public void RevertAllRemembering()
    {
        foreach (var id in B.TweaksApplied) B.TweaksToReapply.Add(id);
        RevertAll();
    }

    /// <summary>Puts a tweak's original values back. A value that was changed after the tweak wrote it (by the user or another program) is left alone; the names of those are returned.</summary>
    private List<string> Revert(string id)
    {
        var kept = new List<string>();
        string prefix = id + "|";
        foreach (var key in B.Tweaks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            var parts = key.Split('|', 3);
            string subKey = parts[1], name = parts[2];
            string orig = B.Tweaks[key];
            B.TweaksWritten.TryGetValue(key, out var written);
            if (subKey == FlagSubKey)
            {
                var path = FlagFile();
                if (path != null && File.Exists(path))
                {
                    var flags = ReadFlags(path);
                    string cur = flags.TryGetValue(name, out var cv) ? "S:" + cv : "N";
                    if (written != null && cur != written && cur != orig) { kept.Add(name); Log.Info($"Tweak {id}: {name} changed since Nighty set it; left as is"); }
                    else WriteFlag(path, name, orig == "N" ? null : orig[2..]);
                }
            }
            else if (subKey == AnimSubKey)
            {
                NativeMethods.SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out int now, 0);
                string cur = "I:" + now;
                if (written != null && cur != written && cur != orig) { kept.Add("window animations"); Log.Info($"Tweak {id}: animations changed since Nighty set them; left as is"); }
                else if (orig.StartsWith("I:")) NativeMethods.SystemParametersInfo(SPI_SETCLIENTAREAANIMATION, 0, (IntPtr)int.Parse(orig[2..]), NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);
            }
            else
            {
                using var read = Registry.CurrentUser.OpenSubKey(subKey);
                string cur = read?.GetValue(name) switch { null => "N", int i => "I:" + i, string str => "S:" + str, var o => "S:" + o };
                if (written != null && cur != written && cur != orig) { kept.Add(name); Log.Info($"Tweak {id}: {name} changed since Nighty set it; left as is"); }
                else if (orig == "N")
                {
                    using var k = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
                    k?.DeleteValue(name, throwOnMissingValue: false);
                }
                else
                {
                    using var k = Registry.CurrentUser.CreateSubKey(subKey);
                    if (orig.StartsWith("I:")) k.SetValue(name, int.Parse(orig[2..]), RegistryValueKind.DWord);
                    else k.SetValue(name, orig[2..], RegistryValueKind.String);
                }
            }
            B.Tweaks.Remove(key);
            B.TweaksWritten.Remove(key);
        }
        B.TweaksApplied.Remove(id);
        Svc.Settings.Save();
        Log.Info("Tweak reverted: " + id);
        return kept;
    }

    private void ApplyEdit(string id, Edit e)
    {
        string key = $"{id}|{e.SubKey}|{e.Name}";
        if (e.Kind == EditKind.RobloxFlag)
        {
            var path = FlagFile() ?? throw new InvalidOperationException("Roblox isn't installed for this user");
            if (!B.Tweaks.ContainsKey(key))
            {
                B.Tweaks[key] = ReadFlags(path).TryGetValue(e.Name, out var old) ? "S:" + old : "N";
                Svc.Settings.Save();
            }
            WriteFlag(path, e.Name, (string)e.Value);
            B.TweaksWritten[key] = "S:" + (string)e.Value;
            return;
        }
        using var k = Registry.CurrentUser.CreateSubKey(e.SubKey);
        if (!B.Tweaks.ContainsKey(key))
        {
            B.Tweaks[key] = k.GetValue(e.Name) switch { null => "N", int i => "I:" + i, string s => "S:" + s, var o => "S:" + o };
            Svc.Settings.Save();
        }
        if (e.Value is int v) k.SetValue(e.Name, v, RegistryValueKind.DWord);
        else k.SetValue(e.Name, (string)e.Value, RegistryValueKind.String);
        B.TweaksWritten[key] = e.Value is int wi ? "I:" + wi : "S:" + (string)e.Value;
    }

    /// <summary>The ClientAppSettings.json of the newest Roblox client (the file Roblox reads allowed FastFlags from).</summary>
    private static string? FlagFile()
    {
        var folder = Svc.Roblox.FindVersionFolder();
        return folder == null ? null : Path.Combine(folder, "ClientSettings", "ClientAppSettings.json");
    }

    private static Dictionary<string, string> ReadFlags(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new() : new(); }
        catch { return new(); }
    }

    /// <summary>Merges one flag into the file, keeping every other flag; a null value removes it (and the file when empty).</summary>
    private static void WriteFlag(string path, string name, string? value)
    {
        var flags = ReadFlags(path);
        if (value == null) flags.Remove(name); else flags[name] = value;
        if (flags.Count == 0) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(flags, new JsonSerializerOptions { WriteIndented = true }));
    }

    private List<Edit>? EditsFor(string id, out string? problem)
    {
        problem = null;
        const string cv = @"Software\Microsoft\Windows\CurrentVersion\";
        switch (id)
        {
            case "gamedvr":
                return new()
                {
                    new(EditKind.Registry, @"System\GameConfigStore", "GameDVR_Enabled", 0),
                    new(EditKind.Registry, cv + "GameDVR", "AppCaptureEnabled", 0),
                };
            case "fpscap":
                if (FlagFile() == null) { problem = "Roblox isn't installed for this user, so there is nothing to set"; return null; }
                return new() { new(EditKind.RobloxFlag, FlagSubKey, FpsFlag, "9999") };
            case "gpu":
            {
                var folder = Svc.Roblox.FindVersionFolder();
                if (folder == null) { problem = "Roblox isn't installed for this user, so there is nothing to set"; return null; }
                string exe = Path.Combine(folder, "RobloxPlayerBeta.exe");
                return new() { new(EditKind.Registry, @"Software\Microsoft\DirectX\UserGpuPreferences", exe, "GpuPreference=2;") };
            }
            default:
                problem = "Unknown tweak";
                return null;
        }
    }
}
