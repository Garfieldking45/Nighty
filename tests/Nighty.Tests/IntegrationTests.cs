using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nighty.Models;
using Nighty.Services;
using Nighty.Services.Booster;
using Xunit;

namespace Nighty.Tests;

public class MigrationTests
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void An_older_settings_file_keeps_its_game_mode_choices_and_counts_as_approved()
    {
        // A settings file written before the booster existed: no "Booster" section at all.
        const string old = "{\"Game\":{\"AutoGameMode\":true,\"HighPerformancePower\":false,\"RobloxHighPriority\":true,\"TimerResolution\":false,\"WindowsGameMode\":true,\"KeepDisplayAwake\":true,\"RobloxNoThrottle\":false,\"CalmBackgroundApps\":true,\"FreeMemory\":false},\"Backups\":{\"GameModeActive\":false,\"TweaksApplied\":[\"gamedvr\"],\"Tweaks\":{\"gamedvr|k|n\":\"I:1\"}}}";
        var s = JsonSerializer.Deserialize<AppSettings>(old, Json)!;
        Assert.Equal(0, s.Booster.SchemaVersion);
        BoosterMigration.Apply(s, loadedFromFile: true);

        Assert.True(s.Booster.PlanReviewed);
        Assert.Equal(BoosterProfile.Custom, s.Booster.Profile);
        Assert.True(s.Booster.KeptFromOlderSetup);
        Assert.Equal(1, s.Booster.SchemaVersion);
        // Nothing the user had chosen changed.
        var enabled = GameChoices.Enabled(s.Game);
        Assert.Equal(new[] { "awake", "calm", "gamemode", "priority" }, enabled.OrderBy(x => x).ToArray());
        Assert.Contains("gamedvr", s.Backups.TweaksApplied);
        Assert.Equal("I:1", s.Backups.Tweaks["gamedvr|k|n"]);
        Assert.Empty(s.Backups.TweaksWritten);          // the new field starts empty for old data and old restores behave as before
    }

    [Fact]
    public void A_brand_new_install_has_nothing_approved()
    {
        var s = new AppSettings();
        BoosterMigration.Apply(s, loadedFromFile: false);
        Assert.False(s.Booster.PlanReviewed);
        Assert.Equal(1, s.Booster.SchemaVersion);
    }

    [Fact]
    public void Migration_runs_once_and_never_undoes_a_later_choice()
    {
        var s = new AppSettings();
        BoosterMigration.Apply(s, false);
        s.Booster.PlanReviewed = true; s.Booster.Profile = BoosterProfile.Performance; s.Booster.KeptFromOlderSetup = false;
        BoosterMigration.Apply(s, true);
        Assert.Equal(BoosterProfile.Performance, s.Booster.Profile);
        Assert.False(s.Booster.KeptFromOlderSetup);
    }

    [Fact]
    public void New_settings_survive_a_save_and_load()
    {
        var s = new AppSettings();
        BoosterMigration.Apply(s, false);
        s.Booster.PlanReviewed = true; s.Booster.Profile = BoosterProfile.Performance; s.Booster.ApprovedFingerprint = "abc";
        s.Backups.TweaksWritten["x|y|z"] = "I:0";
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, Json), Json)!;
        Assert.Equal(BoosterProfile.Performance, back.Booster.Profile);
        Assert.Equal("abc", back.Booster.ApprovedFingerprint);
        Assert.Equal("I:0", back.Backups.TweaksWritten["x|y|z"]);
    }

    [Fact]
    public void The_plan_choices_map_onto_the_game_mode_settings_exactly()
    {
        var g = new GameSettings();
        GameChoices.Set(g, new[] { ActionIds.Power, ActionIds.Calm });
        Assert.Equal(new[] { "calm", "power" }, GameChoices.Enabled(g).OrderBy(x => x).ToArray());
        GameChoices.Set(g, Array.Empty<string>());
        Assert.Empty(GameChoices.Enabled(g));
        GameChoices.Set(g, ActionIds.Session);
        Assert.Equal(ActionIds.Session.Count, GameChoices.Enabled(g).Count);
    }
}

public class DiagnosticsTests
{
    private static readonly Redactor R = new("jdoe42", "DESKTOP-ABC123", @"C:\Users\jdoe42");

    [Theory]
    [InlineData(@"C:\Users\jdoe42\AppData\Local\Froststrap\Versions\v1", @"C:\Users\<user>\AppData\Local\Froststrap\Versions\v1")]
    [InlineData(@"c:\users\JDOE42\Downloads", @"C:\Users\<user>\Downloads")]
    [InlineData("signed in as jdoe42 on DESKTOP-ABC123", "signed in as <user> on <computer>")]
    [InlineData("mail me: someone@example.com", "mail me: <email>")]
    [InlineData("dns 192.168.1.10 and 8.8.8.8", "dns <ip> and <ip>")]
    [InlineData("adapter 00-1A-2B-3C-4D-5E", "adapter <mac>")]
    [InlineData("password: hunter2", "password=<removed>")]
    public void Personal_details_are_removed(string input, string expected) => Assert.Equal(expected, R.Redact(input));

    [Fact]
    public void Ordinary_text_is_left_alone()
        => Assert.Equal("GPU 59 °C, driver 32.0.16.1088, 144 Hz", R.Redact("GPU 59 °C, driver 32.0.16.1088, 144 Hz"));

    [Fact]
    public void A_user_name_inside_another_word_is_not_mangled()
        => Assert.Equal("jdoe420 stays", R.Redact("jdoe420 stays"));

    [Fact]
    public void The_exported_report_contains_the_facts_and_none_of_the_personal_details()
    {
        var hw = TestHw.Make();
        var plan = new RecommendationEngine().Evaluate(TestHw.Ctx(hw));
        var journal = new List<JournalEntry>
        {
            new() { ActionId = "gpu", Description = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences → C:\Users\jdoe42\AppData\Local\Roblox\x.exe", Original = null, Intended = "str:GpuPreference=2;", State = EntryState.Applied },
        };
        var input = new DiagnosticsInput("2.2.0", hw, new[] { new DataSourceStatus("x", "Thing", false, "no") }, null, plan, journal, "1 restored", Array.Empty<BenchmarkRun>(),
            TestHw.Config(), new[] { @"12:00:00 [INFO] user jdoe42 opened C:\Users\jdoe42\Documents on DESKTOP-ABC123" }, new[] { "note for jdoe42" });
        string text = DiagnosticsExporter.Build(input, R, DateTimeOffset.UtcNow);

        Assert.DoesNotContain("jdoe42", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DESKTOP-ABC123", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NVIDIA GeForce GTX 1650", text);
        Assert.Contains("HardwareFingerprint", text);
        Assert.Contains("Recommendations", text);
        Assert.Contains("ChangeJournal", text);
        using var doc = JsonDocument.Parse(text);                      // and it is valid JSON
        Assert.True(doc.RootElement.TryGetProperty("Plan", out _));
    }
}

public class FrameRateInspectorTests : IDisposable
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "nighty-fr-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_local, true); } catch { } }

    private string MakeVersion(string launcher, string? flagsJson)
    {
        var v = Path.Combine(_local, launcher, "Versions", "version-abc");
        Directory.CreateDirectory(Path.Combine(v, "ClientSettings"));
        File.WriteAllText(Path.Combine(v, "RobloxPlayerBeta.exe"), "");
        if (flagsJson != null) File.WriteAllText(Path.Combine(v, "ClientSettings", "ClientAppSettings.json"), flagsJson);
        return v;
    }

    [Fact]
    public void A_launcher_managed_setup_like_froststrap_is_read_correctly()
    {
        const string flags = "{\"FIntFRMMinGrassDistance\":\"0\",\"DFIntTextureQualityOverride\":\"0\",\"DFFlagTextureQualityOverrideEnabled\":\"True\",\"DFIntCSGLevelOfDetailSwitchingDistance\":\"0\",\"FFlagDebugSkyGray\":\"True\"}";
        MakeVersion("Froststrap", flags);
        var mods = Path.Combine(_local, "Froststrap", "Modifications", "ClientSettings"); Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "ClientAppSettings.json"), flags);
        File.WriteAllText(Path.Combine(_local, "Froststrap", "Settings.json"), "{ \"UseFastFlagManager\": true }");
        Directory.CreateDirectory(Path.Combine(_local, "Roblox"));
        File.WriteAllText(Path.Combine(_local, "Roblox", "GlobalBasicSettings_13.xml"), "<roblox><Item><Properties><int name=\"FramerateCap\">1000</int><int name=\"GraphicsQualityLevel\">21</int><bool name=\"Fullscreen\">true</bool></Properties></Item></roblox>");

        var c = FrameRateInspector.Inspect(_local, null, LauncherKind.Unknown);
        Assert.Equal(LauncherKind.Froststrap, c.Launcher);
        Assert.True(c.LauncherManagesFlags);
        Assert.Equal(1000, c.FramerateCapSetting);
        Assert.Equal(21, c.GraphicsQualityLevel);
        Assert.True(c.Fullscreen);
        Assert.Null(c.TargetFpsFromFlags);
        Assert.False(c.LauncherHasTargetFps);
        Assert.Equal(1000, c.ConfiguredLimit);
        Assert.Contains("FIntFRMMinGrassDistance=0", c.LowDetailFlags);
        Assert.Contains("DFIntTextureQualityOverride=0", c.LowDetailFlags);
        Assert.Contains("FFlagDebugSkyGray=True", c.LowDetailFlags);
    }

    [Fact]
    public void The_texture_override_does_not_count_when_it_is_switched_off()
    {
        MakeVersion("Roblox", "{\"DFIntTextureQualityOverride\":\"0\",\"DFFlagTextureQualityOverrideEnabled\":\"False\"}");
        var c = FrameRateInspector.Inspect(_local, null, LauncherKind.Official);
        Assert.DoesNotContain(c.LowDetailFlags, f => f.StartsWith("DFIntTextureQualityOverride"));
    }

    [Fact]
    public void The_lower_of_the_flag_and_the_setting_is_the_configured_limit()
    {
        MakeVersion("Roblox", "{\"DFIntTaskSchedulerTargetFps\":\"144\"}");
        Directory.CreateDirectory(Path.Combine(_local, "Roblox"));
        File.WriteAllText(Path.Combine(_local, "Roblox", "GlobalBasicSettings_13.xml"), "<int name=\"FramerateCap\">240</int>");
        var c = FrameRateInspector.Inspect(_local, null, LauncherKind.Official);
        Assert.Equal(144, c.ConfiguredLimit);
        Assert.False(c.LauncherManagesFlags);
    }

    [Fact]
    public void Missing_or_broken_files_give_unknowns_not_exceptions()
    {
        Assert.Null(FrameRateInspector.Inspect(_local, null, LauncherKind.Unknown).VersionFolder);
        MakeVersion("Roblox", "{ broken json");
        var c = FrameRateInspector.Inspect(_local, null, LauncherKind.Official);
        Assert.Empty(c.ClientFlags);
        Assert.NotEmpty(c.Notes);
        Assert.Null(c.ConfiguredLimit);
    }
}

/// <summary>The real Windows process code, run against a throwaway process this test starts itself, never against anyone else's.</summary>
public class NativeProcessTests : IDisposable
{
    private readonly Process _child;
    public NativeProcessTests()
    {
        _child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 40 127.0.0.1 > nul") { CreateNoWindow = true, UseShellExecute = false })!;
        Thread.Sleep(300);
    }
    public void Dispose() { try { _child.Kill(true); } catch { } _child.Dispose(); }

    [Fact]
    public void Priority_and_efficiency_mode_can_be_changed_verified_and_restored_on_a_real_process()
    {
        var src = new WindowsProcessSource();
        var d = src.Describe(_child.Id)!;
        Assert.NotNull(d.Path);
        Assert.EndsWith("cmd.exe", d.Path, StringComparison.OrdinalIgnoreCase);

        var ctl = new NativeProcessControl();
        var tx = new ChangeTransactions(new MemoryJournalStore(), new DefaultTargetResolver(ctl)); tx.Load();
        var prio = new ProcessPriorityTarget(ctl, d.Id, "cmd");
        var thr = new ProcessThrottleTarget(ctl, d.Id, "cmd");
        string origThr = thr.Read().Value!;

        var r1 = tx.Apply("s", "calm", prio, PriorityNames.BelowNormal);
        Assert.True(r1.Status == ApplyStatus.Verified, "priority: " + r1.Status + " " + r1.Detail);
        var r2 = tx.Apply("s", "calm", thr, ThrottleState.Encode(ThrottleState.ExecutionSpeed, ThrottleState.ExecutionSpeed));
        Assert.True(r2.Status == ApplyStatus.Verified, "throttle: " + r2.Status + " " + r2.Detail);
        _child.Refresh();
        Assert.Equal(ProcessPriorityClass.BelowNormal, _child.PriorityClass);

        var rep = tx.RestoreAll();
        Assert.True(rep.FullyRestored);
        Assert.Equal(2, rep.Restored);
        _child.Refresh();
        Assert.Equal(ProcessPriorityClass.Normal, _child.PriorityClass);
        Assert.Equal(origThr, thr.Read().Value);
    }

    [Fact]
    public void A_process_that_has_exited_or_whose_id_is_reused_reads_as_gone()
    {
        var d = new WindowsProcessSource().Describe(_child.Id)!;
        var ctl = new NativeProcessControl();
        Assert.Equal(ReadStatus.Ok, ctl.GetPriority(d.Id).Status);
        Assert.Equal(ReadStatus.Gone, ctl.GetPriority(new ProcIdentity(d.Id.Pid, d.Id.StartFileTime + 12345)).Status);   // same id, different start time
        _child.Kill(true); _child.WaitForExit();
        Assert.Equal(ReadStatus.Gone, ctl.GetPriority(d.Id).Status);
        Assert.False(ctl.SetPriority(d.Id, PriorityNames.High).Ok);
    }

    [Fact]
    public void The_process_list_includes_this_test_and_describe_reports_a_start_time()
    {
        var src = new WindowsProcessSource();
        Assert.Contains(src.List(), p => p.Pid == Environment.ProcessId);
        Assert.True(src.Describe(Environment.ProcessId)!.Id.StartFileTime > 0);
        Assert.Null(src.Describe(int.MaxValue - 7));
    }

    [Fact]
    public void The_real_power_readers_return_values_without_changing_anything()
    {
        string? before = PowerSchemeTarget.Active();
        Assert.NotNull(before);
        Assert.NotEmpty(PowerSchemeTarget.List());
        _ = PowerOverlayTarget.Actual();
        Assert.Equal(before, PowerSchemeTarget.Active());
        Assert.Equal(PowerMode.Balanced, PowerOverlayTarget.ToMode(Guid.Empty.ToString()));
        Assert.Equal(PowerMode.BestPerformance, PowerOverlayTarget.ToMode("ded574b5-45a0-4f42-8737-46345c09c238"));
        Assert.Equal(PowerMode.Unknown, PowerOverlayTarget.ToMode("not a guid"));
    }
}

public class LocatorTests
{
    [Fact]
    public void The_locator_caches_briefly_and_refreshes_on_demand()
    {
        var world = new FakeWorld(); var time = new ManualTime();
        var loc = new RobloxGameLocator(world, FakeWorld.Local, time, TimeSpan.FromSeconds(1));
        Assert.False(loc.Find().IsRunning);
        world.Roblox();
        Assert.False(loc.Find().IsRunning);              // still the cached answer
        Assert.True(loc.Find(fresh: true).IsRunning);
        world.Procs.Clear();
        Assert.True(loc.Find().IsRunning);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(loc.Find().IsRunning);
    }
}
