using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using HarmonyLib;
using ReboundGuard;

namespace ReboundGuardTests
{
    public static class FieldIterationTests
    {
        private static int checks;
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        private static Type Internal(string name) => typeof(Plugin).Assembly.GetType("ReboundGuard." + name, true);
        private static T Call<T>(string name, string method, params object[] args) => (T)AccessTools.Method(Internal(name), method).Invoke(null, args);
        private static void Call(string name, string method, params object[] args) => AccessTools.Method(Internal(name), method).Invoke(null, args);
        private static Patch Entry(string owner)
        {
            var entry = (Patch)RuntimeHelpers.GetUninitializedObject(typeof(Patch));
            AccessTools.Field(typeof(Patch), "owner").SetValue(entry, owner); return entry;
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Empty() { }
        private static void Prefix() { }
        private static void Finalizer() { }
        private static double Nanoseconds(Action action)
        {
            for (int i = 0; i < 1000; i++) action();
            var samples = new double[9];
            for (int sample = 0; sample < samples.Length; sample++)
            {
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < 20000; i++) action();
                samples[sample] = (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / 20000;
            }
            Array.Sort(samples); return samples[4];
        }
        public static object Run()
        {
            const string owner = "barotrauma.reboundguard.experimental.v040";
            var read = (Func<Patches, bool>)AccessTools.Method(Internal("PatchSafety"), "HasForeignPatch", new[] { typeof(Patches) }).CreateDelegate(typeof(Func<Patches, bool>));
            Check(!read(null), "no patches are compatible");
            for (int group = 0; group < 5; group++)
            {
                var groups = Enumerable.Range(0, 5).Select(_ => new[] { Entry(owner) }).ToArray();
                Check(!read(new Patches(groups[0], groups[1], groups[2], groups[3], groups[4])), "all own patch groups allowed");
                groups[group] = new[] { Entry(owner), Entry("foreign") };
                var patches = new Patches(groups[0], groups[1], groups[2], groups[3], groups[4]);
                Check(read(patches) && patches.Owners.Any(id => id != owner), "foreign prefix/postfix/transpiler/finalizer/IL manipulator detected");
            }
            var own = Entry(owner); var empty = Array.Empty<Patch>();
            var samplePatches = new Patches(new[] { own }, new[] { own }, new[] { own }, new[] { own }, empty);
            Action legacy = () => { if (samplePatches.Owners.Any(id => id != owner)) throw new Exception(); };
            Action direct = () => { if (read(samplePatches)) throw new Exception(); };
            double legacyNs = Nanoseconds(legacy), directNs = Nanoseconds(direct);
            long before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) legacy(); long legacyBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) direct(); long directBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(directBytes == 0, "owner group inspection allocates no temporary collections");
            double legacyScopeNs, lazyScopeNs; long legacyScopeBytes, lazyScopeBytes;
            var plugin = new Plugin();
            try
            {
                plugin.Initialize(); plugin.OnLoadCompleted();
                MethodBase[] protectedMethods = ((MethodBase[])AccessTools.Field(typeof(NearbyItemIndex), "protectedMethods").GetValue(null))
                    .Concat((MethodBase[])AccessTools.Field(typeof(ContainerEffectFastPath), "protectedMethods").GetValue(null)).ToArray();
                var beginIndex = (Action)AccessTools.Method(typeof(NearbyItemIndex), "BeginSimulation").CreateDelegate(typeof(Action));
                var beginContainer = (Action)AccessTools.Method(typeof(ContainerEffectFastPath), "BeginSimulation").CreateDelegate(typeof(Action));
                Action legacyScope = () =>
                {
                    foreach (MethodBase protectedMethod in protectedMethods)
                    {
                        var patches = Harmony.GetPatchInfo(protectedMethod);
                        if (patches != null && patches.Owners.Any(id => id != owner)) return;
                    }
                };
                Action lazyScope = () => { beginIndex(); beginContainer(); };
                legacyScopeNs = Nanoseconds(legacyScope); lazyScopeNs = Nanoseconds(lazyScope);
                before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) legacyScope(); legacyScopeBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) lazyScope(); lazyScopeBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Check(lazyScopeBytes == 0, "idle scope entry allocates nothing with all real plugin patches installed");
                Check((long)AccessTools.Field(typeof(NearbyItemIndex), "safetyChecks").GetValue(null) == 0 &&
                    (long)AccessTools.Field(typeof(ContainerEffectFastPath), "safetyChecks").GetValue(null) == 0,
                    "idle entries never scan patches with full plugin installed");
            }
            finally { plugin.Dispose(); }

            Call("StepHealth", "Reset", true);
            for (int i = 0; i < 95; i++) Call("StepHealth", "Observe", 4.5, 100L + i * 20L, 110L + i * 20L, 0L);
            for (int i = 0; i < 4; i++) Call("StepHealth", "Observe", 18.0, 5000L + i * 20L, 5010L + i * 20L, 0L);
            Call("StepHealth", "Observe", 101.0, 6000L, 6010L, 0L);
            Check(Call<double>("StepHealth", "PercentileBound", .95) == 5, "p95 uses upper histogram bound");
            Check(Call<double>("StepHealth", "PercentileBound", .99) == 20, "p99 includes spikes");
            string health = Call<string>("StepHealth", "Report", 7000L);
            Check(health.Contains("steps_over_16_667ms=5") && health.Contains("steps_over_50ms=1"), "slow step counters exact");
            Call("StepHealth", "Reset", true);
            Check(Call<double>("StepHealth", "PercentileBound", .95) == 0, "health resets between windows");
            Call("CorrectionTelemetry", "Reset");
            Call("CorrectionTelemetry", "Observe", true, .1, .6);
            Call("CorrectionTelemetry", "Observe", true, 2.5, .8);
            Call("CorrectionTelemetry", "Observe", false, 20.0, 30.0);
            string correction = Call<string>("CorrectionTelemetry", "Report");
            Check(correction.Contains("controlled_character_events=2") && correction.Contains("other_character_events=1") && correction.Contains("controlled_target_displacements_over_2=1") && correction.Contains("max_controlled_ack_error=0.800"), "acknowledged error and target travel distinguished");
            Call("CorrectionTelemetry", "Reset");

            var harmony = new Harmony(owner);
            var method = AccessTools.Method(typeof(FieldIterationTests), nameof(Empty));
            bool diagnostics = Settings.Diagnostics, profiling = Settings.DetailedProfiling;
            double plainNs = Nanoseconds(Empty), monitoredNs;
            try
            {
                Settings.Diagnostics = Settings.DetailedProfiling = true;
                Call("SubsystemProfiler", "Reset");
                Call("SubsystemProfiler", "Add", harmony, method);
                Call("SubsystemProfiler", "BeginStep");
                for (int i = 0; i < 65; i++) Empty();
                Call("SubsystemProfiler", "CaptureWorstStep"); Call("SubsystemProfiler", "EndStep");
                string report = Call<string>("SubsystemProfiler", "Report");
                Check(report.Contains("calls=65"), "every coarse invocation measured without 32-step alias");
                Check(Call<string>("SubsystemProfiler", "Report").Contains("calls=0"), "profiler counters reset on report");
                monitoredNs = Nanoseconds(Empty);
                harmony.UnpatchSelf(); Call("SubsystemProfiler", "Reset");
                // A void finalizer must preserve, rather than swallow, failures.
                var failing = AccessTools.Method(typeof(FieldIterationTests), nameof(ThrowExpected));
                Call("SubsystemProfiler", "Add", harmony, failing);
                bool threw = false; try { ThrowExpected(); } catch (InvalidOperationException) { threw = true; }
                Check(threw && Call<string>("SubsystemProfiler", "Report").Contains("calls=1"), "throwing original observed and exception preserved");
            }
            finally { harmony.UnpatchSelf(); Call("SubsystemProfiler", "Reset"); Settings.Diagnostics = diagnostics; Settings.DetailedProfiling = profiling; }
            return new { checks, owner_inspection_legacy_ns = legacyNs, owner_inspection_direct_ns = directNs, owner_inspection_legacy_bytes_per_1000 = legacyBytes,
                owner_inspection_direct_bytes_per_1000 = directBytes, empty_method_ns = plainNs, empty_method_with_profiler_ns = monitoredNs,
                legacy_idle_scope_ns = legacyScopeNs, lazy_idle_scope_ns = lazyScopeNs, legacy_idle_scope_bytes_per_1000 = legacyScopeBytes, lazy_idle_scope_bytes_per_1000 = lazyScopeBytes,
                note = "Owner inspection excludes GetPatchInfo wrapper allocations. Profiler timer includes Harmony overhead on an empty method, not game FPS. No live multiplayer run." };
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void ThrowExpected() => throw new InvalidOperationException("expected test exception");
    }
}
