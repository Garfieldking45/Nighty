using Nighty.Services;
using Nighty.Services.Booster;
using Xunit;

namespace Nighty.Tests;

public class BoostSetTests
{
    private static OptimizationPlan Plan() => new RecommendationEngine().Evaluate(TestHw.Ctx(TestHw.Make(), null, null));

    [Fact]
    public void Every_boost_item_is_something_the_rule_set_knows_about()
    {
        var plan = Plan();
        foreach (var id in BoostService.CatalogIds) Assert.NotNull(plan.Find(id));
    }

    [Fact]
    public void The_boost_never_includes_actions_that_slow_other_programs_or_do_nothing_for_roblox()
    {
        var ids = BoostService.CatalogIds.Concat(BoostService.SessionIds).Concat(BoostService.TweakIds).ToHashSet();
        foreach (var banned in new[] { ActionIds.Calm, ActionIds.Memory, ActionIds.Timer, ActionIds.Awake, "transparency", "visualfx", "animations", "bgapps", "toasts" })
            Assert.DoesNotContain(banned, ids);
    }

    [Fact]
    public void Settings_that_must_exist_before_roblox_starts_are_separate_from_game_mode_actions()
    {
        Assert.Equal(new[] { "gpu", "fpscap", "gamedvr" }, BoostService.TweakIds);
        Assert.All(BoostService.SessionIds, id => Assert.True(ActionIds.IsSession(id)));
        Assert.Empty(BoostService.TweakIds.Intersect(BoostService.SessionIds));
        Assert.Equal(BoostService.CatalogIds.Count, BoostService.TweakIds.Count + BoostService.SessionIds.Count);
    }

    [Fact]
    public void Choosing_the_boost_switches_off_every_other_game_mode_action()
    {
        var g = new Nighty.Models.GameSettings { CalmBackgroundApps = true, FreeMemory = true, TimerResolution = true, KeepDisplayAwake = true };
        GameChoices.Set(g, BoostService.SessionIds);
        Assert.False(g.CalmBackgroundApps); Assert.False(g.FreeMemory); Assert.False(g.TimerResolution); Assert.False(g.KeepDisplayAwake);
        Assert.True(g.HighPerformancePower); Assert.True(g.RobloxHighPriority); Assert.True(g.RobloxNoThrottle); Assert.True(g.WindowsGameMode);
    }

    [Fact]
    public void A_fresh_install_has_the_boost_off_and_the_unhelpful_actions_off()
    {
        var g = new Nighty.Models.GameSettings();
        Assert.False(g.AutoGameMode);
        Assert.False(g.CalmBackgroundApps); Assert.False(g.FreeMemory); Assert.False(g.TimerResolution);
        Assert.DoesNotContain("shader", g.CleanerCategories);
    }
}
