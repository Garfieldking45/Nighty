using Nighty.Services.Booster;
using Xunit;

namespace Nighty.Tests;

public class AnalysisTests
{
    private static IReadOnlyList<Finding> Analyze(HardwareProfile hw, int n, Func<int, DateTimeOffset, TelemetrySample> make, FrameRateConfig? cfg = null)
        => BottleneckAnalyzer.Analyze(hw, TestHw.Stats(TestHw.Samples(hw, n, make)), cfg);

    [Fact]
    public void Too_few_samples_give_a_limitation_and_no_conclusions()
    {
        var hw = TestHw.Make();
        var f = Analyze(hw, 3, (_, at) => TestHw.Sample(at, hw, gpuGame: 99));
        Assert.Contains(f, x => x.Subject == FindingSubject.Telemetry && x.Level == EvidenceKind.Limitation);
        Assert.DoesNotContain(f, x => x.Subject == FindingSubject.Gpu);
    }

    [Fact]
    public void No_measurements_while_roblox_is_closed_says_so_instead_of_diagnosing()
    {
        var hw = TestHw.Make();
        var f = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, game: false));
        Assert.Contains(f, x => x.Subject == FindingSubject.Telemetry && x.Statement.Contains("not running"));
        Assert.DoesNotContain(f, x => x.Subject is FindingSubject.Gpu or FindingSubject.Cpu or FindingSubject.Adapter);
    }

    [Fact]
    public void A_fully_busy_gpu_is_a_hypothesis_not_a_confirmed_limit()
    {
        var hw = TestHw.Make();
        var f = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, gpuGame: 99));
        var g = Assert.Single(f, x => x.Subject == FindingSubject.Gpu);
        Assert.Equal(EvidenceKind.Hypothesis, g.Level);
        Assert.NotNull(g.HowToVerify);
    }

    [Fact]
    public void Low_gpu_use_alone_never_blames_the_processor()
    {
        var hw = TestHw.Make();
        var f = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, gpuGame: 30, busiestCore: 40));
        Assert.DoesNotContain(f, x => x.Subject == FindingSubject.Cpu);
        var g = Assert.Single(f, x => x.Subject == FindingSubject.Gpu);
        Assert.Contains("does not prove", string.Join(" ", g.Evidence));
    }

    [Fact]
    public void A_hot_core_with_a_quiet_gpu_is_only_a_low_confidence_possibility_and_asks_for_real_frame_timing()
    {
        var hw = TestHw.Make();
        var f = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, gpuGame: 30, busiestCore: 97));
        var c = Assert.Single(f, x => x.Subject == FindingSubject.Cpu);
        Assert.Equal(EvidenceKind.Hypothesis, c.Level);
        Assert.Equal(Confidence.Low, c.Confidence);
        Assert.Contains(f, x => x.Subject == FindingSubject.FrameRate && x.Level == EvidenceKind.Limitation);
    }

    [Fact]
    public void The_integrated_gpu_doing_the_work_is_reported_as_an_observation_with_high_confidence()
    {
        var hw = TestHw.Make();
        var f = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, gpuGame: 60, renderAdapter: 1));      // adapter 1 = Intel UHD
        var a = Assert.Single(f, x => x.Subject == FindingSubject.Adapter);
        Assert.Equal(EvidenceKind.Observation, a.Level);
        Assert.Equal(Confidence.High, a.Confidence);
        Assert.Contains("integrated", a.Statement);
    }

    [Fact]
    public void Roblox_on_the_dedicated_card_is_reported_plainly()
    {
        var hw = TestHw.Make();
        var f = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, gpuGame: 60, renderAdapter: 0));
        var a = Assert.Single(f, x => x.Subject == FindingSubject.Adapter);
        Assert.DoesNotContain("integrated", a.Statement);
        Assert.Contains("GTX 1650", a.Statement);
    }

    [Fact]
    public void A_frame_rate_pinned_to_the_configured_limit_is_recognised()
    {
        var hw = TestHw.Make();
        var cfg = TestHw.Config(cap: 240);
        var f = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, fps: 240), cfg);
        Assert.Contains(f, x => x.Subject == FindingSubject.FrameRate && x.Statement.Contains("configured limit of 240"));
    }

    [Fact]
    public void A_frame_rate_locked_to_the_display_refresh_is_recognised_but_with_medium_confidence()
    {
        var hw = TestHw.Make();     // display is 144 Hz
        var f = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, fps: 144));
        var x = Assert.Single(f, y => y.Subject == FindingSubject.FrameRate && y.Statement.Contains("locked"));
        Assert.Equal(Confidence.Medium, x.Confidence);
    }

    [Fact]
    public void Thermal_throttling_is_claimed_only_when_the_driver_reports_it()
    {
        var hw = TestHw.Make();
        var hot = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, thermal: true, temp: 88));
        Assert.Contains(hot, x => x.Subject == FindingSubject.Thermal && x.Level == EvidenceKind.Observation && x.Statement.Contains("thermal slowdown") && !x.Statement.Contains("no thermal"));

        var warmButFine = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, thermal: false, temp: 80));
        Assert.Contains(warmButFine, x => x.Statement.Contains("no thermal slowdown"));

        var unknown = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, thermal: null, temp: null));
        Assert.DoesNotContain(unknown, x => x.Subject == FindingSubject.Thermal && x.Level == EvidenceKind.Observation);
        Assert.Contains(unknown, x => x.Subject == FindingSubject.Thermal && x.Level == EvidenceKind.Limitation && x.Statement.Contains("Processor temperature"));
    }

    [Fact]
    public void Memory_pressure_needs_low_free_memory()
    {
        var hw = TestHw.Make();
        var tight = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, availPct: 6, faults: 300));
        Assert.Contains(tight, x => x.Subject == FindingSubject.Memory && x.Statement.Contains("tight"));
        var fine = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, availPct: 50));
        Assert.Contains(fine, x => x.Subject == FindingSubject.Memory && x.Statement.Contains("fine"));
    }

    [Fact]
    public void A_performance_limit_is_reported_only_while_the_processor_is_busy()
    {
        var hw = TestHw.Make();
        var busy = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, cpu: 70, perfLimit: 70));
        Assert.Contains(busy, x => x.Subject == FindingSubject.Power && x.Statement.Contains("holding the processor back"));
        var idle = Analyze(hw, 20, (_, at) => TestHw.Sample(at, hw, cpu: 5, perfLimit: 70));
        Assert.DoesNotContain(idle, x => x.Statement.Contains("holding the processor back"));
    }

    [Fact]
    public void Battery_use_on_a_laptop_is_flagged()
    {
        var hw = TestHw.Make(power: TestHw.Power(PowerSource.Battery));
        var f = Analyze(hw, 2, (_, at) => TestHw.Sample(at, hw));
        Assert.Contains(f, x => x.Subject == FindingSubject.Power && x.Statement.Contains("battery"));
    }

    [Fact]
    public void Stale_samples_are_left_out_of_the_statistics()
    {
        var hw = TestHw.Make();
        var old = Enumerable.Range(0, 20).Select(i => TestHw.Sample(DateTimeOffset.UtcNow.AddMinutes(-30).AddSeconds(i), hw, gpuGame: 99)).ToList();
        var stats = WindowStats.From(old, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60));
        Assert.Equal(0, stats.Count);
    }

    [Fact]
    public void Missing_values_are_skipped_not_averaged_as_zero()
    {
        var hw = TestHw.Make();
        var samples = TestHw.Samples(hw, 10, (i, at) => TestHw.Sample(at, hw, faults: i % 2 == 0 ? 100 : null));
        var stats = TestHw.Stats(samples);
        Assert.Equal(100, stats.HardFaultsMean!.Value, 6);
        var none = TestHw.Stats(TestHw.Samples(hw, 10, (_, at) => TestHw.Sample(at, hw, faults: null, perfLimit: null)));
        Assert.Null(none.HardFaultsMean);
        Assert.Null(none.PerformanceLimitMin);
    }

    [Fact]
    public void Lower_detail_flags_are_reported_and_warn_that_runs_are_not_comparable()
    {
        var hw = TestHw.Make();
        var cfg = TestHw.Config() with { LowDetailFlags = new[] { "FIntFRMMinGrassDistance=0" } };
        var f = Analyze(hw, 2, (_, at) => TestHw.Sample(at, hw), cfg);
        var x = Assert.Single(f, y => y.Subject == FindingSubject.Config && y.Statement.Contains("lower rendering detail"));
        Assert.Contains("not comparable", x.HowToVerify);
    }
}

public class TelemetryServiceTests
{
    private sealed class CountingSource : ITelemetrySource
    {
        public int Calls; public bool Throw; public readonly HardwareProfile Hw = TestHw.Make();
        public IReadOnlyList<DataSourceStatus> Sources => Array.Empty<DataSourceStatus>();
        public TelemetrySample Sample()
        {
            if (Throw) throw new InvalidOperationException("counter vanished");
            Interlocked.Increment(ref Calls);
            return TestHw.Sample(DateTimeOffset.UtcNow, Hw);
        }
    }

    [Fact]
    public void History_is_bounded()
    {
        var src = new CountingSource();
        using var svc = new TelemetryService(src, capacity: 10);
        for (int i = 0; i < 50; i++) svc.SampleOnce();
        Assert.Equal(10, svc.Snapshot().Count);
        Assert.Equal(svc.Snapshot().Last(), svc.Latest);
    }

    [Fact]
    public void A_failing_sample_is_reported_and_does_not_end_sampling()
    {
        var src = new CountingSource { Throw = true };
        using var svc = new TelemetryService(src);
        Exception? seen = null;
        svc.SampleFailed += e => seen = e;
        Assert.Null(svc.SampleOnce());
        Assert.NotNull(seen);
        src.Throw = false;
        Assert.NotNull(svc.SampleOnce());
    }

    [Fact]
    public async Task Sampling_runs_only_while_someone_is_subscribed_and_stops_when_they_leave()
    {
        var src = new CountingSource();
        using var svc = new TelemetryService(src, TimeSpan.FromMilliseconds(15));
        Assert.False(svc.IsRunning);
        var sub = svc.Subscribe();
        var sub2 = svc.Subscribe();
        await Task.Delay(150);
        Assert.True(svc.IsRunning);
        Assert.True(src.Calls >= 3);
        sub.Dispose();
        Assert.True(svc.IsRunning);          // one subscriber is still watching
        sub2.Dispose();
        sub2.Dispose();                      // disposing twice is harmless
        Assert.False(svc.IsRunning);
        await Task.Delay(60);
        int after = src.Calls;
        await Task.Delay(100);
        Assert.Equal(after, src.Calls);      // nothing keeps polling after the last subscriber left
    }

    [Fact]
    public async Task Disposing_the_service_stops_polling_and_later_subscriptions_do_nothing()
    {
        var src = new CountingSource();
        var svc = new TelemetryService(src, TimeSpan.FromMilliseconds(15));
        svc.Subscribe();
        await Task.Delay(60);
        svc.Dispose();
        int after = src.Calls;
        await Task.Delay(100);
        Assert.Equal(after, src.Calls);
        svc.Subscribe();
        await Task.Delay(60);
        Assert.Equal(after, src.Calls);
    }

    [Fact]
    public void The_snapshot_window_returns_only_recent_samples()
    {
        var src = new CountingSource();
        using var svc = new TelemetryService(src);
        svc.SampleOnce();
        var future = DateTimeOffset.UtcNow.AddMinutes(10);
        Assert.Empty(svc.Snapshot(TimeSpan.FromSeconds(30), future));
        Assert.Single(svc.Snapshot(TimeSpan.FromSeconds(30)));
    }
}
