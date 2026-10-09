using System.Text.Json;
using Nighty.Models;

namespace Nighty.Services;

public sealed record ModApplyReport(int Copied, int Skipped, string? Error);

/// <summary>
/// Content mods: replaces cosmetic asset files (images, sounds, fonts) inside the installed Roblox "content" folder
/// with files from a user-chosen folder. Every replaced original is backed up first and can be restored.
/// Nothing is injected into Roblox and only whitelisted asset types inside content\ can be touched.
/// </summary>
public sealed class ModsService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".ogg", ".mp3", ".ttf", ".otf", ".ktx", ".tga" };

    private string ManifestPath(string version) => Path.Combine(AppPaths.ModBackups, Path.GetFileName(version), "manifest.json");
    private string BackupDir(string version) => Path.Combine(AppPaths.ModBackups, Path.GetFileName(version), "files");

    /// <summary>Files in a mod folder that would be applied (relative to the Roblox "content" folder).</summary>
    public (List<string> Valid, int Rejected) Inspect(string modFolder)
    {
        var valid = new List<string>();
        int rejected = 0;
        if (!Directory.Exists(modFolder)) return (valid, 0);
        // Accept either <mod>\content\... or <mod>\... mirroring the content folder.
        var root = Directory.Exists(Path.Combine(modFolder, "content")) ? Path.Combine(modFolder, "content") : modFolder;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!AllowedExtensions.Contains(Path.GetExtension(f))) { rejected++; continue; }
            valid.Add(Path.GetRelativePath(root, f));
        }
        return (valid, rejected);
    }

    public bool IsApplied(string version) => File.Exists(ManifestPath(version));

    public Task<ModApplyReport> ApplyAsync(string version, IEnumerable<ModEntry> mods, CancellationToken ct) => Task.Run(() =>
    {
        if (Svc.Roblox.IsRunning) return new ModApplyReport(0, 0, "Close Roblox first — its files are locked while it runs.");
        int copied = 0, skipped = 0;
        try
        {
            var contentRoot = Path.GetFullPath(Path.Combine(version, "content"));
            if (!Directory.Exists(contentRoot)) return new ModApplyReport(0, 0, "The Roblox content folder was not found.");

            // Start from a clean slate so disabled mods are really removed.
            if (IsApplied(version)) RestoreInternal(version);

            var touched = new List<string>();
            var backupRoot = BackupDir(version);
            Directory.CreateDirectory(backupRoot);

            foreach (var mod in mods.Where(m => m.Enabled))
            {
                var srcRoot = Directory.Exists(Path.Combine(mod.SourcePath, "content")) ? Path.Combine(mod.SourcePath, "content") : mod.SourcePath;
                if (!Directory.Exists(srcRoot)) { skipped++; continue; }
                foreach (var file in Directory.EnumerateFiles(srcRoot, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!AllowedExtensions.Contains(Path.GetExtension(file))) { skipped++; continue; }
                    var rel = Path.GetRelativePath(srcRoot, file);
                    var dest = Path.GetFullPath(Path.Combine(contentRoot, rel));
                    if (!dest.StartsWith(contentRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; } // path traversal guard

                    if (!touched.Contains(rel))
                    {
                        if (File.Exists(dest))
                        {
                            var bak = Path.Combine(backupRoot, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                            File.Copy(dest, bak, true);
                        }
                        touched.Add(rel);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, true);
                    copied++;
                }
            }
            File.WriteAllText(ManifestPath(version), JsonSerializer.Serialize(touched));
            Log.Info($"Mods applied: {copied} files");
            return new ModApplyReport(copied, skipped, null);
        }
        catch (Exception ex)
        {
            Log.Error("Applying mods failed", ex);
            return new ModApplyReport(copied, skipped, ex.Message);
        }
    }, ct);

    public Task<string> RevertAsync(string version) => Task.Run(() =>
    {
        if (Svc.Roblox.IsRunning) return "Close Roblox first — its files are locked while it runs.";
        if (!IsApplied(version)) return "No applied mods to revert.";
        try { int n = RestoreInternal(version); return $"Restored {n} original file(s)."; }
        catch (Exception ex) { Log.Error("Reverting mods failed", ex); return ex.Message; }
    });

    private int RestoreInternal(string version)
    {
        var contentRoot = Path.GetFullPath(Path.Combine(version, "content"));
        var touched = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ManifestPath(version))) ?? new();
        int n = 0;
        foreach (var rel in touched)
        {
            var dest = Path.GetFullPath(Path.Combine(contentRoot, rel));
            if (!dest.StartsWith(contentRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var bak = Path.Combine(BackupDir(version), rel);
            if (File.Exists(bak)) { File.Copy(bak, dest, true); n++; }
            else if (File.Exists(dest)) { File.Delete(dest); n++; }   // file did not exist originally
        }
        File.Delete(ManifestPath(version));
        try { Directory.Delete(BackupDir(version), true); } catch { }
        return n;
    }
}
