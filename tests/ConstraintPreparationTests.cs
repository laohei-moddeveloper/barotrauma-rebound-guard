using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using FarseerPhysics.Dynamics.Contacts;
using HarmonyLib;
using ReboundGuard;
using Config = ReboundGuard.Settings;

namespace ReboundGuardTests
{
    public static class ConstraintPreparationTests
    {
        private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance);
        private static int checks;
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; }
        private static object Copy(object value) => Clone.Invoke(value, null);
        private static ContactSolver Make(int count)
        {
            var source = (ContactSolver)AccessTools.Method(typeof(ConstraintSolverTests), "Template").Invoke(null, null);
            var result = (ContactSolver)Copy(source);
            result._count = count;
            result._velocityConstraints = new ContactVelocityConstraint[count];
            result._positionConstraints = new ContactPositionConstraint[count];
            result._positions = source._positions.ToArray(); result._velocities = source._velocities.ToArray();
            for (int i = 0; i < count; i++)
            {
                var vc = (ContactVelocityConstraint)Copy(source._velocityConstraints[0]);
                vc.points = source._velocityConstraints[0].points.Select(p => (VelocityConstraintPoint)Copy(p)).ToArray();
                vc.contactIndex = 0;
                result._velocityConstraints[i] = vc;
                result._positionConstraints[i] = (ContactPositionConstraint)Copy(source._positionConstraints[0]);
            }
            return result;
        }
        private static int[] Bits(object value)
        {
            var values = new List<int>(); Add(value, values); return values.ToArray();
        }
        private static void Add(object value, List<int> values)
        {
            if (value == null) { values.Add(int.MinValue); return; }
            if (value is float f) { values.Add(BitConverter.SingleToInt32Bits(f)); return; }
            if (value is int i) { values.Add(i); return; }
            if (value is bool b) { values.Add(b ? 1 : 0); return; }
            if (value.GetType().IsEnum) { values.Add(Convert.ToInt32(value)); return; }
            if (value is Array array) { foreach (object item in array) Add(item, values); return; }
            foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) Add(field.GetValue(value), values);
        }
        public static object Run()
        {
            Check((bool)AccessTools.Method(typeof(ParallelConstraintPreparation), "SupportedCodeMatches").Invoke(null, null), "native initialization fingerprint");
            AccessTools.Method(typeof(ParallelConstraintPreparation), "Bind").Invoke(null, null);
            var range = (Action<ContactSolver, int, int>)AccessTools.Field(typeof(ParallelConstraintPreparation), "runRange").GetValue(null);
            bool enabled = Config.ParallelConstraintPreparation;
            int threshold = Config.MinimumContactsForParallelPreparation;
            var foreign = new Harmony("reboundguard.preparation.foreign.test");
            try
            {
                Config.ParallelConstraintPreparation = true; Config.MinimumContactsForParallelPreparation = 1;
                foreach (int count in new[] { 0, 1, 2, 17, 257, 2048 })
                {
                    var serial = Make(count); var copied = Make(count); var parallel = Make(count);
                    serial.InitializeVelocityConstraints(); range(copied, 0, count);
                    Check(Bits(serial._velocityConstraints).SequenceEqual(Bits(copied._velocityConstraints)), "native IL range preserves all constraint fields: " + count);
                    if (count > 0)
                    {
                        Check(ParallelConstraintPreparation.TryRun(parallel), "parallel preparation executes: " + count);
                        Check(Bits(serial._velocityConstraints).SequenceEqual(Bits(parallel._velocityConstraints)), "native/parallel preparation bit exact: " + count);
                        Check(Bits(serial._velocities).SequenceEqual(Bits(parallel._velocities)), "shared body velocities unchanged: " + count);
                        Check(Bits(serial._positions).SequenceEqual(Bits(parallel._positions)), "shared positions unchanged: " + count);
                    }
                }
                var subset = Make(17); var before = subset._velocityConstraints.Select(v => Bits(v)).ToArray();
                range(subset, 3, 14);
                for (int i = 0; i < 17; i++) if (i < 3 || i >= 14)
                    Check(before[i].SequenceEqual(Bits(subset._velocityConstraints[i])), "range excludes external contacts: " + i);
                var original = AccessTools.Method(typeof(ContactSolver), "InitializeVelocityConstraints");
                foreign.Patch(original, prefix: new HarmonyMethod(typeof(ConstraintPreparationTests), nameof(EmptyPrefix)));
                Check(!ParallelConstraintPreparation.TryRun(Make(17)), "foreign native hook forces original path");
                foreign.UnpatchSelf();
                var benchmarks = new List<object>();
                foreach (int count in new[] { 64, 256, 1024, 4096, 16384 })
                {
                    var serial = Make(count); var parallel = Make(count);
                    Action baseline = () => serial.InitializeVelocityConstraints();
                    Action cloned = () => range(parallel, 0, count);
                    Action optimized = () => { if (!ParallelConstraintPreparation.TryRun(parallel)) parallel.InitializeVelocityConstraints(); };
                    for (int i = 0; i < 20; i++) { baseline(); optimized(); }
                    var first = Time(baseline); var copyTime = Time(cloned); var second = Time(optimized);
                    var workers = new List<object>();
                    foreach (int threads in new[] { 1, 2, 4 })
                    {
                        Action raw = () => System.Threading.Tasks.Parallel.For(0, threads,
                            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads }, chunk => range(parallel, count * chunk / threads, count * (chunk + 1) / threads));
                        var timing = Time(raw); workers.Add(new { workers = threads, raw_median_ms = timing.median });
                    }
                    benchmarks.Add(new { contacts = count, serial_median_ms = first.median, complete_parallel_median_ms = second.median,
                        cloned_serial_ms = copyTime.median, raw_parallel = workers, serial_p95_ms = first.p95, complete_parallel_p95_ms = second.p95, speedup = first.median / second.median });
                }
                var realHook = new Harmony("barotrauma.reboundguard.experimental.v040");
                object hookBenchmark;
                try
                {
                    var body = Make(4096);
                    double unpatched = Time(() => body.InitializeVelocityConstraints()).median;
                    AccessTools.Method(typeof(ParallelConstraintPreparation), "Install").Invoke(null, new object[] { realHook });
                    Config.ParallelConstraintPreparation = false;
                    double patchedSerial = Time(() => body.InitializeVelocityConstraints()).median;
                    Config.ParallelConstraintPreparation = true;
                    double patchedParallel = Time(() => body.InitializeVelocityConstraints()).median;
                    hookBenchmark = new { contacts = 4096, unpatched_ms = unpatched, patched_serial_ms = patchedSerial, patched_parallel_ms = patchedParallel };
                }
                finally { realHook.UnpatchSelf(); }
                return new { checks, benchmarks, actual_hook_benchmark = hookBenchmark, note = "Installed original method versus range-cloned native IL. Full TryRun cost includes conflict checks and scheduling. Synthetic repeated polygon manifold with independent constraint objects but shared read-only body states; not whole-game FPS." };
            }
            finally { foreign.UnpatchSelf(); Config.ParallelConstraintPreparation = enabled; Config.MinimumContactsForParallelPreparation = threshold; }
        }
        private static void EmptyPrefix() { }
        private static (double median, double p95) Time(Action action)
        {
            var times = new double[19];
            for (int sample = 0; sample < times.Length; sample++)
            { long start = Stopwatch.GetTimestamp(); for (int i = 0; i < 8; i++) action(); times[sample] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency / 8; }
            Array.Sort(times); return (times[9], times[18]);
        }
    }
}
