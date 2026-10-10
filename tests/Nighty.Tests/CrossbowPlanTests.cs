using Nighty.Models;
using Nighty.Services;
using Xunit;

namespace Nighty.Tests;

public class CrossbowPlanTests
{
    private static SlotMacroConfig Crossbow() => SlotMacroConfig.Create(SlotMacroKind.Crossbow);

    [Fact]
    public void Defaults_release_the_weapon_key_before_the_shot_and_swap_after_it()
    {
        var p = SlotMacroService.PlanShot(Crossbow());
        Assert.True(p.KeyUp <= p.ShotDown, "the weapon key must be released before the shot");
        Assert.True(p.ShotDown < p.ShotUp);
        Assert.True(p.ShotUp <= p.SwordKey, "the sword must not be selected before the shot is released");
    }

    [Fact]
    public void Default_shot_is_held_for_at_least_two_frames_at_60fps_and_gets_a_longer_equip_wait_than_before()
    {
        var p = SlotMacroService.PlanShot(Crossbow());
        Assert.True(p.ShotUp - p.ShotDown >= 2 * 1000.0 / 60, $"hold {p.ShotUp - p.ShotDown} ms is under two frames");
        Assert.True(p.ShotDown >= 30, $"equip wait {p.ShotDown} ms is not longer than the old 25 ms");
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, -5)]
    [InlineData(500, 500, 500)]
    public void Any_setting_still_gives_an_ordered_plan(double equip, double hold, double swap)
    {
        var c = Crossbow(); c.EquipDelayMs = equip; c.ShotHoldMs = hold; c.SwapDelayMs = swap;
        var p = SlotMacroService.PlanShot(c);
        Assert.True(p.KeyUp < p.ShotDown && p.ShotDown < p.ShotUp && p.ShotUp <= p.SwordKey, p.ToString());
        Assert.True(p.ShotDown - p.KeyUp >= 2, "the key release and the shot must not touch");
    }

    [Fact]
    public void Settings_are_clamped_to_their_ranges()
    {
        var c = Crossbow(); c.EquipDelayMs = 9999; c.ShotHoldMs = -3; c.SwapDelayMs = 9999;
        Assert.Equal(200, c.EquipDelayMs);
        Assert.Equal(10, c.ShotHoldMs);
        Assert.Equal(150, c.SwapDelayMs);
    }
}
