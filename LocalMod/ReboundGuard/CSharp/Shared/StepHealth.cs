using System;
using System.Diagnostics;
using System.Globalization;

namespace ReboundGuard
{
    internal static class StepHealth
    {
        private static readonly double[] Bounds = { 0.25, 0.5, 1, 2, 3, 4, 5, 6, 8, 10, 12, 16.667, 20, 25, 33.334, 50, 100, 200, 500, 1000, double.PositiveInfinity };
        private static readonly int[] Histogram = new int[Bounds.Length];
        private static int count, overBudget, over50, gen0, gen1, gen2;
        private static long lastFinish, worstStart, worstStartGc, worstEndGc, allocatedStart;
        private static double worst, maxOutsideGap;
        internal static long CollectionTotal() => (long)GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        internal static void Observe(double milliseconds, long start, long finish, long startGc)
        {
            count++;
            for (int i = 0; i < Bounds.Length; i++) if (milliseconds <= Bounds[i]) { Histogram[i]++; break; }
            if (milliseconds > 1000.0 / 60) overBudget++;
            if (milliseconds > 50) over50++;
            if (lastFinish != 0) maxOutsideGap = Math.Max(maxOutsideGap, (start - lastFinish) * 1000.0 / Stopwatch.Frequency);
            lastFinish = finish;
            if (milliseconds > worst)
            {
                worst = milliseconds; worstStart = start; worstStartGc = startGc; worstEndGc = CollectionTotal();
                SubsystemProfiler.CaptureWorstStep();
            }
        }
        internal static double PercentileBound(double quantile)
        {
            if (count == 0) return 0;
            int target = (int)Math.Ceiling(count * quantile), seen = 0;
            for (int i = 0; i < Histogram.Length; i++) { seen += Histogram[i]; if (seen >= target) return Bounds[i]; }
            return double.PositiveInfinity;
        }
        internal static string Report(long now) => string.Format(CultureInfo.InvariantCulture,
            "step health: measured_steps={0}; p95_update_ms_le={1:F3}; p99_update_ms_le={2:F3}; steps_over_16_667ms={3}; steps_over_50ms={4}; max_outside_update_gap_ms={5:F3}; gc_gen0={6}; gc_gen1={7}; gc_gen2={8}; allocated_process_mb={9:F3}; worst_step_age_s={10:F3}; worst_step_gc_collection_count_delta={11}",
            count, PercentileBound(.95), PercentileBound(.99), overBudget, over50, maxOutsideGap,
            GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2,
            (GC.GetTotalAllocatedBytes(false) - allocatedStart) / 1048576.0,
            worstStart == 0 ? 0 : (now - worstStart) / (double)Stopwatch.Frequency, worstEndGc - worstStartGc);
        internal static void Reset(bool clearGap = true)
        {
            Array.Clear(Histogram); count = overBudget = over50 = 0; worst = maxOutsideGap = 0;
            worstStart = worstStartGc = worstEndGc = 0;
            if (clearGap) lastFinish = 0;
            gen0 = GC.CollectionCount(0); gen1 = GC.CollectionCount(1); gen2 = GC.CollectionCount(2);
            allocatedStart = GC.GetTotalAllocatedBytes(false);
        }
    }
}
