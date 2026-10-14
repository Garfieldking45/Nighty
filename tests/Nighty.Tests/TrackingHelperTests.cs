using Nighty.Services;
using Xunit;

namespace Nighty.Tests;

/// <summary>Tracking Helper, the way Lyre does it: your values only while Roblox is in front, Windows' own everywhere else.</summary>
public class TrackingHelperTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]    // Roblox lost focus: Windows' own values come back
    [InlineData(false, true, false)]    // option off: never switched automatically
    [InlineData(false, false, false)]
    public void Values_are_in_force_only_while_Roblox_is_in_front_and_the_option_is_on(bool onlyInRoblox, bool robloxFront, bool expected) =>
        Assert.Equal(expected, PointerService.WantApplied(onlyInRoblox, robloxFront));
}

public class SlotOneSlowdownTests
{
    [Theory]
    [InlineData(true, true, true, 1, true)]     // Roblox in front, clicking, slot 1: slow
    [InlineData(true, true, true, 2, false)]    // any other slot: normal
    [InlineData(true, true, true, 9, false)]
    [InlineData(true, true, true, 0, false)]    // slot not known yet is not slot 1
    [InlineData(true, true, false, 1, false)]   // not clicking: normal
    [InlineData(true, false, true, 1, false)]   // Roblox not in front: never touch the pointer
    [InlineData(false, true, true, 1, false)]   // option off
    public void Slows_only_when_in_slot_one_and_clicking_in_Roblox(bool enabled, bool roblox, bool clicking, int slot, bool expected) =>
        Assert.Equal(expected, PointerService.WantSlow(enabled, roblox, clicking, slot));
}
