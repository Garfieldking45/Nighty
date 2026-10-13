namespace Nighty.Services;

/// <summary>
/// HitFix's self-tuning spin window. The clicker sleeps on a timer until <see cref="MarginMs"/> before a deadline and
/// busy-waits the rest. Every timer wake is measured (how much later than asked it came back), and the margin follows
/// those measurements: it starts generous, shrinks toward what this PC's timer really overshoots by, and grows again
/// whenever wakes land late. Pure arithmetic with no clock of its own, so it can be tested.
/// </summary>
internal sealed class SpinTuner
{
    public const double MinMs = 0.15, MaxMs = 3.0, SampleCapMs = 8.0, HeadroomMs = 0.22, LegacyFloorMs = 1.1;
    private const int RingSize = 128, RecomputeEvery = 16;

    private readonly bool _highResTimer;
    private readonly float[] _ring = new float[RingSize];
    private readonly float[] _scratch = new float[RingSize];
    private long _count;
    private double _mean = 0.5, _dev = 0.25, _p99 = 1.5;   // priors until the first 16 wakes have been seen
    private double _missRate;
    private double _scale = 1.0;

    /// <param name="highResTimer">False when only the 15.6 ms legacy timer exists: the margin then never drops below <see cref="LegacyFloorMs"/>.</param>
    public SpinTuner(bool highResTimer)
    {
        _highResTimer = highResTimer;
        Recompute();
    }

    /// <summary>How long before the deadline the timer hands over to the spin loop.</summary>
    public double MarginMs { get; private set; }

    /// <summary>Margin multiplier learned from how often wakes landed late (0.7 to 2).</summary>
    public double Scale => _scale;

    public long Samples => _count;

    /// <summary>Feed one timer wake: how many ms later than requested it returned.</summary>
    public void Observe(double overshootMs)
    {
        double s = double.IsNaN(overshootMs) ? 0 : Math.Clamp(overshootMs, 0, SampleCapMs);
        double d = s - _mean;
        _mean += d * 0.05;
        _dev += (Math.Abs(d) - _dev) * 0.05;
        // A "miss" is a wake that came back at least a whole margin late, i.e. past the deadline it was meant to leave time for.
        _missRate += ((s >= MarginMs ? 1.0 : 0.0) - _missRate) * 0.1;

        _ring[_count % RingSize] = (float)s;
        _count++;
        if (_count >= RecomputeEvery && _count % RecomputeEvery == 0)
        {
            int n = (int)Math.Min(_count, RingSize);
            Array.Copy(_ring, _scratch, n);
            Array.Sort(_scratch, 0, n);
            _p99 = _scratch[Math.Min(n - 1, n * 99 / 100)];
            if (_missRate > 0.01) _scale *= 1.15;
            else if (_missRate < 0.001) _scale *= 0.97;
            _scale = Math.Clamp(_scale, 0.7, 2.0);
        }
        Recompute();
    }

    private void Recompute()
    {
        double m = Math.Max(_mean + 3 * _dev, _p99 + HeadroomMs) * _scale;
        if (!_highResTimer) m = Math.Max(m, LegacyFloorMs);
        MarginMs = Math.Clamp(m, MinMs, MaxMs);
    }
}
