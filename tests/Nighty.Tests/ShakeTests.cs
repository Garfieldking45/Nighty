using Nighty.Services;
using Xunit;

namespace Nighty.Tests;

/// <summary>The shake motion is pure maths, so it is checked without a game or a mouse.</summary>
public class ShakeTests
{
    [Fact]
    public void Strength_and_speed_map_to_the_ranges_Lyre_uses()
    {
        Assert.InRange(ShakePath.AmplitudeFor(1), 0.40, 0.42);
        Assert.InRange(ShakePath.AmplitudeFor(100), 15.9, 16.1);
        Assert.InRange(ShakePath.PeriodFor(1), 259, 261);      // slow sway
        Assert.InRange(ShakePath.PeriodFor(100), 17.9, 18.1);  // fast buzz
        Assert.True(ShakePath.PeriodFor(40) > ShakePath.PeriodFor(41));   // a higher speed is always quicker
        Assert.Equal(0.0, ShakePath.SmoothnessFor(0));
        Assert.Equal(1.0, ShakePath.SmoothnessFor(100));
    }

    [Fact]
    public void The_path_starts_at_the_origin()
    {
        var p = new ShakePath(1, 8, 90, 0.6);
        var s = p.Sample(1000);
        Assert.Equal(0, s.X, 6); Assert.Equal(0, s.Y, 6);
    }

    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(40, 0.6)]
    [InlineData(100, 1.0)]
    public void The_path_never_leaves_its_reach(int strength, double smooth)
    {
        double amp = ShakePath.AmplitudeFor(strength);
        var p = new ShakePath(42, amp, 40, smooth);
        for (double t = 0; t < 20000; t += 3)
        {
            var (x, y) = p.Sample(t);
            Assert.True(Math.Sqrt(x * x + y * y) <= amp * 1.25 + 1e-6, $"t={t} left the reach");
        }
    }

    [Fact]
    public void The_same_seed_gives_the_same_path()
    {
        var a = new ShakePath(7, 6, 60, 0.5); var b = new ShakePath(7, 6, 60, 0.5);
        for (double t = 0; t < 2000; t += 5) Assert.Equal(a.Sample(t), b.Sample(t));
    }

    /// <summary>Total distance travelled between samples: a sharp jitter rests then jumps, so its largest single step is bigger.</summary>
    private static double LargestStep(double smooth)
    {
        var p = new ShakePath(99, 12, 120, smooth);
        double max = 0; (double X, double Y) prev = p.Sample(0);
        for (double t = 1; t < 30000; t += 1)
        {
            var s = p.Sample(t);
            max = Math.Max(max, Math.Sqrt((s.X - prev.X) * (s.X - prev.X) + (s.Y - prev.Y) * (s.Y - prev.Y)));
            prev = s;
        }
        return max;
    }

    [Fact]
    public void Low_smoothness_jumps_and_high_smoothness_glides()
    {
        Assert.True(LargestStep(0.0) > LargestStep(1.0) * 1.2, "a sharp jitter should have bigger single steps than a glide");
    }

    [Fact]
    public void A_long_pause_does_not_race_through_old_points()
    {
        var p = new ShakePath(5, 8, 50, 0.6);
        p.Sample(0); p.Sample(100);
        var s = p.Sample(5_000_000);   // an hour-long gap
        Assert.True(Math.Sqrt(s.X * s.X + s.Y * s.Y) <= 8 * 1.25 + 1e-6);
    }
}
