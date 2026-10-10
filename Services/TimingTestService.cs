using System.Diagnostics;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

public sealed record TimingReport(double RequestedCps, double MeasuredCps, double AvgIntervalMs, double JitterMs, double AvgErrorMs,
    double P99ErrorMs, double MinMs, double MaxMs, double CpuPercent, string Summary);

/// <summary>
/// Checks how exactly this PC delivers clicks. Runs the clicker's own timing loop (same waits, same thread boost, same
/// SendInput calls) for a few seconds, but with zero-distance mouse moves, so nothing is ever clicked.
/// </summary>
public static class TimingTestService
{
    public static Task<TimingReport> RunAsync(double cps, PrecisionMode mode, bool hitFix, double seconds, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<TimingReport>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(Run(cps, mode, hitFix, seconds, ct)); }
            catch (OperationCanceledException) { tcs.SetCanceled(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "Nighty timing test", Priority = ThreadPriority.Highest };
        t.Start();
        return tcs.Task;
    }

    private static TimingReport Run(double cps, PrecisionMode mode, bool hitFix, double seconds, CancellationToken ct)
    {
        cps = Math.Clamp(cps, 1, 100);
        double periodMs = 1000.0 / cps;
        long period = Wait.FromMs(periodMs);
        int total = Math.Max(10, (int)(cps * seconds));
        double spin = ClickerService.SpinFor(mode, hitFix);
        var stamps = new long[total];

        NativeMethods.timeBeginPeriod(1);
        using var boost = ThreadBoost.Apply(hitFix);
        using var proc = Process.GetCurrentProcess();
        var cpuStart = proc.TotalProcessorTime;
        long wallStart = Wait.Now;
        try
        {
            long next = Wait.Now + Wait.FromMs(60);
            double holdMs = Math.Min(periodMs * 0.5, 20);
            for (int i = 0; i < total; i++)
            {
                Wait.Until(next, ct, spin);
                ct.ThrowIfCancellationRequested();
                stamps[i] = Wait.Now;
                InputSender.Noop();
                Wait.Ms(holdMs, ct);
                InputSender.Noop();
                next += period;
                if (Wait.Now > next + period * 3) next = Wait.Now;   // stall: resync like the clicker does
            }
        }
        finally { NativeMethods.timeEndPeriod(1); }

        proc.Refresh();
        double wallMs = Wait.ToMs(Wait.Now - wallStart);
        double cpuMs = (proc.TotalProcessorTime - cpuStart).TotalMilliseconds;

        var intervals = new double[total - 1];
        var errors = new double[total - 1];
        for (int i = 1; i < total; i++)
        {
            intervals[i - 1] = Wait.ToMs(stamps[i] - stamps[i - 1]);
            errors[i - 1] = Math.Abs(intervals[i - 1] - periodMs);
        }
        double avg = intervals.Average();
        double jitter = Math.Sqrt(intervals.Sum(v => (v - avg) * (v - avg)) / intervals.Length);
        Array.Sort(errors);
        double p99 = errors[Math.Min(errors.Length - 1, (int)Math.Ceiling(errors.Length * 0.99) - 1)];
        double measured = (total - 1) / (Wait.ToMs(stamps[^1] - stamps[0]) / 1000.0);
        double avgErr = errors.Average();
        var report = new TimingReport(cps, measured, avg, jitter, avgErr, p99, intervals.Min(), intervals.Max(), wallMs > 0 ? cpuMs / wallMs * 100 : 0,
            $"Measured {measured:0.000} CPS, {avgErr:0.000} ms average error");
        Log.Info($"Timing test: {report.Summary}, jitter {jitter:0.000} ms, p99 {p99:0.000} ms (mode {mode}, hitfix {hitFix})");
        return report;
    }
}
