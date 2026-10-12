using Nighty.Services.Booster;
using Xunit;

namespace Nighty.Tests;

internal sealed class EngineRig
{
    public readonly FakeWorld World = new();
    public readonly FakeTargetFactory Targets;
    public readonly MemoryJournalStore Store = new();
    public readonly FakeResources Res = new();
    public readonly ManualTime Time = new();
    public readonly List<string> Logs = new();
    public HardwareProfile? Hw = TestHw.Make();
    public (long, long)? Mem = (16L << 30, 4L << 30);   // 25% free
    public ChangeTransactions Tx = null!;
    public GameModeEngine Engine = null!;
    public bool ThrowOnDetect;

    public EngineRig()
    {
        Targets = new FakeTargetFactory(World);
        Build();
    }

    public void Build()
    {
        Tx = new ChangeTransactions(Store, Targets.Resolver(), Time); Tx.Load();
        Engine = new GameModeEngine(new GameModeDeps
        {
            Game = new ThrowingLocator(this, new RobloxGameLocator(World, FakeWorld.Local, Time, TimeSpan.Zero)),
            Processes = World, Control = World, Targets = Targets, Journal = Tx, Resources = Res, Hardware = () => Hw,
            Memory = () => Mem, Log = Logs.Add, Time = Time,
        });
    }

    private sealed class ThrowingLocator : IGameLocator
    {
        private readonly EngineRig _r; private readonly IGameLocator _inner;
        public ThrowingLocator(EngineRig r, IGameLocator i) { _r = r; _inner = i; }
        public RobloxSession Find() => _r.ThrowOnDetect ? throw new InvalidOperationException("boom") : _inner.Find();
    }

    public static IReadOnlySet<string> Set(params string[] ids) => ids.ToHashSet();
}

public class GameModeEngineTests
{
    [Fact]
    public void With_nothing_approved_nothing_is_changed()
    {
        var r = new EngineRig();
        var game = r.World.Roblox();
        var chrome = r.World.Add("chrome");
        r.Engine.Activate(EngineRig.Set());
        r.Engine.Tick();
        Assert.Equal(0, r.World.PriorityWrites + r.World.ThrottleWrites);
        Assert.Equal(0, r.Targets.GameBar.Writes + r.Targets.Scheme.Writes + r.Targets.Overlay.Writes);
        Assert.Equal(PriorityNames.Normal, game.Priority);
        Assert.False(r.Res.TimerOn);
        Assert.Empty(r.Tx.Entries);
    }

    [Fact]
    public void Only_the_approved_actions_are_applied()
    {
        var r = new EngineRig();
        var game = r.World.Roblox();
        r.Engine.Activate(EngineRig.Set(ActionIds.Priority));
        Assert.Equal(PriorityNames.High, game.Priority);
        Assert.Equal(ThrottleState.Encode(0, 0), game.Throttle);        // throttle was not approved
        Assert.Equal(0, r.Targets.Overlay.Writes);
    }

    [Fact]
    public void Priority_is_applied_to_roblox_and_put_back_when_game_mode_ends()
    {
        var r = new EngineRig();
        var game = r.World.Roblox(); game.Priority = PriorityNames.AboveNormal;      // a non-default original
        r.Engine.Activate(EngineRig.Set(ActionIds.Priority, ActionIds.Throttle));
        Assert.Equal(PriorityNames.High, game.Priority);
        Assert.Equal(ThrottleState.Encode(ThrottleState.ExecutionSpeed | ThrottleState.IgnoreTimerResolution, 0), game.Throttle);
        var rep = r.Engine.Deactivate();
        Assert.Equal(PriorityNames.AboveNormal, game.Priority);                       // the real original, not "Normal"
        Assert.Equal(ThrottleState.Encode(0, 0), game.Throttle);
        Assert.True(rep.FullyRestored);
        Assert.False(r.Engine.IsActive);
    }

    [Fact]
    public void Roblox_starting_after_game_mode_is_picked_up_once()
    {
        var r = new EngineRig();
        r.Engine.Activate(EngineRig.Set(ActionIds.Priority));
        Assert.Equal(ActionState.Waiting, r.Engine.Reports.Single(x => x.ActionId == ActionIds.Priority).State);
        var game = r.World.Roblox();
        r.Engine.Tick();
        Assert.Equal(PriorityNames.High, game.Priority);
        int writes = r.World.PriorityWrites;
        for (int i = 0; i < 5; i++) r.Engine.Tick();
        Assert.Equal(writes, r.World.PriorityWrites);                                  // not applied again and again
    }

    [Fact]
    public void A_restarted_roblox_is_handled_as_a_new_process_and_the_old_one_is_closed_out()
    {
        var r = new EngineRig();
        var first = r.World.Roblox();
        r.Engine.Activate(EngineRig.Set(ActionIds.Priority));
        r.World.Kill(first);
        r.Engine.Tick();
        var second = r.World.Roblox();
        r.Engine.Tick();
        Assert.Equal(PriorityNames.High, second.Priority);
        Assert.DoesNotContain(r.Tx.Pending, e => e.TargetKey.Contains(FakeWorld.Id(first).ToString()));   // the dead one is no longer pending
        var rep = r.Engine.Deactivate();
        Assert.Equal(PriorityNames.Normal, second.Priority);
        Assert.True(rep.FullyRestored);
    }

    [Fact]
    public void A_reused_process_id_is_never_touched()
    {
        var r = new EngineRig();
        var game = r.World.Roblox(pid: 4242);
        r.Engine.Activate(EngineRig.Set(ActionIds.Priority));
        r.World.Kill(game);
        var stranger = r.World.Add("notepad", pid: 4242);           // Windows hands the same id to another program
        stranger.Priority = PriorityNames.BelowNormal;
        r.Engine.Deactivate();
        Assert.Equal(PriorityNames.BelowNormal, stranger.Priority);
    }

    [Fact]
    public void Several_roblox_clients_are_all_handled()
    {
        var r = new EngineRig();
        var a = r.World.Roblox(); var b = r.World.Roblox("Roblox");
        r.Engine.Activate(EngineRig.Set(ActionIds.Priority));
        Assert.Equal(PriorityNames.High, a.Priority); Assert.Equal(PriorityNames.High, b.Priority);
        r.Engine.Deactivate();
        Assert.Equal(PriorityNames.Normal, a.Priority); Assert.Equal(PriorityNames.Normal, b.Priority);
    }

    [Fact]
    public void A_process_called_robloxplayerbeta_from_an_unexpected_folder_is_ignored()
    {
        var r = new EngineRig();
        var fake = r.World.Add("RobloxPlayerBeta", @"C:\Temp\Downloads\RobloxPlayerBeta.exe");
        r.Engine.Activate(EngineRig.Set(ActionIds.Priority, ActionIds.Throttle));
        Assert.Equal(PriorityNames.Normal, fake.Priority);
        Assert.Equal(ActionState.Waiting, r.Engine.Reports.Single(x => x.ActionId == ActionIds.Priority).State);
    }

    [Theory]
    [InlineData("Roblox", LauncherKind.Official)]
    [InlineData("Bloxstrap", LauncherKind.Bloxstrap)]
    [InlineData("Fishstrap", LauncherKind.Fishstrap)]
    [InlineData("Voidstrap", LauncherKind.Voidstrap)]
    [InlineData("Froststrap", LauncherKind.Froststrap)]
    public void The_launcher_is_read_from_where_the_client_runs(string folder, LauncherKind expected)
    {
        var w = new FakeWorld();
        w.Roblox(folder);
        var s = RobloxClientDetector.Detect(w, FakeWorld.Local);
        Assert.True(s.IsRunning);
        Assert.Equal(expected, s.Launcher);
        Assert.Single(s.Clients);
    }

    [Fact]
    public void An_all_users_install_is_recognised_through_the_machine_roots()
    {
        var w = new FakeWorld();
        w.Add("RobloxPlayerBeta", @"C:\Program Files (x86)\Roblox\Versions\version-1\RobloxPlayerBeta.exe");
        Assert.False(RobloxClientDetector.Detect(w, FakeWorld.Local).IsRunning);
        var s = RobloxClientDetector.Detect(w, FakeWorld.Local, new[] { @"C:\Program Files (x86)\Roblox" });
        Assert.True(s.IsRunning);
        Assert.Equal(LauncherKind.Official, s.Launcher);
    }

    [Fact]
    public void A_client_whose_path_cannot_be_read_is_left_alone_with_a_note()
    {
        var w = new FakeWorld();
        w.Add("RobloxPlayerBeta", path: null);
        var s = RobloxClientDetector.Detect(w, FakeWorld.Local);
        Assert.False(s.IsRunning);
        Assert.NotEmpty(s.Notes);
    }

    // ---------------- background apps ----------------
    [Fact]
    public void Calm_only_slows_known_background_apps_and_never_protected_or_unknown_ones()
    {
        var r = new EngineRig();
        r.World.Roblox();
        var chrome = r.World.Add("chrome"); var steam = r.World.Add("steam");
        var defender = r.World.Add("MsMpEng"); var dwm = r.World.Add("dwm"); var game = r.World.Add("unknowngame"); var self = r.World.Add("Nighty");
        var nvc = r.World.Add("nvcontainer"); var av = r.World.Add("avp");
        r.Engine.Activate(EngineRig.Set(ActionIds.Calm));
        Assert.Equal(PriorityNames.BelowNormal, chrome.Priority);
        Assert.Equal(PriorityNames.BelowNormal, steam.Priority);
        foreach (var untouched in new[] { defender, dwm, game, self, nvc, av }) Assert.Equal(PriorityNames.Normal, untouched.Priority);
        Assert.Equal(2, r.Engine.AppsSlowed);
        r.Engine.Deactivate();
        Assert.Equal(PriorityNames.Normal, chrome.Priority);
        Assert.Equal(ThrottleState.Encode(0, 0), chrome.Throttle);
    }

    [Fact]
    public void Calm_does_nothing_until_roblox_runs_and_lets_go_when_it_closes()
    {
        var r = new EngineRig();
        var chrome = r.World.Add("chrome");
        r.Engine.Activate(EngineRig.Set(ActionIds.Calm));
        Assert.Equal(PriorityNames.Normal, chrome.Priority);
        var game = r.World.Roblox();
        r.Engine.Tick();
        Assert.Equal(PriorityNames.BelowNormal, chrome.Priority);
        r.World.Kill(game);
        r.Engine.Tick();
        Assert.Equal(PriorityNames.Normal, chrome.Priority);       // released as soon as Roblox is gone
        Assert.Equal(0, r.Engine.AppsSlowed);
    }

    [Fact]
    public void An_app_the_user_already_set_to_low_priority_is_not_touched_or_restored()
    {
        var r = new EngineRig();
        r.World.Roblox();
        var chrome = r.World.Add("chrome"); chrome.Priority = PriorityNames.Idle;
        r.Engine.Activate(EngineRig.Set(ActionIds.Calm));
        Assert.Equal(PriorityNames.Idle, chrome.Priority);
        r.Engine.Deactivate();
        Assert.Equal(PriorityNames.Idle, chrome.Priority);
    }

    [Fact]
    public void A_priority_changed_by_the_user_during_game_mode_is_not_overwritten_at_the_end()
    {
        var r = new EngineRig();
        r.World.Roblox();
        var chrome = r.World.Add("chrome");
        r.Engine.Activate(EngineRig.Set(ActionIds.Calm));
        chrome.Priority = PriorityNames.High;                       // the user raised it in Task Manager
        var rep = r.Engine.Deactivate();
        Assert.Equal(PriorityNames.High, chrome.Priority);
        Assert.True(rep.SkippedChanged >= 1);
    }

    [Fact]
    public void One_process_that_refuses_does_not_stop_the_others_and_is_reported()
    {
        var r = new EngineRig();
        r.World.Roblox();
        var a = r.World.Add("chrome"); var b = r.World.Add("steam"); var c = r.World.Add("msedge");
        b.DenyWrite = true;
        r.Engine.Activate(EngineRig.Set(ActionIds.Calm));
        Assert.Equal(PriorityNames.BelowNormal, a.Priority);
        Assert.Equal(PriorityNames.BelowNormal, c.Priority);
        Assert.Equal(PriorityNames.Normal, b.Priority);
        Assert.Contains("could not be changed", r.Engine.Reports.Single(x => x.ActionId == ActionIds.Calm).Text);
        Assert.True(r.Engine.Deactivate().FullyRestored);
    }

    [Fact]
    public void Failed_attempts_are_limited_and_spaced_out()
    {
        var r = new EngineRig();
        var game = r.World.Roblox(); game.DenyWrite = true;
        r.Engine.Activate(EngineRig.Set(ActionIds.Priority));
        int first = r.World.PriorityWrites;
        r.Engine.Tick(); r.Engine.Tick();
        Assert.Equal(first, r.World.PriorityWrites);                // too soon to retry
        for (int i = 0; i < 6; i++) { r.Time.Advance(TimeSpan.FromSeconds(11)); r.Engine.Tick(); }
        Assert.True(r.World.PriorityWrites <= first + 2, "retries must stop after a few attempts");
        Assert.Equal(ActionState.Failed, r.Engine.Reports.Single(x => x.ActionId == ActionIds.Priority).State);
    }

    // ---------------- power ----------------
    [Fact]
    public void Power_is_not_changed_on_battery()
    {
        var r = new EngineRig { Hw = TestHw.Make(power: TestHw.Power(PowerSource.Battery)) };
        r.Engine.Activate(EngineRig.Set(ActionIds.Power));
        Assert.Equal(0, r.Targets.Overlay.Writes + r.Targets.Scheme.Writes);
        var rep = r.Engine.Reports.Single(x => x.ActionId == ActionIds.Power);
        Assert.Equal(ActionState.Skipped, rep.State);
        Assert.Contains("battery", rep.Text);
    }

    [Fact]
    public void Power_mode_is_used_when_the_balanced_plan_is_active_and_restored_afterwards()
    {
        var r = new EngineRig();
        r.Engine.Activate(EngineRig.Set(ActionIds.Power));
        Assert.Equal(PowerOverlayTarget.BestPerformance.ToString(), r.Targets.Overlay.Value);
        Assert.Equal(TestHw.Balanced, r.Targets.Scheme.Value);                       // the plan itself was not touched
        r.Engine.Deactivate();
        Assert.Equal(Guid.Empty.ToString(), r.Targets.Overlay.Value);
    }

    [Fact]
    public void The_high_performance_plan_is_used_when_there_is_no_power_mode_and_the_original_plan_comes_back()
    {
        var r = new EngineRig { Hw = TestHw.Make(power: TestHw.Power(modeSupported: false, active: "acer-custom-plan")) };
        r.Targets.Scheme.Value = "acer-custom-plan";
        r.Engine.Activate(EngineRig.Set(ActionIds.Power));
        Assert.Equal(TestHw.HighPerf, r.Targets.Scheme.Value);
        r.Engine.Deactivate();
        Assert.Equal("acer-custom-plan", r.Targets.Scheme.Value);
    }

    [Fact]
    public void An_already_active_high_performance_plan_is_left_alone()
    {
        var r = new EngineRig { Hw = TestHw.Make(power: TestHw.Power(active: TestHw.HighPerf)) };
        r.Targets.Scheme.Value = TestHw.HighPerf;
        r.Engine.Activate(EngineRig.Set(ActionIds.Power));
        Assert.Equal(0, r.Targets.Scheme.Writes + r.Targets.Overlay.Writes);
        Assert.Equal(ActionState.AlreadyInPlace, r.Engine.Reports.Single().State);
        Assert.Empty(r.Tx.Entries);
    }

    [Fact]
    public void A_pc_with_no_usable_power_option_is_skipped_with_a_reason()
    {
        var r = new EngineRig { Hw = TestHw.Make(power: TestHw.Power(highPlan: false, modeSupported: false)) };
        r.Engine.Activate(EngineRig.Set(ActionIds.Power));
        Assert.Equal(ActionState.Skipped, r.Engine.Reports.Single().State);
    }

    [Fact]
    public void Power_is_skipped_until_the_hardware_has_been_scanned()
    {
        var r = new EngineRig { Hw = null };
        r.Engine.Activate(EngineRig.Set(ActionIds.Power));
        Assert.Equal(ActionState.Skipped, r.Engine.Reports.Single().State);
    }

    [Fact]
    public void The_windows_game_mode_flag_is_set_and_an_absent_original_is_restored_as_absent()
    {
        var r = new EngineRig();
        r.Engine.Activate(EngineRig.Set(ActionIds.GameMode));
        Assert.Equal("dword:1", r.Targets.GameBar.Value);
        r.Engine.Deactivate();
        Assert.Null(r.Targets.GameBar.Value);
    }

    // ---------------- memory ----------------
    [Fact]
    public void Memory_is_not_trimmed_when_plenty_is_free()
    {
        var r = new EngineRig { Mem = (16L << 30, 8L << 30) };
        r.World.Roblox(); var chrome = r.World.Add("chrome");
        r.Engine.Activate(EngineRig.Set(ActionIds.Memory));
        Assert.Equal(0, chrome.Trimmed);
        Assert.Equal(ActionState.Skipped, r.Engine.Reports.Single(x => x.ActionId == ActionIds.Memory).State);
    }

    [Fact]
    public void Memory_is_trimmed_once_per_roblox_when_it_is_short_and_the_report_says_nothing_needs_restoring()
    {
        var r = new EngineRig { Mem = (16L << 30, 1L << 30) };
        r.World.Roblox(); var chrome = r.World.Add("chrome"); var other = r.World.Add("unknown");
        r.Engine.Activate(EngineRig.Set(ActionIds.Memory));
        r.Engine.Tick(); r.Engine.Tick();
        Assert.Equal(50 << 20, chrome.Trimmed);
        Assert.Equal(0, other.Trimmed);
        Assert.True(r.Engine.MemoryFreedMb > 0);
        Assert.True(r.Engine.Deactivate().FullyRestored);
    }

    // ---------------- in-process resources ----------------
    [Fact]
    public void Timer_and_keep_awake_are_released_when_game_mode_ends()
    {
        var r = new EngineRig();
        r.Engine.Activate(EngineRig.Set(ActionIds.Timer, ActionIds.Awake));
        Assert.True(r.Res.TimerOn); Assert.True(r.Res.AwakeOn);
        r.Engine.Deactivate();
        Assert.False(r.Res.TimerOn); Assert.False(r.Res.AwakeOn);
        Assert.Equal(r.Res.TimerBegins, r.Res.TimerEnds);
    }

    // ---------------- robustness ----------------
    [Fact]
    public void A_detection_failure_during_a_tick_is_survived_and_nothing_is_changed()
    {
        var r = new EngineRig();
        r.World.Roblox();
        r.Engine.Activate(EngineRig.Set(ActionIds.Priority));
        r.ThrowOnDetect = true;
        var ex = Record.Exception(() => r.Engine.Tick());
        Assert.Null(ex);
        Assert.True(r.Engine.IsActive);
    }

    [Fact]
    public void Activating_twice_does_not_stack_changes()
    {
        var r = new EngineRig();
        var game = r.World.Roblox();
        r.Engine.Activate(EngineRig.Set(ActionIds.Priority));
        r.Engine.Activate(EngineRig.Set(ActionIds.Priority));
        Assert.Single(r.Tx.Entries);
        r.Engine.Deactivate();
        Assert.Equal(PriorityNames.Normal, game.Priority);
        Assert.Equal(RestoreReport.Empty, r.Engine.Deactivate());      // and ending twice is harmless
    }

    [Fact]
    public void A_partial_restore_failure_is_reported_and_keeps_the_record_for_the_next_run()
    {
        var r = new EngineRig();
        r.World.Roblox();
        r.Engine.Activate(EngineRig.Set(ActionIds.GameMode));
        r.Targets.GameBar.FailWrite = true;
        var rep = r.Engine.Deactivate();
        Assert.False(rep.FullyRestored);
        Assert.True(r.Tx.HasPending);
        Assert.False(r.Engine.IsActive);
    }

    // ---------------- crash recovery ----------------
    [Fact]
    public void After_a_crash_the_next_start_restores_everything_and_a_second_recovery_does_nothing()
    {
        var r = new EngineRig();
        var game = r.World.Roblox(); var chrome = r.World.Add("chrome");
        game.Priority = PriorityNames.AboveNormal;
        r.Engine.Activate(EngineRig.Set(ActionIds.Priority, ActionIds.Calm, ActionIds.GameMode, ActionIds.Power));
        Assert.Equal(PriorityNames.High, game.Priority);
        // The process dies here: no Deactivate. The new run starts with the same disk and the same Windows.
        r.Build();
        var rep = r.Engine.RecoverAtStartup();
        Assert.Equal(PriorityNames.AboveNormal, game.Priority);
        Assert.Equal(PriorityNames.Normal, chrome.Priority);
        Assert.Null(r.Targets.GameBar.Value);
        Assert.Equal(Guid.Empty.ToString(), r.Targets.Overlay.Value);
        Assert.True(rep.FullyRestored);
        Assert.True(rep.Total >= 4);
        Assert.Equal(0, r.Engine.RecoverAtStartup().Total);
    }

    [Fact]
    public void After_a_restart_of_the_whole_pc_processes_that_no_longer_exist_are_not_an_error_but_system_settings_are_restored()
    {
        var r = new EngineRig();
        var game = r.World.Roblox();
        r.Engine.Activate(EngineRig.Set(ActionIds.Priority, ActionIds.GameMode));
        // Reboot: every process is new (new start times), registry values persist.
        r.World.Procs.Clear();
        var newGame = r.World.Roblox();
        r.Build();
        var rep = r.Engine.RecoverAtStartup();
        Assert.True(rep.FullyRestored);
        Assert.Equal(PriorityNames.Normal, newGame.Priority);       // an unrelated new process was not touched
        Assert.Null(r.Targets.GameBar.Value);
        Assert.Equal(1, rep.Obsolete);
    }

    [Fact]
    public void An_unreadable_recovery_file_is_reported_and_does_not_crash_startup()
    {
        var r = new EngineRig();
        r.Store.ForcedLoad = LoadState.Unreadable;
        r.Build();
        var rep = r.Engine.RecoverAtStartup();
        Assert.Equal(RestoreReport.Empty, rep);
        Assert.Equal(LoadState.Unreadable, r.Tx.LoadResult.State);
        Assert.Contains(r.Logs, l => l.Contains("could not be read"));
    }
}

public class BackgroundPolicyTests
{
    [Fact]
    public void No_background_candidate_is_also_protected()
    {
        foreach (var name in BackgroundPolicy.Candidates)
            Assert.False(BackgroundPolicy.Protected.Contains(name), name + " is on both lists");
    }

    [Theory]
    [InlineData("MsMpEng")] [InlineData("csrss")] [InlineData("dwm")] [InlineData("explorer")] [InlineData("audiodg")] [InlineData("narrator")]
    [InlineData("nvcontainer")] [InlineData("avp")] [InlineData("Nighty")] [InlineData("RobloxPlayerBeta")] [InlineData("Froststrap")] [InlineData("RobloxCrashHandler")]
    public void Essential_programs_are_never_eligible(string name)
    {
        Assert.False(BackgroundPolicy.IsCandidateName(name));
        Assert.True(BackgroundPolicy.IsProtectedName(name));
    }

    [Theory]
    [InlineData("chrome")] [InlineData("CHROME")] [InlineData("steam")] [InlineData("onedrive")]
    public void Known_background_apps_are_eligible(string name) => Assert.True(BackgroundPolicy.IsCandidateName(name));

    [Theory]
    [InlineData("")] [InlineData(null)] [InlineData("somegame")] [InlineData("svchost")]
    public void Anything_unknown_is_left_alone(string? name) => Assert.False(BackgroundPolicy.IsCandidateName(name));
}
