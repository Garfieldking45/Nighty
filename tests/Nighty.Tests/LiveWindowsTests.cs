using System.Text.Json;
using Nighty.Services.Booster;
using Xunit;
using Xunit.Abstractions;

namespace Nighty.Tests;

/// <summary>Runs the real Windows readers on whatever PC runs the tests. They only read, and assert only what every Windows PC has.</summary>
public class LiveSmokeTests
{
    private readonly ITestOutputHelper _out;
    public LiveSmokeTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Real_hardware_discovery_reads_this_pc()
    {
        using var src = new WindowsHardwareSource();
        var hw = src.Discover();
        _out.WriteLine(JsonSerializer.Serialize(hw, new JsonSerializerOptions { WriteIndented = true }));
        _out.WriteLine("fingerprint " + hw.Fingerprint);
        Assert.True(hw.Cpu.LogicalProcessors > 0);
        Assert.True(hw.Memory.TotalBytes > 1L << 30);
        Assert.Equal(hw.Fingerprint, src.Discover().Fingerprint);   // stable between scans
    }
}

public class LiveTelemetrySmokeTests
{
    private readonly ITestOutputHelper _out;
    public LiveTelemetrySmokeTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Real_telemetry_samples_this_pc_and_reports_gaps()
    {
        using var hwSource = new WindowsHardwareSource();
        var hw = hwSource.Discover();
        var procs = new WindowsProcessSource();
        var game = new RobloxGameLocator(procs);
        var src = new WindowsTelemetrySource(() => hw, game, procs, hwSource.Nvml, () => null);
        TelemetrySample? last = null;
        for (int i = 0; i < 4; i++) { last = src.Sample(); Thread.Sleep(1000); }
        _out.WriteLine(JsonSerializer.Serialize(last, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        foreach (var s in src.Sources) _out.WriteLine($"{(s.Available ? "OK  " : "MISS")} {s.Name}: {s.Detail}");
        Assert.NotNull(last);
        Assert.NotNull(last!.Cpu.TotalPercent);
        Assert.True(last.Memory.TotalBytes > 0);
        var r = RobloxClientDetector.Detect(procs, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _out.WriteLine($"Roblox running={r.IsRunning} launcher={r.Launcher} launcherRunning={r.LauncherRunning} notes={string.Join(';', r.Notes)}");
    }
}

public class LivePlanTests
{
    private readonly ITestOutputHelper _out;
    public LivePlanTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void The_plan_for_this_pc_can_be_built_from_real_readings_without_changing_anything()
    {
        using var hwSource = new WindowsHardwareSource();
        var hw = hwSource.Discover();
        string? planBefore = PowerSchemeTarget.Active();
        var procs = new WindowsProcessSource();
        var game = RobloxClientDetector.Detect(procs, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var state = new WindowsSystemStateReader().Read(game, new HashSet<string>(), new HashSet<string>());
        var plan = new RecommendationEngine().Evaluate(new EvaluationContext(hw, WindowStats.Empty, state, DateTimeOffset.UtcNow));
        foreach (var i in plan.Items) _out.WriteLine($"{i.Applicability,-14} {i.Id,-13} {i.Title} :: {i.Reason}");
        foreach (var f in plan.Findings) _out.WriteLine($"[{f.Level}] {f.Subject}: {f.Statement} | {string.Join(" / ", f.Evidence)}");
        _out.WriteLine("Balanced: " + string.Join(",", plan.Select(BoosterProfile.Balanced)));
        _out.WriteLine("Performance: " + string.Join(",", plan.Select(BoosterProfile.Performance)));
        Assert.NotEmpty(plan.Items);
        Assert.Equal(planBefore, PowerSchemeTarget.Active());   // read-only: the power plan is unchanged
    }
}
