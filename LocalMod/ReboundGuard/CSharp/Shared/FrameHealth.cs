using System;
using System.Globalization;

namespace ReboundGuard
{
    // Wall-clock call/interval measurements, not CPU time or GPU timestamps.
    // This has no game dependencies so the actual accumulator can be tested.
    public sealed class FrameHealth
    {
        private sealed class Distribution
        {
            private static readonly double[] Bounds = { .25, .5, 1, 2, 4, 8, 12, 16.667, 20, 25, 33.334, 50, 100, 200, 500, 1000, double.PositiveInfinity };
            private readonly int[] buckets = new int[Bounds.Length];
            internal int Count, Over50;
            internal double Maximum;
            internal void Observe(double milliseconds)
            {
                if (!double.IsFinite(milliseconds) || milliseconds < 0) return;
                Count++; if (milliseconds > 50) Over50++;
                Maximum = Math.Max(Maximum, milliseconds);
                for (int i = 0; i < Bounds.Length; i++)
                    if (milliseconds <= Bounds[i]) { buckets[i]++; break; }
            }
            internal double Bound(double quantile)
            {
                if (Count == 0) return 0;
                int target = (int)Math.Ceiling(Count * quantile), seen = 0;
                for (int i = 0; i < Bounds.Length; i++) { seen += buckets[i]; if (seen >= target) return Bounds[i]; }
                return double.PositiveInfinity;
            }
            internal void Reset() { Array.Clear(buckets); Count = Over50 = 0; Maximum = 0; }
        }
        private readonly Distribution intervals = new Distribution(), updates = new Distribution(), draws = new Distribution(), rendering = new Distribution();
        private double lastDrawStart = double.NaN;
        private int updateSteps, maxUpdateSteps, multiStepUpdates;
        private bool inUpdate;

        public void BeginUpdate() { updateSteps = 0; inUpdate = true; }
        public void ObserveSimulationStep() { if (inUpdate) updateSteps++; }
        public void EndUpdate(double milliseconds)
        {
            updates.Observe(milliseconds);
            maxUpdateSteps = Math.Max(maxUpdateSteps, updateSteps);
            if (updateSteps > 1) multiStepUpdates++;
            inUpdate = false;
        }
        public void ObserveDraw(double startMilliseconds, double durationMilliseconds)
        {
            if (!double.IsNaN(lastDrawStart)) intervals.Observe(startMilliseconds - lastDrawStart);
            lastDrawStart = startMilliseconds;
            draws.Observe(durationMilliseconds);
        }
        public void ObserveGameDraw(double milliseconds) { rendering.Observe(milliseconds); }
        // Menus/background/loading transitions break interval continuity. They
        // must not become a giant gameplay frame on return to the game screen.
        public void Pause() { lastDrawStart = double.NaN; inUpdate = false; }
        public string Report()
        {
            string result = string.Format(CultureInfo.InvariantCulture,
                "client frame: draws={0}; update_calls={1}; draw_interval_samples={2}; draw_interval_p95_ms_le={3:F3}; draw_interval_p99_ms_le={4:F3}; draw_interval_max_ms={5:F3}; draw_intervals_over_50ms={6}; " +
                "update_call_p95_ms_le={7:F3}; update_call_max_ms={8:F3}; draw_call_p95_ms_le={9:F3}; draw_call_max_ms={10:F3}; game_draw_call_p95_ms_le={11:F3}; game_draw_call_max_ms={12:F3}; " +
                "max_simulation_steps_per_update={13}; updates_with_multiple_simulation_steps={14}; game_draw_calls={15}; gpu_time_measured=False; call_times_include_waits=True",
                draws.Count, updates.Count, intervals.Count, intervals.Bound(.95), intervals.Bound(.99), intervals.Maximum, intervals.Over50,
                updates.Bound(.95), updates.Maximum, draws.Bound(.95), draws.Maximum, rendering.Bound(.95), rendering.Maximum,
                maxUpdateSteps, multiStepUpdates, rendering.Count);
            intervals.Reset(); updates.Reset(); draws.Reset(); rendering.Reset();
            maxUpdateSteps = multiStepUpdates = 0;
            // Preserve the last draw and an in-progress update across report
            // windows: the simulation reporter runs inside GameMain.Update.
            return result;
        }
    }
}
