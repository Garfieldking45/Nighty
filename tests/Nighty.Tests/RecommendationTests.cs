using Nighty.Services.Booster;
using Xunit;

namespace Nighty.Tests;

public class ClassifierTests
{
    [Fact]
    public void Nvidia_is_dedicated_intel_uhd_is_integrated_and_the_basic_render_driver_is_software()
    {
        Assert.Equal(GpuKind.Dedicated, GpuClassifier.Classify(0x10DE, "NVIDIA GeForce GTX 1650", 4L << 30, false).Kind);
        Assert.Equal(GpuKind.Integrated, GpuClassifier.Classify(0x8086, "Intel(R) UHD Graphics", 128L << 20, false).Kind);
        Assert.Equal(GpuKind.Software, GpuClassifier.Classify(0x1414, "Microsoft Basic Render Driver", null, true).Kind);
    }

    [Fact]
    public void An_intel_arc_card_with_its_own_memory_is_dedicated()
        => Assert.Equal(GpuKind.Dedicated, GpuClassifier.Classify(0x8086, "Intel(R) Arc(TM) A750 Graphics", 8L << 30, false).Kind);

    [Theory]
    [InlineData("AMD Radeon RX 6600", 8L << 30, GpuKind.Dedicated)]
    [InlineData("AMD Radeon(TM) Graphics", 512L << 20, GpuKind.Integrated)]
    [InlineData("AMD Radeon(TM) Graphics", 4L << 30, GpuKind.Integrated)]      // an APU with a big BIOS reservation is still integrated
    [InlineData("AMD Radeon 780M Graphics", 512L << 20, GpuKind.Integrated)]
    [InlineData("AMD Radeon Pro W6600", 8L << 30, GpuKind.Dedicated)]
    [InlineData("AMD Radeon Something New", 512L << 20, GpuKind.Unknown)]       // not guessed
    public void Amd_adapters_are_classified_from_their_name_and_never_guessed(string name, long mem, GpuKind expected)
        => Assert.Equal(expected, GpuClassifier.Classify(0x1002, name, mem, false).Kind);

    [Fact]
    public void An_unknown_vendor_is_unknown()
        => Assert.Equal(GpuKind.Unknown, GpuClassifier.Classify(0x1234, "Mystery GPU", 2L << 30, false).Kind);

    [Theory]
    [InlineData(new[] { 10 }, "Acer", "Nitro AN515-57", false, PlatformKind.Laptop)]
    [InlineData(new[] { 3 }, "ASUS", "ROG", false, PlatformKind.Desktop)]
    [InlineData(new[] { 13 }, "HP", "All-in-one", false, PlatformKind.Desktop)]
    [InlineData(new[] { 1 }, "VMware, Inc.", "VMware Virtual Platform", false, PlatformKind.VirtualMachine)]
    [InlineData(new[] { 3 }, "Microsoft Corporation", "Virtual Machine", false, PlatformKind.VirtualMachine)]
    [InlineData(new int[0], "Unknown", "X", true, PlatformKind.Laptop)]
    [InlineData(new int[0], "Unknown", "X", false, PlatformKind.Unknown)]
    [InlineData(new[] { 2 }, "Unknown", "X", false, PlatformKind.Unknown)]
    public void The_kind_of_computer_comes_from_the_chassis_type_and_model(int[] chassis, string maker, string model, bool battery, PlatformKind expected)
        => Assert.Equal(expected, PlatformClassifier.Classify(chassis, maker, model, battery).Kind);
}

public class HardwareProfileTests
{
    [Fact]
    public void Hybrid_graphics_means_one_integrated_and_one_dedicated_adapter()
    {
        Assert.True(TestHw.Make().HasHybridGraphics);
        Assert.False(TestHw.Make(gpus: new[] { TestHw.Nvidia(0) }).HasHybridGraphics);
        Assert.False(TestHw.Make(gpus: new[] { TestHw.IntelIgpu(0) }).HasHybridGraphics);
        Assert.False(TestHw.Make(gpus: new[] { TestHw.Nvidia(0), TestHw.AmdDiscrete(1, 400) }).HasHybridGraphics);
    }

    [Fact]
    public void The_fingerprint_changes_with_hardware_or_drivers_but_not_with_power_state()
    {
        var a = TestHw.Make();
        var onBattery = TestHw.Make(power: TestHw.Power(PowerSource.Battery));
        Assert.Equal(a.Fingerprint, onBattery.Fingerprint);
        var newDriver = a with { Gpus = a.Gpus.Select(g => g.Vendor == GpuVendor.Nvidia ? g with { DriverVersion = "99.9" } : g).ToList() };
        Assert.NotEqual(a.Fingerprint, newDriver.Fingerprint);
        Assert.NotEqual(a.Fingerprint, TestHw.Make(ramGb: 32).Fingerprint);
        Assert.NotEqual(a.Fingerprint, TestHw.Make(platform: PlatformKind.Desktop).Fingerprint);
    }

    [Fact]
    public void Refreshing_the_power_state_updates_the_profile_and_tells_listeners_the_hardware_did_not_change()
    {
        var src = new FakeHardwareSource(TestHw.Make());
        using var svc = new HardwareService(src);
        var events = new List<bool>();
        svc.Updated += (_, _, identityChanged) => events.Add(identityChanged);
        svc.Rescan();
        src.Power = TestHw.Power(PowerSource.Battery);
        Assert.True(svc.RefreshPower());
        Assert.Equal(PowerSource.Battery, svc.Current!.Power.Source);
        Assert.False(svc.RefreshPower());                 // nothing changed the second time
        Assert.Equal(new[] { true, false }, events);

        src.Profile = TestHw.Make(ramGb: 32);
        svc.Rescan();
        Assert.Equal(new[] { true, false, true }, events); // a rescan that finds different hardware says so
    }

    private sealed class FakeHardwareSource : IHardwareSource
    {
        public HardwareProfile Profile; public PowerInfo Power;
        public FakeHardwareSource(HardwareProfile p) { Profile = p; Power = p.Power; }
        public HardwareProfile Discover() => Profile with { Power = Power };
        public PowerInfo ReadPower() => Power;
    }
}

public class RecommendationEngineTests
{
    private static OptimizationPlan Plan(HardwareProfile hw, WindowStats? w = null, SystemSnapshot? s = null)
        => new RecommendationEngine().Evaluate(TestHw.Ctx(hw, w, s));

    [Fact]
    public void Evaluating_has_no_side_effects_and_is_repeatable()
    {
        var hw = TestHw.Make();
        var a = Plan(hw); var b = Plan(hw);
        Assert.Equal(a.Items.Select(i => (i.Id, i.Applicability)), b.Items.Select(i => (i.Id, i.Applicability)));
    }

    [Fact]
    public void Every_recommendation_explains_itself()
    {
        var plan = Plan(TestHw.Make());
        foreach (var i in plan.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Title), i.Id);
            Assert.False(string.IsNullOrWhiteSpace(i.Reason), i.Id);
            Assert.NotEmpty(i.Evidence);
            if (i.Applicability != Applicability.NotApplicable)
            {
                Assert.False(string.IsNullOrWhiteSpace(i.ExpectedBenefit), i.Id);
                Assert.False(string.IsNullOrWhiteSpace(i.RestoreMethod), i.Id);
                Assert.False(string.IsNullOrWhiteSpace(i.VerifyMethod), i.Id);
            }
        }
        Assert.Equal(plan.Items.Count, plan.Items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public void A_hybrid_laptop_on_ac_gets_the_graphics_preference_and_power_recommendations()
    {
        var plan = Plan(TestHw.Make());
        Assert.Equal(Applicability.Recommended, plan.Find("gpu")!.Applicability);
        Assert.Equal(Applicability.Recommended, plan.Find(ActionIds.Power)!.Applicability);
        Assert.Contains("gpu", plan.Select(BoosterProfile.Performance));
        Assert.Contains("gpu", plan.Select(BoosterProfile.Balanced));
    }

    [Fact]
    public void A_laptop_on_battery_is_not_offered_the_power_boost()
    {
        var plan = Plan(TestHw.Make(power: TestHw.Power(PowerSource.Battery)));
        var p = plan.Find(ActionIds.Power)!;
        Assert.Equal(Applicability.NotApplicable, p.Applicability);
        Assert.Contains("battery", p.Reason);
        Assert.DoesNotContain(ActionIds.Power, plan.Select(BoosterProfile.Performance));
    }

    [Fact]
    public void Integrated_only_graphics_never_gets_a_dedicated_gpu_recommendation()
    {
        var plan = Plan(TestHw.Make(gpus: new[] { TestHw.IntelIgpu(0) }));
        Assert.Equal(Applicability.NotApplicable, plan.Find("gpu")!.Applicability);
        Assert.DoesNotContain("gpu", plan.Select(BoosterProfile.Performance));
    }

    [Fact]
    public void A_desktop_with_one_dedicated_card_has_nothing_to_choose_between()
    {
        var plan = Plan(TestHw.Make(PlatformKind.Desktop, new[] { TestHw.Nvidia(0) }, power: TestHw.Power(highPlan: true)));
        Assert.Equal(Applicability.NotApplicable, plan.Find("gpu")!.Applicability);
        Assert.Contains("single graphics adapter", plan.Find("gpu")!.Reason);
        Assert.NotEqual(Applicability.NotApplicable, plan.Find(ActionIds.Power)!.Applicability);
    }

    [Fact]
    public void A_virtual_machine_gets_no_power_change()
    {
        var plan = Plan(TestHw.Make(PlatformKind.VirtualMachine, new[] { TestHw.IntelIgpu(0) }));
        Assert.Equal(Applicability.NotApplicable, plan.Find(ActionIds.Power)!.Applicability);
    }

    [Fact]
    public void A_pc_that_already_has_the_gpu_preference_set_is_not_asked_again()
    {
        var snap = TestHw.Snapshot(gpuPref: "GpuPreference=2;");
        Assert.Equal(Applicability.NotApplicable, Plan(TestHw.Make(), null, snap).Find("gpu")!.Applicability);
    }

    [Fact]
    public void Already_active_settings_are_not_applicable()
    {
        var hw = TestHw.Make(power: TestHw.Power(active: TestHw.HighPerf));
        var plan = Plan(hw, null, TestHw.Snapshot(dvr: false, gameMode: true));
        Assert.Equal(Applicability.NotApplicable, plan.Find(ActionIds.Power)!.Applicability);
        Assert.Equal(Applicability.NotApplicable, plan.Find("gamedvr")!.Applicability);
        Assert.Equal(Applicability.NotApplicable, plan.Find(ActionIds.GameMode)!.Applicability);
    }

    [Fact]
    public void Profiles_only_pick_recommended_items_and_never_optional_discouraged_or_unsupported_ones()
    {
        var plan = Plan(TestHw.Make());
        foreach (var profile in new[] { BoosterProfile.Balanced, BoosterProfile.Performance })
            foreach (var id in plan.Select(profile))
                Assert.Equal(Applicability.Recommended, plan.Find(id)!.Applicability);
        Assert.DoesNotContain(ActionIds.Timer, plan.Select(BoosterProfile.Performance));      // discouraged
        Assert.DoesNotContain(ActionIds.Awake, plan.Select(BoosterProfile.Performance));
        Assert.Empty(plan.Select(BoosterProfile.Custom));
    }

    [Fact]
    public void Balanced_is_a_subset_of_performance_for_this_hardware()
    {
        var plan = Plan(TestHw.Make());
        Assert.True(plan.Select(BoosterProfile.Balanced).IsSubsetOf(plan.Select(BoosterProfile.Performance)));
        Assert.True(plan.Select(BoosterProfile.Balanced).Count < plan.Select(BoosterProfile.Performance).Count);
    }

    [Fact]
    public void Sanitize_drops_ids_that_cannot_be_applied_here()
    {
        var plan = Plan(TestHw.Make(power: TestHw.Power(PowerSource.Battery)));
        var clean = plan.Sanitize(new[] { ActionIds.Power, "gpu", "made-up" });
        Assert.Equal(new[] { "gpu" }, clean.ToArray());
    }

    [Fact]
    public void Priority_calm_and_memory_need_evidence_before_they_are_recommended()
    {
        var hw = TestHw.Make();
        var quiet = TestHw.Stats(TestHw.Samples(hw, 20, (_, at) => TestHw.Sample(at, hw, cpu: 20, otherCpu: 0.5, availPct: 60)));
        var plan = Plan(hw, quiet);
        Assert.Equal(Applicability.Optional, plan.Find(ActionIds.Priority)!.Applicability);
        Assert.Equal(Applicability.Optional, plan.Find(ActionIds.Calm)!.Applicability);
        Assert.Equal(Applicability.Discouraged, plan.Find(ActionIds.Memory)!.Applicability);

        var busy = TestHw.Stats(TestHw.Samples(hw, 20, (_, at) => TestHw.Sample(at, hw, cpu: 92, otherCpu: 25, availPct: 8, faults: 400)));
        var plan2 = Plan(hw, busy);
        Assert.Equal(Applicability.Recommended, plan2.Find(ActionIds.Priority)!.Applicability);
        Assert.Equal(Applicability.Recommended, plan2.Find(ActionIds.Calm)!.Applicability);
        Assert.Equal(Applicability.Recommended, plan2.Find(ActionIds.Memory)!.Applicability);
    }

    [Fact]
    public void Without_any_measurements_the_recommendations_say_so_instead_of_pretending()
    {
        var plan = Plan(TestHw.Make());
        Assert.Contains(plan.Find(ActionIds.Priority)!.Evidence, e => e.Kind == EvidenceKind.Limitation);
        Assert.Contains(plan.Notes, n => n.Contains("cautious"));
    }

    [Fact]
    public void A_value_that_was_not_measured_is_never_shown_as_a_number()
    {
        var plan = Plan(TestHw.Make());
        Assert.Equal("not measured yet", plan.Find(ActionIds.Memory)!.CurrentValue);
        Assert.DoesNotContain("100%", plan.Find(ActionIds.Memory)!.CurrentValue);
        Assert.Contains(plan.Find(ActionIds.Memory)!.Evidence, e => e.Kind == EvidenceKind.Limitation);
    }

    [Fact]
    public void The_timer_tweak_is_discouraged_on_modern_windows_because_it_only_affects_nighty_itself()
    {
        var t = Plan(TestHw.Make(build: 26200)).Find(ActionIds.Timer)!;
        Assert.Equal(Applicability.Discouraged, t.Applicability);
        Assert.Contains("per process", string.Join(" ", t.Evidence.Select(e => e.Text)));
    }

    [Fact]
    public void Low_memory_and_integrated_only_systems_get_the_effects_tweaks_but_a_dedicated_gpu_pc_does_not()
    {
        var weak = Plan(TestHw.Make(gpus: new[] { TestHw.IntelIgpu(0) }, ramGb: 8));
        Assert.Equal(Applicability.Recommended, weak.Find("transparency")!.Applicability);
        Assert.Equal(Applicability.Recommended, weak.Find("visualfx")!.Applicability);
        var strong = Plan(TestHw.Make(PlatformKind.Desktop, new[] { TestHw.Nvidia(0) }, ramGb: 32));
        Assert.Equal(Applicability.Optional, strong.Find("transparency")!.Applicability);
        Assert.Equal(Applicability.Optional, strong.Find("visualfx")!.Applicability);
    }

    // ---- frame-rate cap ----
    [Fact]
    public void A_launcher_that_rewrites_flags_makes_the_fps_cap_tweak_discouraged_with_the_reason()
    {
        var cfg = TestHw.Config(cap: 1000, flag: null, managed: true, launcher: LauncherKind.Froststrap);
        var plan = Plan(TestHw.Make(), null, TestHw.Snapshot(cfg: cfg with { FramerateCapSetting = 60 }));
        var r = plan.Find("fpscap")!;
        Assert.Equal(Applicability.Discouraged, r.Applicability);
        Assert.Contains("Froststrap", r.Reason);
        Assert.DoesNotContain("fpscap", plan.Select(BoosterProfile.Performance));
    }

    [Fact]
    public void An_already_uncapped_roblox_does_not_need_the_fps_cap_tweak()
    {
        var plan = Plan(TestHw.Make(), null, TestHw.Snapshot(cfg: TestHw.Config(cap: 1000)));
        Assert.Equal(Applicability.NotApplicable, plan.Find("fpscap")!.Applicability);
    }

    [Fact]
    public void A_capped_official_install_is_recommended_the_fps_tweak()
    {
        var plan = Plan(TestHw.Make(), null, TestHw.Snapshot(cfg: TestHw.Config(cap: 60)));
        Assert.Equal(Applicability.Recommended, plan.Find("fpscap")!.Applicability);
    }
}
