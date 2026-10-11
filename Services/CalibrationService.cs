using Nighty.Native;

namespace Nighty.Services;

public sealed record CalibrationResult(double BestCps, string Summary, int Duty = 50);

/// <summary>
/// Measures how steadily this PC can really deliver clicks. For each candidate speed it runs the same timing loop as the
/// clicker (including real SendInput calls, but zero-distance mouse moves so nothing is clicked) and records how many
/// clicks arrived late. The highest speed that stays steady wins; going past it makes uneven gaps, which is what shows
/// up in the game as dropped or doubled hits.
/// </summary>
public static class CalibrationService
{
    /// <summary>The rate calibration aims for: 34-35 hits per second. The search only goes lower when this PC cannot hold it steadily.</summary>
    public const double TargetCps = 35;
    private static readonly double[] TargetSteps = { 35, 34, 33, 32, 30, 28 };
    private static readonly int[] DutyCandidates = { 30, 35, 40, 45, 50, 55, 60, 65, 70 };
    private static readonly double[] Candidates = { 20, 28, 35, 44, 50, 60, 70, 80, 90, 100 };
    private const double SecondsPerStep = 1.2;

    public static CalibrationResult Run(IProgress<string>? progress, CancellationToken ct)
    {
        NativeMethods.timeBeginPeriod(1);
        uint idx = 0;
        IntPtr mmcss = NativeMethods.AvSetMmThreadCharacteristicsW("Games", ref idx);
        var prev = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        try
        {
            // 1. Aim for 34-35 CPS: the highest of these the PC can deliver evenly.
            double chosen = 0;
            foreach (var cps in TargetSteps)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Testing {cps:0} CPS…");
                if (Steady(cps, 0.5, ct, out _)) { chosen = cps; break; }
            }
            if (chosen == 0)
            {
                // Could not hold 28 CPS: fall back to finding the best steady speed below that.
                double best = Candidates[0];
                foreach (var cps in Candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Testing {cps:0} CPS…");
                    if (Steady(cps, 0.5, ct, out _)) best = cps; else break;
                }
                chosen = Math.Max(10, Math.Floor(best * 0.95));
            }

            // 2. Pick the click duty cycle that this PC releases most accurately at that speed.
            int duty = 50;
            double bestErr = double.MaxValue;
            foreach (int d in DutyCandidates)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Testing {d}% duty cycle at {chosen:0} CPS…");
                Steady(chosen, d / 100.0, ct, out double err);
                // Errors within 0.05 ms are noise; on a tie keep the duty closest to 50% (a clear press and a clear gap).
                double score = Math.Round(err / 0.05) * 0.05 + Math.Abs(d - 50) * 0.0001;
                if (score < bestErr) { bestErr = score; duty = d; }
            }
            double holdMs = 1000.0 / chosen * duty / 100.0;
            string target = chosen >= 34 ? "on target" : $"below the 34-35 target (this PC is steady up to about {chosen:0})";
            return new CalibrationResult(chosen, $"Set to {chosen:0} CPS ({target}) with a {duty}% duty cycle: {holdMs:0.0} ms held, {1000.0 / chosen - holdMs:0.0} ms between.", duty);
        }
        finally
        {
            Thread.CurrentThread.Priority = prev;
            if (mmcss != IntPtr.Zero) NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
            NativeMethods.timeEndPeriod(1);
        }
    }

    /// <param name="duty">Fraction of each period the button is held (0.5 = half).</param>
    /// <param name="holdErrMs">Average error between the intended and the measured hold time.</param>
    private static bool Steady(double cps, double duty, CancellationToken ct, out double holdErrMs)
    {
        double periodMs = 1000.0 / cps;
        long period = Wait.FromMs(periodMs);
        long lateLimit = Wait.FromMs(periodMs * 0.25);
        double holdMs = periodMs * duty;
        double errSum = 0;
        int total = (int)(cps * SecondsPerStep), late = 0, sent = 0;
        long next = Wait.Now + Wait.FromMs(50);
        long start = next;
        for (int i = 0; i < total; i++)
        {
            Wait.Until(next, ct);
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            long actual = Wait.Now;
            InputSender.Noop();   // the press
            long pressed = Wait.Now;
            Wait.Until(pressed + Wait.FromMs(holdMs), ct);
            InputSender.Noop();   // the release
            errSum += Math.Abs(Wait.ToMs(Wait.Now - pressed) - holdMs);
            if (actual - next > lateLimit) late++;
            sent++;
            next += period;
            if (Wait.Now > next + period * 3) next = Wait.Now;   // stall: resync like the clicker does
        }
        holdErrMs = sent > 0 ? errSum / sent : 0;
        double elapsed = (Wait.Now - start) / (double)Wait.FromMs(1000);
        double achieved = sent / Math.Max(0.001, elapsed);
        return late <= Math.Max(1, total * 0.02) && achieved >= cps * 0.97;
    }
}
