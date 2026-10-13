using System.Runtime.InteropServices;

namespace Nighty.Native;

/// <summary>
/// Which logical processor the HitFix thread should live on. On hybrid CPUs (Intel 12th gen and newer) the efficiency cores
/// are numbered last, so "the highest-numbered core" would land on the slow ones; Windows' CPU Sets say which are fast.
/// </summary>
internal static class CpuTopology
{
    /// <param name="Index">Logical processor number inside its processor group.</param>
    /// <param name="Core">Physical core number; hyper-thread siblings share it.</param>
    /// <param name="Efficiency">EfficiencyClass: higher is faster (performance cores on a hybrid CPU).</param>
    internal readonly record struct CpuSet(ushort Group, byte Index, byte Core, byte Efficiency, bool Parked, bool Allocated);

    /// <summary>
    /// The fastest usable processor: not parked, not reserved by another process, never processor 0 of group 0 (it takes the
    /// interrupts). Highest efficiency class wins, then the last physical core, then the first thread on it. Null when nothing qualifies.
    /// </summary>
    internal static (ushort Group, byte Number)? PickFastCore(IReadOnlyList<CpuSet> sets)
    {
        var usable = sets.Where(c => !c.Parked && !c.Allocated && !(c.Group == 0 && c.Index == 0)).ToList();
        if (usable.Count == 0) return null;
        byte best = usable.Max(c => c.Efficiency);
        var pick = usable.Where(c => c.Efficiency == best)
            .OrderByDescending(c => c.Group).ThenByDescending(c => c.Core).ThenBy(c => c.Index).First();
        return (pick.Group, pick.Index);
    }

    /// <summary>Reads the CPU Sets this process may use. Empty if the query fails (very old Windows).</summary>
    internal static List<CpuSet> Read()
    {
        var result = new List<CpuSet>();
        IntPtr buf = IntPtr.Zero;
        try
        {
            var me = NativeMethods.GetCurrentProcess();
            NativeMethods.GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint len, me, 0);
            if (len == 0 || len > 1 << 20) return result;
            buf = Marshal.AllocHGlobal((int)len);
            if (!NativeMethods.GetSystemCpuSetInformation(buf, len, out len, me, 0)) return result;
            // SYSTEM_CPU_SET_INFORMATION: Size @0, Type @4 (0 = CpuSet), then Id @8, Group @0xC, LogicalProcessorIndex @0xE,
            // CoreIndex @0xF, EfficiencyClass @0x12, flags @0x13 (bit 0 parked, bit 1 allocated).
            for (int off = 0; off + 0x14 <= len;)
            {
                int size = Marshal.ReadInt32(buf, off);
                if (size <= 0) break;
                if (Marshal.ReadInt32(buf, off + 4) == 0)
                {
                    byte flags = Marshal.ReadByte(buf, off + 0x13);
                    result.Add(new CpuSet((ushort)Marshal.ReadInt16(buf, off + 0xC), Marshal.ReadByte(buf, off + 0xE), Marshal.ReadByte(buf, off + 0xF),
                        Marshal.ReadByte(buf, off + 0x12), (flags & 1) != 0, (flags & 2) != 0));
                }
                off += size;
            }
        }
        catch { result.Clear(); }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        return result;
    }
}
