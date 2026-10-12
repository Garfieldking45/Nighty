using Nighty.Services.Booster;
using Xunit;

namespace Nighty.Tests;

public class FrameMathTests
{
    [Fact]
    public void Steady_frames_give_exact_figures()
    {
        var s = FrameMath.Compute(Enumerable.Repeat(10.0, 100).ToList())!;
        Assert.Equal(100, s.AvgFps, 6);
        Assert.Equal(10, s.MedianMs, 6); Assert.Equal(10, s.P95Ms, 6); Assert.Equal(10, s.P99Ms, 6);
        Assert.Equal(100, s.OnePercentLowFps!.Value, 6);
        Assert.Equal(1.0, s.DurationSec, 6);
    }

    [Fact]
    public void One_slow_frame_in_a_hundred_defines_the_one_percent_low()
    {
        var frames = Enumerable.Repeat(10.0, 99).Append(100.0).ToList();
        var s = FrameMath.Compute(frames)!;
        Assert.Equal(100 * 1000.0 / 1090, s.AvgFps, 6);            // frames / total time, not the mean of per-frame FPS
        Assert.Equal(10, s.OnePercentLowFps!.Value, 6);            // the single slowest frame (1% of 100) ran at 10 FPS
        Assert.Equal(10.9, s.P99Ms, 6);                            // interpolated between ranks
        Assert.Equal(100, s.MaxMs);
    }

    [Fact]
    public void Percentiles_interpolate_between_ranks()
    {
        var v = new double[] { 1, 2, 3, 4, 5 };
        Assert.Equal(3, FrameMath.Percentile(v, 50), 9);
        Assert.Equal(4.8, FrameMath.Percentile(v, 95), 9);
        Assert.Equal(1, FrameMath.Percentile(v, 0), 9);
        Assert.Equal(5, FrameMath.Percentile(v, 100), 9);
    }

    [Fact]
    public void Too_few_frames_give_no_statistics_and_the_one_percent_low_needs_a_hundred_frames()
    {
        Assert.Null(FrameMath.Compute(Enumerable.Repeat(10.0, 29).ToList()));
        Assert.Null(FrameMath.Compute(Enumerable.Repeat(10.0, 60).ToList())!.OnePercentLowFps);
    }

    [Fact]
    public void Invalid_and_extreme_intervals_are_dropped_and_counted_rather_than_corrupting_the_result()
    {
        var frames = Enumerable.Repeat(10.0, 100).Concat(new[] { double.NaN, -5, 0, double.PositiveInfinity, 5000 }).ToList();
        var s = FrameMath.Compute(frames)!;
        Assert.Equal(100, s.FrameCount);
        Assert.Equal(5, s.DiscardedFrames);
        Assert.Equal(100, s.AvgFps, 6);
    }

    [Fact]
    public void Timestamps_become_intervals_even_when_out_of_order()
    {
        var d = FrameMath.Intervals(new[] { 30.0, 10.0, 20.0, 45.0 });
        Assert.Equal(new[] { 10.0, 10.0, 15.0 }, d);
    }
}

public class BenchmarkComparerTests
{
    private static BenchmarkContext Ctx(string fp = "hw1", string scene = "baseplate", PowerSource p = PowerSource.Ac, FrameSourceKind k = FrameSourceKind.PresentEvents, string display = "1920x1080@144Hz", string limit = "1000", IReadOnlyList<string>? applied = null)
        => new(fp, "pc", LauncherKind.Froststrap, "version-1", p, "High performance", PowerMode.Balanced, applied is { Count: > 0 }, applied ?? Array.Empty<string>(), limit, "1000", 21, "", display, "GTX", scene, "", k, 1.0);

    private static BenchmarkRun Run(double avg, RunGroup g, double? low = null, double? p95 = null, double seconds = 60, BenchmarkContext? ctx = null)
        => new()
        {
            Label = $"{g} {avg}", Group = g, PlannedSeconds = seconds, Context = ctx ?? Ctx(),
            Frames = new FrameStats(1000, 0, seconds, avg, 1000 / avg, p95 ?? 1000 / avg * 1.3, 1000 / avg * 1.6, 50, low ?? avg * 0.6),
        };

    private static BenchmarkRun[] Runs(RunGroup g, BenchmarkContext? ctx, params double[] avgs) => avgs.Select(a => Run(a, g, ctx: ctx)).ToArray();

    [Fact]
    public void A_clear_improvement_across_several_runs_is_called_an_improvement()
    {
        var r = BenchmarkComparer.Compare(Runs(RunGroup.Baseline, null, 100, 102, 98), Runs(RunGroup.After, null, 120, 118, 122));
        Assert.Equal(Verdict.Improved, r.Verdict);
        Assert.Contains(r.Metrics, m => m.Name == "Average FPS" && m.Outcome == "Improved" && m.ChangePercent > 15);
    }

    [Fact]
    public void A_clear_drop_is_reported_as_a_regression()
    {
        var r = BenchmarkComparer.Compare(Runs(RunGroup.Baseline, null, 120, 118, 122), Runs(RunGroup.After, null, 100, 102, 98));
        Assert.Equal(Verdict.Regressed, r.Verdict);
        Assert.StartsWith("Worse", r.Headline);
    }

    [Fact]
    public void A_small_difference_inside_noisy_runs_is_inconclusive()
    {
        var r = BenchmarkComparer.Compare(Runs(RunGroup.Baseline, null, 100, 112, 88), Runs(RunGroup.After, null, 103, 115, 91));
        Assert.Equal(Verdict.Inconclusive, r.Verdict);
        Assert.Contains("noise", string.Join(" ", r.Metrics.Select(m => m.Explanation)));
        Assert.Contains(r.Warnings, w => w.Contains("differ from each other"));
    }

    [Fact]
    public void A_single_run_per_side_can_never_be_called_an_improvement_however_big_the_difference()
    {
        var r = BenchmarkComparer.Compare(Runs(RunGroup.Baseline, null, 100), Runs(RunGroup.After, null, 300));
        Assert.Equal(Verdict.Inconclusive, r.Verdict);
        Assert.Contains(r.Warnings, w => w.Contains("Only one run"));
        Assert.All(r.Metrics, m => Assert.Equal("Not enough runs", m.Outcome));
    }

    [Fact]
    public void A_tiny_but_perfectly_consistent_difference_is_not_practically_meaningful()
    {
        var r = BenchmarkComparer.Compare(Runs(RunGroup.Baseline, null, 100.0, 100.1, 99.9), Runs(RunGroup.After, null, 100.8, 100.9, 100.7));
        Assert.Equal(Verdict.Inconclusive, r.Verdict);      // statistically clear but under 2%
    }

    [Fact]
    public void Better_average_but_worse_frame_times_is_mixed_not_a_win()
    {
        var before = new[] { Run(100, RunGroup.Baseline, low: 80, p95: 12), Run(101, RunGroup.Baseline, low: 81, p95: 12.1), Run(99, RunGroup.Baseline, low: 79, p95: 11.9) };
        var after = new[] { Run(120, RunGroup.After, low: 50, p95: 20), Run(121, RunGroup.After, low: 51, p95: 20.2), Run(119, RunGroup.After, low: 49, p95: 19.8) };
        var r = BenchmarkComparer.Compare(before, after);
        Assert.Equal(Verdict.Mixed, r.Verdict);
    }

    [Fact]
    public void Welch_degrees_of_freedom_make_two_runs_per_side_demanding()
    {
        // With 2 runs per side the critical t is large: a modest difference with a little spread must not pass.
        var r = BenchmarkComparer.Compare(Runs(RunGroup.Baseline, null, 100, 106), Runs(RunGroup.After, null, 108, 113));
        Assert.Equal(Verdict.Inconclusive, r.Verdict);
        var clear = BenchmarkComparer.Compare(Runs(RunGroup.Baseline, null, 100, 100.5), Runs(RunGroup.After, null, 130, 130.5));
        Assert.Equal(Verdict.Improved, clear.Verdict);
    }

    [Fact]
    public void T_critical_values_follow_the_table()
    {
        Assert.Equal(12.706, BenchmarkComparer.TCritical95(1), 3);
        Assert.Equal(2.776, BenchmarkComparer.TCritical95(4), 3);
        Assert.Equal(1.96, BenchmarkComparer.TCritical95(1000), 3);
        double prev = double.MaxValue;
        for (double df = 1; df <= 200; df += 0.5) { double t = BenchmarkComparer.TCritical95(df); Assert.True(t <= prev + 1e-9); prev = t; }
    }

    [Theory]
    [InlineData("hardware")] [InlineData("scene")] [InlineData("power")] [InlineData("source")] [InlineData("display")] [InlineData("limit")] [InlineData("duration")]
    public void Runs_made_under_different_conditions_are_incomparable_and_no_verdict_is_given(string what)
    {
        var a = Runs(RunGroup.Baseline, Ctx(), 100, 101, 99);
        BenchmarkRun[] b = what switch
        {
            "hardware" => Runs(RunGroup.After, Ctx(fp: "other"), 120, 121, 119),
            "scene" => Runs(RunGroup.After, Ctx(scene: "a busy city"), 120, 121, 119),
            "power" => Runs(RunGroup.After, Ctx(p: PowerSource.Battery), 120, 121, 119),
            "source" => Runs(RunGroup.After, Ctx(k: FrameSourceKind.ManualEntry), 120, 121, 119),
            "display" => Runs(RunGroup.After, Ctx(display: "3440x1440@60Hz"), 120, 121, 119),
            "limit" => Runs(RunGroup.After, Ctx(limit: "60"), 120, 121, 119),
            _ => new[] { Run(120, RunGroup.After, seconds: 120), Run(121, RunGroup.After, seconds: 120), Run(119, RunGroup.After, seconds: 120) },
        };
        var r = BenchmarkComparer.Compare(a, b);
        Assert.Equal(Verdict.Incomparable, r.Verdict);
        Assert.Empty(r.Metrics);
        Assert.NotEmpty(r.IncomparableReasons);
    }

    [Fact]
    public void Scene_notes_that_differ_only_in_case_or_spacing_are_the_same_scene()
    {
        var r = BenchmarkComparer.Compare(Runs(RunGroup.Baseline, Ctx(scene: "Baseplate  spawn"), 100, 101, 99), Runs(RunGroup.After, Ctx(scene: "baseplate spawn"), 120, 121, 119));
        Assert.Equal(Verdict.Improved, r.Verdict);
    }

    [Fact]
    public void Comparing_with_no_optimization_applied_anywhere_warns_that_nothing_can_be_attributed()
    {
        var r = BenchmarkComparer.Compare(Runs(RunGroup.Baseline, null, 100, 101, 99), Runs(RunGroup.After, null, 120, 121, 119));
        Assert.Contains(r.Warnings, w => w.Contains("nothing for this comparison to attribute"));
        var applied = Ctx(applied: new[] { "power" });
        var r2 = BenchmarkComparer.Compare(Runs(RunGroup.Baseline, null, 100, 101, 99), Runs(RunGroup.After, applied, 120, 121, 119));
        Assert.DoesNotContain(r2.Warnings, w => w.Contains("nothing for this comparison to attribute"));
    }

    [Fact]
    public void Every_run_is_used_not_just_the_best()
    {
        var withBadRun = BenchmarkComparer.Compare(Runs(RunGroup.Baseline, null, 100, 100, 100), Runs(RunGroup.After, null, 130, 130, 60));
        Assert.NotEqual(Verdict.Improved, withBadRun.Verdict);      // the weak third run counts
    }

    [Fact]
    public void Manual_runs_compare_on_the_figures_that_exist()
    {
        BenchmarkRun M(double a, RunGroup g) => new() { Label = "m", Group = g, PlannedSeconds = 60, ManualAvgFps = a, Context = Ctx(k: FrameSourceKind.ManualEntry) };
        var r = BenchmarkComparer.Compare(new[] { M(100, RunGroup.Baseline), M(101, RunGroup.Baseline), M(99, RunGroup.Baseline) }, new[] { M(130, RunGroup.After), M(131, RunGroup.After), M(129, RunGroup.After) });
        Assert.Equal(Verdict.Improved, r.Verdict);
        Assert.Single(r.Metrics);       // no frame-time data, so only the average could be compared
    }

    [Fact]
    public void Empty_sides_are_inconclusive()
        => Assert.Equal(Verdict.Inconclusive, BenchmarkComparer.Compare(Array.Empty<BenchmarkRun>(), Runs(RunGroup.After, null, 1)).Verdict);
}

public class CsvImportTests
{
    [Fact]
    public void A_presentmon_csv_is_read_and_only_roblox_rows_are_used()
    {
        var csv = "Application,ProcessID,MsBetweenPresents,Dropped\r\n" +
                  string.Join("\r\n", Enumerable.Range(0, 40).Select(_ => "RobloxPlayerBeta.exe,1,8.5,0")) + "\r\n" +
                  string.Join("\r\n", Enumerable.Range(0, 40).Select(_ => "chrome.exe,2,16.6,0"));
        var (frames, err) = CsvFrameImport.Parse(csv);
        Assert.Null(err);
        Assert.Equal(40, frames.Count);
        Assert.All(frames, f => Assert.Equal(8.5, f));
    }

    [Fact]
    public void A_file_with_other_programs_only_is_rejected()
    {
        var csv = "Application,MsBetweenPresents\r\nchrome.exe,16\r\ngame.exe,8";
        Assert.NotNull(CsvFrameImport.Parse(csv).Error);
    }

    [Fact]
    public void Semicolon_separated_frameview_style_files_work()
    {
        var csv = "Application;FrameTime\r\n" + string.Join("\r\n", Enumerable.Range(0, 5).Select(_ => "RobloxPlayerBeta.exe;7,5".Replace("7,5", "7.5")));
        var (frames, err) = CsvFrameImport.Parse(csv);
        Assert.Null(err); Assert.Equal(5, frames.Count);
    }

    [Theory]
    [InlineData("")] [InlineData("a,b\r\n")] [InlineData("Application,Other\r\nRoblox,1")] [InlineData("MsBetweenPresents\r\nabc\r\ndef")]
    public void Garbage_is_rejected_with_a_message_not_an_exception(string csv)
        => Assert.NotNull(CsvFrameImport.Parse(csv).Error);
}

public class BenchmarkStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nighty-bench-" + Guid.NewGuid().ToString("N"));
    public BenchmarkStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static BenchmarkRun Run(int i) => new()
    {
        Label = "r" + i, StartedUtc = new DateTime(2026, 1, 1).AddMinutes(i), PlannedSeconds = 30, Group = RunGroup.After,
        Context = new BenchmarkContext("fp", "pc", LauncherKind.Official, null, PowerSource.Ac, null, PowerMode.Unknown, false, new[] { "power" }, null, null, null, "", null, null, "s", "", FrameSourceKind.PresentEvents, 1),
        Frames = new FrameStats(500, 0, 30, 100, 10, 12, 14, 30, 70),
    };

    [Fact]
    public void Runs_round_trip_and_the_history_is_capped()
    {
        var path = Path.Combine(_dir, "b.json");
        var store = new FileBenchmarkStore(path, maxRuns: 5);
        store.Save(Enumerable.Range(0, 9).Select(Run).ToList());
        var back = store.Load();
        Assert.Equal(5, back.Count);
        Assert.Equal("r8", back.Last().Label);                    // the newest are kept
        Assert.Equal(100, back[0].Frames!.AvgFps);
        Assert.Equal(new[] { "power" }, back[0].Context.AppliedActions);
    }

    [Fact]
    public void A_corrupt_history_file_starts_empty_and_keeps_the_bad_file_aside()
    {
        var path = Path.Combine(_dir, "b.json");
        File.WriteAllText(path, "{{{ nope");
        Assert.Empty(new FileBenchmarkStore(path).Load());
        Assert.Contains(Directory.GetFiles(_dir), f => f.Contains("corrupt"));
    }
}

public class BenchmarkRunnerTests
{
    private sealed class FakeLocator : IGameLocator
    {
        public RobloxSession Session = new(new[] { new RobloxClient(new ProcIdentity(900, 1), @"C:\x\RobloxPlayerBeta.exe", LauncherKind.Froststrap) }, LauncherKind.Froststrap, true, Array.Empty<string>());
        public RobloxSession Find() => Session;
    }

    private sealed class FakeFrames : IFrameSource
    {
        public bool Available = true; public int FrameCount = 400; public double Ms = 5; public bool Disposed; public int Begun;
        public FrameSourceAvailability Availability() => new(Available, Available ? "ok" : "no permission");
        public IFrameRecording Begin(IReadOnlyCollection<int> pids) { Begun++; return new Rec(this); }
        private sealed class Rec : IFrameRecording
        {
            private readonly FakeFrames _f; public Rec(FakeFrames f) => _f = f;
            public int Count => _f.FrameCount;
            public List<double> Stop() => Enumerable.Repeat(_f.Ms, _f.FrameCount).ToList();
            public void Dispose() => _f.Disposed = true;
        }
    }

    private sealed class Src : ITelemetrySource
    {
        public readonly HardwareProfile Hw = TestHw.Make();
        public IReadOnlyList<DataSourceStatus> Sources => Array.Empty<DataSourceStatus>();
        public TelemetrySample Sample() => TestHw.Sample(DateTimeOffset.UtcNow, Hw, fps: 200);
    }

    private static (BenchmarkRunner Runner, FakeLocator Game, FakeFrames Frames, TelemetryService Tel) Make(Func<bool>? fg = null)
    {
        var game = new FakeLocator(); var frames = new FakeFrames();
        var src = new Src();
        var tel = new TelemetryService(src, TimeSpan.FromMilliseconds(10));
        var runner = new BenchmarkRunner(new BenchmarkDeps
        {
            Game = game, Frames = frames, Telemetry = tel, Hardware = () => src.Hw, GameModeActive = () => true, AppliedActions = () => new[] { "power", "gamemode" },
            RobloxForeground = fg ?? (() => true),
        });
        return (runner, game, frames, tel);
    }

    private static BenchmarkRequest Req(string label = "Baseline 1") => new(label, RunGroup.Baseline, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(40), "baseplate", "quality 5");

    [Fact]
    public async Task A_normal_run_records_frames_system_metrics_and_the_conditions_it_was_made_under()
    {
        var (runner, _, frames, tel) = Make();
        using var _t = tel;
        var o = await runner.RunAsync(Req(), null, default, allowShort: true);
        Assert.True(o.Ok, o.Error);
        var run = o.Run!;
        Assert.Equal(200, run.Frames!.AvgFps, 6);               // 400 frames of 5 ms
        Assert.Equal(FrameSourceKind.PresentEvents, run.Context.Source);
        Assert.Equal("baseplate", run.Context.SceneNote);
        Assert.Contains("power", run.Context.AppliedActions);
        Assert.True(run.Context.GameModeActive);
        Assert.True(run.System!.Samples > 0);
        Assert.Contains(run.System.Unavailable, u => u.Contains("CPU temperature"));
        Assert.True(frames.Disposed);
        Assert.Equal(1.0, run.Context.RobloxForegroundFraction);
    }

    [Fact]
    public async Task Without_permission_for_frame_capture_the_run_is_refused_with_the_reason()
    {
        var (runner, _, frames, tel) = Make(); using var _t = tel;
        frames.Available = false;
        var o = await runner.RunAsync(Req(), null, default, allowShort: true);
        Assert.False(o.Ok);
        Assert.Contains("no permission", o.Error);
        Assert.Equal(0, frames.Begun);
    }

    [Fact]
    public async Task A_run_needs_roblox_to_be_running()
    {
        var (runner, game, _, tel) = Make(); using var _t = tel;
        game.Session = RobloxSession.None;
        var o = await runner.RunAsync(Req(), null, default, allowShort: true);
        Assert.Contains("not running", o.Error);
    }

    [Fact]
    public async Task Test_lengths_outside_the_allowed_range_are_refused()
    {
        var (runner, _, _, tel) = Make(); using var _t = tel;
        var o = await runner.RunAsync(Req() with { Duration = TimeSpan.FromSeconds(3) }, null, default);
        Assert.NotNull(o.Error);
    }

    [Fact]
    public async Task Roblox_closing_during_the_test_discards_the_run()
    {
        var (runner, game, frames, tel) = Make(); using var _t = tel;
        var task = runner.RunAsync(Req() with { Duration = TimeSpan.FromMilliseconds(600) }, null, default, allowShort: true);
        await Task.Delay(150);
        game.Session = RobloxSession.None;
        var o = await task;
        Assert.False(o.Ok);
        Assert.Contains("closed during the test", o.Error);
        Assert.True(frames.Disposed);
    }

    [Fact]
    public async Task Cancelling_stops_cleanly_and_saves_nothing()
    {
        var (runner, _, frames, tel) = Make(); using var _t = tel;
        using var cts = new CancellationTokenSource();
        var task = runner.RunAsync(Req() with { Duration = TimeSpan.FromSeconds(5) }, null, cts.Token, allowShort: true);
        await Task.Delay(120);
        cts.Cancel();
        var o = await task;
        Assert.True(o.Cancelled);
        Assert.Null(o.Run);
        Assert.True(frames.Disposed);
        await Task.Delay(50);
        Assert.False(tel.IsRunning);        // the system sampling stopped with the run
    }

    [Fact]
    public async Task Too_few_frames_is_an_error_not_a_made_up_result()
    {
        var (runner, _, frames, tel) = Make(); using var _t = tel;
        frames.FrameCount = 10;
        var o = await runner.RunAsync(Req(), null, default, allowShort: true);
        Assert.False(o.Ok);
        Assert.Contains("too few", o.Error);
    }

    [Fact]
    public async Task Roblox_not_being_in_front_is_recorded_as_a_warning()
    {
        var (runner, _, _, tel) = Make(fg: () => false); using var _t = tel;
        var o = await runner.RunAsync(Req(), null, default, allowShort: true);
        Assert.True(o.Ok);
        Assert.Equal(0, o.Run!.Context.RobloxForegroundFraction);
        Assert.Contains(o.Run.Warnings, w => w.Contains("in front"));
    }

    [Fact]
    public void Hand_entered_and_imported_results_are_labelled_as_such_and_unverified()
    {
        var (runner, _, _, tel) = Make(); using var _t = tel;
        var manual = runner.FromManual("By hand", RunGroup.Baseline, 640, 410, 60, "baseplate", "", "");
        Assert.Equal(FrameSourceKind.ManualEntry, manual.Context.Source);
        Assert.Contains(manual.Warnings, w => w.Contains("could not verify"));
        Assert.Null(manual.System);

        var csv = "Application,MsBetweenPresents\r\n" + string.Join("\r\n", Enumerable.Range(0, 200).Select(_ => "RobloxPlayerBeta.exe,2.5"));
        var imp = runner.FromCsv("PresentMon", RunGroup.After, csv, "baseplate", "");
        Assert.True(imp.Ok);
        Assert.Equal(400, imp.Run!.Frames!.AvgFps, 6);
        Assert.Equal(FrameSourceKind.ImportedCsv, imp.Run.Context.Source);
        Assert.False(runner.FromCsv("bad", RunGroup.After, "x", "", "").Ok);
    }

    [Fact]
    public void Different_client_flags_give_different_signatures_so_such_runs_are_not_mixed()
    {
        var a = TestHw.Config(flag: 9999);
        var b = TestHw.Config(flag: 240);
        var none = TestHw.Config();
        Assert.NotEqual(BenchmarkRunner.FlagSignature(a), BenchmarkRunner.FlagSignature(b));
        Assert.Equal("", BenchmarkRunner.FlagSignature(none));
        Assert.Equal(BenchmarkRunner.FlagSignature(a), BenchmarkRunner.FlagSignature(TestHw.Config(flag: 9999)));
    }
}
