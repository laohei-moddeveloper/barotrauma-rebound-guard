using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ReboundGuard;

namespace ReboundGuardTests
{
    public static class FrameHealthTests
    {
        private static int checks;
        private static void Check(bool value, string message)
        {
            if (!value) throw new Exception("Frame health: " + message);
            checks++;
        }
        private static Dictionary<string, string> Fields(string report) => report.Split(':')[1].Split(';')
            .Select(part => part.Trim().Split('=')).Where(pair => pair.Length == 2).ToDictionary(pair => pair[0], pair => pair[1]);
        private static double Number(Dictionary<string, string> fields, string name) => double.Parse(fields[name], CultureInfo.InvariantCulture);
        public static object Run()
        {
            checks = 0;
            var health = new FrameHealth();
            health.ObserveSimulationStep(); // Not inside a root update.
            health.BeginUpdate(); health.ObserveSimulationStep(); health.EndUpdate(4);
            health.ObserveGameDraw(3); health.ObserveDraw(0, 5);
            for (int i = 1; i <= 98; i++) health.ObserveDraw(i * 10, 5);
            health.ObserveDraw(1080, 80); // Last interval is 100 ms.
            var first = Fields(health.Report());
            Check(Number(first, "draws") == 100 && Number(first, "draw_interval_samples") == 99, "first draw has no invented interval");
            Check(Number(first, "draw_interval_p95_ms_le") == 12 && Number(first, "draw_interval_p99_ms_le") == 100, "distribution captures isolated visible-frame stall");
            Check(Number(first, "draw_intervals_over_50ms") == 1 && Number(first, "draw_interval_max_ms") == 100, "long interval counted exactly");
            Check(Number(first, "max_simulation_steps_per_update") == 1 && Number(first, "updates_with_multiple_simulation_steps") == 0, "unscoped simulation is ignored");
            Check(Number(first, "update_call_max_ms") == 4 && Number(first, "draw_call_max_ms") == 80 && Number(first, "game_draw_call_max_ms") == 3, "root update, root draw and game rendering remain separate");
            Check(first["gpu_time_measured"] == "False" && first["call_times_include_waits"] == "True", "wall time never presented as GPU or CPU time");
            health.BeginUpdate(); health.ObserveSimulationStep();
            var middle = Fields(health.Report());
            Check(Number(middle, "draws") == 0 && Number(middle, "update_calls") == 0, "report drains completed samples");
            health.ObserveSimulationStep(); health.EndUpdate(30);
            health.ObserveDraw(1090, 1);
            var following = Fields(health.Report());
            Check(Number(following, "max_simulation_steps_per_update") == 2 && Number(following, "updates_with_multiple_simulation_steps") == 1, "report inside root update preserves step counting");
            Check(Number(following, "draw_interval_samples") == 1 && Number(following, "draw_interval_max_ms") == 10, "draw interval continuity crosses report boundary");
            health.Pause(); health.ObserveDraw(100000, 1);
            var resumed = Fields(health.Report());
            Check(Number(resumed, "draws") == 1 && Number(resumed, "draw_interval_samples") == 0, "menu/loading gap is not a gameplay frame");
            health.ObserveDraw(100010, 2000);
            Check(double.IsPositiveInfinity(Number(Fields(health.Report()), "draw_call_p95_ms_le")), "histogram reports unbounded upper bucket honestly");
            return new { checks, note = "Actual FrameHealth accumulator: synthetic draw intervals, separate call durations, catch-up steps, report/transition boundaries. No renderer, driver, VSync or game-loop hook execution." };
        }
    }
}
