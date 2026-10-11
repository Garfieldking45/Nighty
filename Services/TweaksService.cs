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
    private const string AnimSubKey = "<spi>", AnimName = "ClientAreaAnimation";
    private const string FlagSubKey = "<flag>", FpsFlag = "DFIntTaskSchedulerTargetFps";

    private enum EditKind { Registry, Animations, RobloxFlag }
    private sealed record Edit(EditKind Kind, string SubKey, string Name, object Value);

    private SystemBackups B => Svc.S.Backups;

    public IReadOnlyList<TweakInfo> All { get; } = new TweakInfo[]
    {
        new("gamedvr", "Turn off Game DVR background recording",
            "Stops Windows from recording gameplay in the background, which costs GPU and disk time. Also disables the Game Bar capture hooks."),
        new("gpu", "Run Roblox on the high-performance graphics card",
            "On PCs with two graphics chips (most gaming laptops), tells Windows to always use the fast one for Roblox. Re-applied automatically after Roblox updates."),
        new("fpscap", "Remove Roblox's 60 FPS cap",
            "Roblox renders at 60 FPS unless told otherwise. This raises its frame-rate target (a setting on Roblox's published allowed list) so a fast monitor can be used fully. Re-applied after Roblox updates."),
        new("transparency", "Turn off window transparency effects",
            "Removes the blur and see-through effects from Windows, freeing a little GPU power for the game."),
        new("animations", "Turn off window animations",
            "Windows stops animating windows and menus, which also makes alt-tabbing feel snappier."),
        new("bgapps", "Stop apps running in the background",
            "Prevents Store apps from running when you are not using them, so more CPU and memory are left for the game."),
        new("toasts", "Silence notification pop-ups",
            "Stops Windows notifications from appearing over your game and stealing focus. Turn it off again to get them back."),
    };

    public bool IsApplied(string id) => B.TweaksApplied.Contains(id);

    /// <summary>Applies or reverts one tweak. On failure nothing is marked as applied.</summary>
    public OptionResult Set(string id, bool on)
    {
        try
        {
            if (!on) { Revert(id); return new(StatusKind.Neutral, ""); }
            var edits = EditsFor(id, out string? problem);
            if (edits == null) return new(StatusKind.Warning, problem ?? "Not available on this PC");
            foreach (var e in edits) ApplyEdit(id, e);
            B.TweaksApplied.Add(id);
            Svc.Settings.Save();
            Log.Info("Tweak applied: " + id);
            return new(StatusKind.Success, id == "transparency" || id == "animations" ? "Applied (some windows may need to be reopened)" : "Applied");
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

    /// <summary>Roblox lives in a new folder after every update, so redo the per-path tweak at startup.</summary>
    public void Reapply()
    {
        foreach (var id in B.TweaksToReapply.ToList()) if (!IsApplied(id)) Set(id, true);
        B.TweaksToReapply.Clear();
        if (IsApplied("gpu")) Set("gpu", true);
        if (IsApplied("fpscap")) Set("fpscap", true);
    }

    /// <summary>Reverts every tweak but remembers which were on, so the next start puts them back.</summary>
    public void RevertAllRemembering()
    {
        foreach (var id in B.TweaksApplied) B.TweaksToReapply.Add(id);
        RevertAll();
    }

    private void Revert(string id)
    {
        string prefix = id + "|";
        foreach (var key in B.Tweaks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            var parts = key.Split('|', 3);
            string subKey = parts[1], name = parts[2];
            string orig = B.Tweaks[key];
            if (subKey == FlagSubKey)
            {
                var path = FlagFile();
                if (path != null && File.Exists(path)) WriteFlag(path, name, orig == "N" ? null : orig[2..]);
            }
            else if (subKey == AnimSubKey)
            {
                if (orig.StartsWith("I:")) NativeMethods.SystemParametersInfo(SPI_SETCLIENTAREAANIMATION, 0, (IntPtr)int.Parse(orig[2..]), NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);
            }
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
            B.Tweaks.Remove(key);
        }
        B.TweaksApplied.Remove(id);
        Svc.Settings.Save();
        Log.Info("Tweak reverted: " + id);
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
            return;
        }
        if (e.Kind == EditKind.Animations)
        {
            if (!B.Tweaks.ContainsKey(key))
            {
                NativeMethods.SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out int cur, 0);
                B.Tweaks[key] = "I:" + cur;
                Svc.Settings.Save();
            }
            if (!NativeMethods.SystemParametersInfo(SPI_SETCLIENTAREAANIMATION, 0, (IntPtr)(int)e.Value, NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE))
                throw new InvalidOperationException("Windows rejected the setting");
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
            case "transparency":
                return new() { new(EditKind.Registry, cv + @"Themes\Personalize", "EnableTransparency", 0) };
            case "animations":
                return new() { new(EditKind.Animations, AnimSubKey, AnimName, 0) };
            case "bgapps":
                return new() { new(EditKind.Registry, cv + "BackgroundAccessApplications", "GlobalUserDisabled", 1) };
            case "toasts":
                return new() { new(EditKind.Registry, cv + @"PushNotifications", "ToastEnabled", 0) };
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
