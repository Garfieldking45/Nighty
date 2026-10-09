using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Nighty.Views;

namespace Nighty.Services;

/// <summary>Checks GitHub Releases for a newer Nighty.exe and swaps it in on request.</summary>
public static class UpdateService
{
    private const string Repo = "Garfieldking45/Nighty";
    private const string AssetName = "Nighty.exe";

    public static async Task CheckAsync()
    {
        try
        {
            using var http = NewClient();
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return;
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            if (latest <= current) return;

            string? url = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                    url = a.GetProperty("browser_download_url").GetString();
            if (url == null) return;

            bool yes = await Application.Current.Dispatcher.InvokeAsync(() =>
                Dialogs.Confirm("Update available",
                    $"Nighty {tag.TrimStart('v', 'V')} is available (you have {current.ToString(3)}).\n\nClick Update to download it and restart Nighty.",
                    "Update")).Task.ConfigureAwait(false) ;
            if (!yes) return;

            await DownloadAndInstallAsync(http, url);
        }
        catch (Exception ex) { Log.Warn("Update check failed", ex); }
    }

    private static async Task DownloadAndInstallAsync(HttpClient http, string url)
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return;
        string newExe = exe + ".new";
        try
        {
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(newExe);
                await src.CopyToAsync(dst);
            }

            // Wait for this process to exit, replace the exe, relaunch.
            int pid = Environment.ProcessId;
            string script =
                $"$ErrorActionPreference='Stop'; try {{ Wait-Process -Id {pid} -Timeout 30 }} catch {{}}; " +
                $"Move-Item -LiteralPath '{newExe.Replace("'", "''")}' -Destination '{exe.Replace("'", "''")}' -Force; " +
                $"Start-Process -FilePath '{exe.Replace("'", "''")}'";
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script}\"")
            { UseShellExecute = false, CreateNoWindow = true });

            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            try { File.Delete(newExe); } catch { }
            Application.Current.Dispatcher.Invoke(() =>
                Dialogs.Info("Update failed", "Couldn't download the update. Please try again later."));
        }
    }

    private static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Nighty-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }
}
