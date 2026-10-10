using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Nighty.Models;

namespace Nighty.Services;

public sealed class RobloxInstall
{
    public required string Name { get; init; }
    /// <summary>Newest version-xxxx folder holding RobloxPlayerBeta.exe (null if only a Modifications folder is known).</summary>
    public string? VersionFolder { get; init; }
    /// <summary>Bloxstrap-style Modifications folder: files put here survive Roblox updates. Null for plain Roblox.</summary>
    public string? ModsFolder { get; init; }
    public string Key => ModsFolder ?? VersionFolder ?? Name;
    public string Display => Name + (VersionFolder != null ? " · " + Path.GetFileName(VersionFolder) : "");
}

public sealed record AssetResult(int Changed, string? Error, string? Note = null);

/// <summary>
/// Changes Roblox's mouse cursor and font by replacing cosmetic files in its content folder. For Bloxstrap, Fishstrap, Voidstrap
/// and Froststrap the files go into their Modifications folder (which they re-apply after every update). For plain Roblox the
/// original file is copied away first, so Restore always brings it back, and the change is repeated after updates.
/// Nothing is injected into Roblox and only these image and font files are touched.
/// </summary>
public sealed class RobloxAssetsService
{
    private const string CursorRel = @"textures\Cursors\KeyboardMouse";
    private static readonly string[] Straps = { "Bloxstrap", "Fishstrap", "Voidstrap", "Froststrap" };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed class Applied { public string Key { get; set; } = ""; public string Version { get; set; } = ""; public string Rel { get; set; } = ""; public bool HadOriginal { get; set; } public string Kind { get; set; } = ""; }

    private static string ManifestFile => Path.Combine(AppPaths.Root, "roblox-applied.json");
    private static string BackupRoot => Path.Combine(AppPaths.Root, "roblox-backup");

    private List<Applied> LoadManifest()
    {
        try { return File.Exists(ManifestFile) ? JsonSerializer.Deserialize<List<Applied>>(File.ReadAllText(ManifestFile)) ?? new() : new(); }
        catch { return new(); }
    }
    private void SaveManifest(List<Applied> m) { try { Directory.CreateDirectory(AppPaths.Root); File.WriteAllText(ManifestFile, JsonSerializer.Serialize(m, Json)); } catch (Exception ex) { Log.Warn("Saving mod manifest failed", ex); } }

    // ---------------- finding Roblox ----------------
    public List<RobloxInstall> FindInstalls()
    {
        var list = new List<RobloxInstall>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        void AddRoot(string name, string root, bool strap)
        {
            var versions = Path.Combine(root, "Versions");
            string? newest = Directory.Exists(versions)
                ? new DirectoryInfo(versions).GetDirectories("version-*").Where(d => File.Exists(Path.Combine(d.FullName, "RobloxPlayerBeta.exe")))
                    .OrderByDescending(d => d.LastWriteTimeUtc).Select(d => d.FullName).FirstOrDefault()
                : null;
            if (newest == null && !(strap && Directory.Exists(Path.Combine(root, "Modifications")))) return;
            if (newest == null && strap) { /* a strap with no version yet: still usable through Modifications */ }
            list.Add(new RobloxInstall { Name = name, VersionFolder = newest, ModsFolder = strap ? Path.Combine(root, "Modifications") : null });
        }
        try
        {
            AddRoot("Roblox", Path.Combine(local, "Roblox"), false);
            foreach (var s in Straps) AddRoot(s, Path.Combine(local, s), true);
        }
        catch (Exception ex) { Log.Warn("Looking for Roblox failed", ex); }

        // A running client (even from a non-standard folder) and a folder chosen by hand.
        var running = Svc.Roblox.FindVersionFolder();
        if (running != null && !list.Any(i => string.Equals(i.VersionFolder, running, StringComparison.OrdinalIgnoreCase)))
            list.Add(new RobloxInstall { Name = "Roblox (running)", VersionFolder = running });
        var custom = Svc.S.Roblox.CustomFolder;
        if (custom.Length > 0 && Directory.Exists(custom))
        {
            var ins = FromFolder(custom);
            if (ins != null && !list.Any(i => i.Key.Equals(ins.Key, StringComparison.OrdinalIgnoreCase))) list.Add(ins);
        }
        return list;
    }

    /// <summary>Accepts a version folder (has RobloxPlayerBeta.exe) or a Roblox / strap root (has Versions).</summary>
    public RobloxInstall? FromFolder(string folder)
    {
        try
        {
            if (File.Exists(Path.Combine(folder, "RobloxPlayerBeta.exe"))) return new RobloxInstall { Name = "Chosen folder", VersionFolder = folder };
            var versions = Path.Combine(folder, "Versions");
            if (Directory.Exists(versions))
            {
                var v = new DirectoryInfo(versions).GetDirectories("version-*").Where(d => File.Exists(Path.Combine(d.FullName, "RobloxPlayerBeta.exe")))
                    .OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault();
                if (v != null)
                {
                    bool strap = Directory.Exists(Path.Combine(folder, "Modifications"));
                    return new RobloxInstall { Name = Path.GetFileName(folder.TrimEnd('\\', '/')), VersionFolder = v.FullName, ModsFolder = strap ? Path.Combine(folder, "Modifications") : null };
                }
            }
        }
        catch { }
        return null;
    }

    // ---------------- writing and restoring files ----------------
    private string? Put(RobloxInstall ins, string rel, byte[] data, string kind, List<Applied> manifest)
    {
        // rel is relative to Roblox's content folder
        string dest;
        if (ins.ModsFolder != null) dest = Path.Combine(ins.ModsFolder, "content", rel);
        else if (ins.VersionFolder != null) dest = Path.Combine(ins.VersionFolder, "content", rel);
        else return "No Roblox version folder found.";
        var full = Path.GetFullPath(dest);
        var root = Path.GetFullPath(ins.ModsFolder != null ? Path.Combine(ins.ModsFolder, "content") : Path.Combine(ins.VersionFolder!, "content"));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "Refused to write outside Roblox's content folder.";

        try
        {
            bool hadOriginal = false;
            if (ins.ModsFolder == null)
            {
                var ver = Path.GetFileName(ins.VersionFolder!);
                var existing = manifest.FirstOrDefault(a => a.Key == ins.Key && a.Rel == rel);
                var bak = Path.Combine(BackupRoot, ver, rel);
                if (existing == null && File.Exists(full))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                    File.Copy(full, bak, true);
                    hadOriginal = true;
                }
                else if (existing != null) hadOriginal = existing.HadOriginal;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
            if (!manifest.Any(a => a.Key == ins.Key && a.Rel == rel))
                manifest.Add(new Applied { Key = ins.Key, Version = ins.VersionFolder != null ? Path.GetFileName(ins.VersionFolder) : "", Rel = rel, HadOriginal = hadOriginal, Kind = kind });
            return null;
        }
        catch (UnauthorizedAccessException) { return $"Couldn't write {Path.GetFileName(full)}: close Roblox first, or its folder is protected."; }
        catch (IOException ex) { return Svc.Roblox.IsRunning ? "Close Roblox first, its files are in use." : "Couldn't write " + Path.GetFileName(full) + ": " + ex.Message; }
        catch (Exception ex) { return ex.Message; }
    }

    private int RemoveKind(string kind, List<Applied> manifest, out string? error)
    {
        error = null;
        int n = 0;
        var installs = FindInstalls();
        foreach (var a in manifest.Where(m => m.Kind == kind).ToList())
        {
            var ins = installs.FirstOrDefault(i => i.Key == a.Key);
            if (ins == null) { manifest.Remove(a); continue; }   // the install is gone, nothing to restore
            try
            {
                string full = ins.ModsFolder != null ? Path.Combine(ins.ModsFolder, "content", a.Rel) : Path.Combine(ins.VersionFolder ?? "", "content", a.Rel);
                if (ins.ModsFolder != null) { if (File.Exists(full)) { File.Delete(full); n++; } }
                else
                {
                    var bak = Path.Combine(BackupRoot, a.Version, a.Rel);
                    if (a.HadOriginal && File.Exists(bak)) { File.Copy(bak, full, true); n++; }
                    else if (!a.HadOriginal && File.Exists(full)) { File.Delete(full); n++; }
                }
                manifest.Remove(a);
            }
            catch (Exception ex) { error = Svc.Roblox.IsRunning ? "Close Roblox first, its files are in use." : ex.Message; }
        }
        return n;
    }

    // ---------------- cursor ----------------
    public static int SizeToPx(string size) => size switch { "small" => 32, "large" => 128, _ => 64 };

    public AssetResult ApplyCursor(CursorSpec spec, string size, double brightness, bool firstPerson)
    {
        var installs = FindInstalls();
        if (installs.Count == 0) return new(0, "Roblox wasn't found. Install it, or use Choose folder to show Nighty where it is.");
        return ApplyCursorPng(CursorRenderer.ToPng(CursorRenderer.Render(spec, SizeToPx(size), brightness)), firstPerson);
    }

    /// <summary>Writes an already rendered cursor. Safe to call from a worker thread.</summary>
    public AssetResult ApplyCursorPng(byte[] png, bool firstPerson)
    {
        var installs = FindInstalls();
        if (installs.Count == 0) return new(0, "Roblox wasn't found. Install it, or use Choose folder to show Nighty where it is.");
        var manifest = LoadManifest();
        int n = 0; string? err = null;
        foreach (var ins in installs)
        {
            foreach (var name in new[] { "ArrowCursor.png", "ArrowFarCursor.png" })
            {
                var e = Put(ins, Path.Combine(CursorRel, name), png, "cursor", manifest);
                if (e != null) err ??= e; else n++;
            }
            if (firstPerson)
            {
                var e = Put(ins, @"textures\MouseLockedCursor.png", png, "cursor", manifest);
                if (e != null) err ??= e; else n++;
            }
        }
        SaveManifest(manifest);
        return new(n, err, n > 0 ? "Restart Roblox to see it." : null);
    }

    public AssetResult RestoreCursor()
    {
        var manifest = LoadManifest();
        int n = RemoveKind("cursor", manifest, out var err);
        SaveManifest(manifest);
        return new(n, err);
    }

    // ---------------- font ----------------
    public AssetResult ApplyFont(string fontFile)
    {
        if (!File.Exists(fontFile)) return new(0, "That font file isn't available.");
        if (fontFile.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)) return new(0, "That font comes in a font collection (.ttc), which Roblox can't use. Pick another font.");
        var installs = FindInstalls().Where(i => i.VersionFolder != null).ToList();
        if (installs.Count == 0) return new(0, "Roblox wasn't found. Install it, or use Choose folder to show Nighty where it is.");
        var data = File.ReadAllBytes(fontFile);
        var manifest = LoadManifest();
        int n = 0; string? err = null; bool anyList = false;
        foreach (var ins in installs)
        {
            var fonts = Path.Combine(ins.VersionFolder!, "content", "fonts");
            if (!Directory.Exists(fonts)) continue;
            foreach (var f in Directory.EnumerateFiles(fonts).Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)))
            {
                anyList = true;
                var e = Put(ins, Path.Combine("fonts", Path.GetFileName(f)), data, "font", manifest);
                if (e != null) err ??= e; else n++;
            }
        }
        SaveManifest(manifest);
        if (!anyList) return new(0, "Roblox's font list wasn't found in this install.");
        return new(n, err, n > 0 ? "Restart Roblox to see it." : null);
    }

    public AssetResult RestoreFont()
    {
        var manifest = LoadManifest();
        int n = RemoveKind("font", manifest, out var err);
        SaveManifest(manifest);
        return new(n, err);
    }

    // ---------------- keep after updates ----------------
    private DateTime _lastCheck = DateTime.MinValue;

    /// <summary>
    /// Roblox replaces its files on every update. When a plain Roblox install has a version the saved cursor or font was never
    /// written to, write them there too. Bloxstrap-style installs keep their Modifications folder, so they need nothing.
    /// </summary>
    public string? ReapplyIfNeeded()
    {
        var r = Svc.S.Roblox;
        if (!r.KeepAfterUpdates || (r.CursorId.Length == 0 && r.FontId.Length == 0)) return null;
        if ((DateTime.UtcNow - _lastCheck).TotalSeconds < 30 || Svc.Roblox.IsRunning) return null;
        _lastCheck = DateTime.UtcNow;
        try
        {
            var manifest = LoadManifest();
            var stale = FindInstalls().Where(i => i.ModsFolder == null && i.VersionFolder != null && !manifest.Any(a => a.Key == i.Key)).ToList();
            if (stale.Count == 0) return null;
            string msg = "";
            if (r.CursorId.Length > 0 && CursorLibrary.Find(r.CursorId) is { } c)
            {
                var res = ApplyCursor(c, r.CursorSize, r.CursorBrightness, r.FirstPerson);
                if (res.Changed > 0) msg = "Your Roblox cursor";
            }
            if (r.FontId.Length > 0 && File.Exists(r.FontId))
            {
                var res = ApplyFont(r.FontId);
                if (res.Changed > 0) msg += (msg.Length > 0 ? " and font" : "Your Roblox font");
            }
            if (msg.Length > 0) { Log.Info("Re-applied after a Roblox update: " + msg); return msg + " were put back after a Roblox update."; }
        }
        catch (Exception ex) { Log.Warn("Re-applying cursor/font failed", ex); }
        return null;
    }
}
