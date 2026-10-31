using Nighty.Services;
using Xunit;

namespace Nighty.Tests;

/// <summary>Read-only checks: nothing here changes the display.</summary>
public class ResolutionTests
{
    [Theory]
    [InlineData(1024, 768, "4:3")]
    [InlineData(1280, 1024, "5:4")]
    [InlineData(1920, 1080, "16:9")]
    [InlineData(1920, 1200, "16:10")]
    public void Ratio_is_named(int w, int h, string ratio) => Assert.Equal(ratio, new DisplayMode(w, h, 60).Ratio);

    [Fact]
    public void Reads_the_current_mode_and_a_mode_list_that_contains_it()
    {
        var s = new ResolutionService();
        var cur = s.Current();
        Assert.NotNull(cur);                       // would be null if the DEVMODE layout were wrong
        Assert.True(cur!.Width > 0 && cur.Height > 0 && cur.Hz > 0);
        var modes = s.Modes();
        Assert.Contains(modes, m => m.Width == cur.Width && m.Height == cur.Height);
        Assert.False(s.IsChanged);
    }
}
