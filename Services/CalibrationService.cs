using Nighty.Native;

namespace Nighty.Services;

public sealed record CalibrationResult(double BestCps, string Summary);

/// <summary>
/// Measures how steadily this PC can really deliver clicks. For each candidate speed it runs the same timing loop as the
/// clicker (including real SendInput calls, but zero-distance mouse moves so nothing is clicked) and records how many
/// clicks arrived late. The highest speed that stays steady wins; going past it makes uneven gaps, which is what shows
/// up in the game as dropped or doubled hits.
/// </summary>
public static class CalibrationService
{
    private static readonly double[] Candidates = { 20, 24, 28, 32, 35, 38, 41, 44, 47, 50 };
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
            double best = Candidates[0];
            bool anyFailed = false;
            foreach (var cps in Candidates)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Testing {cps:0} CPS…");
                if (Steady(cps, ct)) { if (!anyFailed) best = cps; }
                else { anyFailed = true; break; }
            }
            // Keep a safety margin so ordinary system noise doesn't push you over the edge.
            double chosen = Math.Max(10, Math.Floor(best * 0.95));
            return new CalibrationResult(chosen, $"Steady up to {best:0} CPS on this PC; set to {chosen:0} CPS.");
        }
        finally
        {
            Thread.CurrentThread.Priority = prev;
            if (mmcss != IntPtr.Zero) NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
            NativeMethods.timeEndPeriod(1);
        }
    }

    private static bool Steady(double cps, CancellationToken ct)
    {
        double periodMs = 1000.0 / cps;
        long period = Wait.FromMs(periodMs);
        long lateLimit = Wait.FromMs(periodMs * 0.25);
        int total = (int)(cps * SecondsPerStep), late = 0, sent = 0;
        long next = Wait.Now + Wait.FromMs(50);
        long start = next;
        for (int i = 0; i < total; i++)
        {
            Wait.Until(next, ct);
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            long actual = Wait.Now;
            InputSender.Noop(); InputSender.Noop();   // a click is a down + an up
            if (actual - next > lateLimit) late++;
            sent++;
            next += period;
            if (Wait.Now > next + period * 3) next = Wait.Now;   // stall: resync like the clicker does
        }
        double elapsed = (Wait.Now - start) / (double)Wait.FromMs(1000);
        double achieved = sent / Math.Max(0.001, elapsed);
        return late <= Math.Max(1, total * 0.02) && achieved >= cps * 0.97;
    }
}
