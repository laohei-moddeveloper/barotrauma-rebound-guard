using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Compatibility;
using HarmonyLib;
using MoonSharp.Interpreter;
using ReboundGuard;

namespace ReboundGuardTests
{
    // Explicit endurance suite, excluded from All. Uses actual generated LuaCs
    // wrappers and a real Lua VM; never launches the game's entry point.
    public static class LuaDispatchStressTests
    {
        private static readonly MethodInfo Register = AccessTools.Method(typeof(LuaPatcherService), "Patch",
            new[] { typeof(string), typeof(MethodBase), typeof(LuaCsPatchFunc), typeof(ILuaCsHook.HookMethodType) });
        private static Type Optimizer => typeof(Plugin).Assembly.GetType("ReboundGuard.LuaBridgeOptimizer");
        [MethodImpl(MethodImplOptions.NoInlining)] public static int Target(int value) => value * 2;
        public static object Run()
        {
            int seconds = int.TryParse(Environment.GetEnvironmentVariable("REBOUND_GUARD_STRESS_SECONDS"), out int requested) ? requested : 30;
            if (seconds < 1 || seconds > 7200) throw new ArgumentOutOfRangeException("Stress duration must be 1..7200 seconds");
            string report = Environment.GetEnvironmentVariable("REBOUND_GUARD_STRESS_REPORT");
            var samples = new List<object>();
            var weakAssemblies = new Queue<WeakReference>();
            var harmony = new Harmony("barotrauma.reboundguard.experimental.v040");
            var service = new LuaPatcherService();
            var script = new Script();
            DynValue callback = script.DoString("return function(instance, p) return p.OriginalReturnValue + 3 end");
            MethodInfo target = AccessTools.Method(typeof(LuaDispatchStressTests), nameof(Target));
            var timer = Stopwatch.StartNew();
            DateTime started = DateTime.UtcNow;
            long calls = 0, cycles = 0, restores = 0, retainedChecks = 0;
            using var process = Process.GetCurrentProcess();
            object Snapshot(bool complete) => new
            {
                started_utc = started, elapsed_seconds = timer.Elapsed.TotalSeconds, requested_seconds = seconds,
                complete, calls, reset_cycles = cycles, native_restore_cycles = restores, retained_table_checks = retainedChecks,
                memory_samples = samples.ToArray(),
                note = "Real Lua VM and actual LuaCs native wrappers under isolated managed host. Repeated registration, hot invocation, native restoration and Reset. Weak assembly survival includes runtime/Harmony caches and is not alone proof of a leak. No live multiplayer, rendering or whole-game performance claim."
            };
            try
            {
                AccessTools.Method(Optimizer, "Install").Invoke(null, new object[] { harmony });
                while (timer.Elapsed.TotalSeconds < seconds)
                {
                    LuaPatcherService.ParameterTable retained = null;
                    Register.Invoke(service, new object[] { "stress-prefix", target, (LuaCsPatchFunc)((instance, table) => {
                        retained ??= table; table["value"] = (int)table["value"] + 1; return DynValue.Void;
                    }), ILuaCsHook.HookMethodType.Before });
                    Register.Invoke(service, new object[] { "stress-postfix", target, (LuaCsPatchFunc)((instance, table) => script.Call(callback, instance, table)), ILuaCsHook.HookMethodType.After });
                    LuaPatcherService.instance = service;
                    var entry = service.registeredPatches[LuaPatcherService.MethodKey.Create(target)];
                    if (!entry.HarmonyPrefixMethod.DeclaringType.Name.StartsWith("DirectBridge")) throw new Exception("Endurance suite did not exercise the direct compiler");
                    weakAssemblies.Enqueue(new WeakReference(entry.HarmonyPrefixMethod.DeclaringType.Assembly));
                    while (weakAssemblies.Count > 120) weakAssemblies.Dequeue();
                    double cycleEnd = Math.Min(seconds, timer.Elapsed.TotalSeconds + 1.0);
                    int value = 0;
                    do
                    {
                        for (int batch = 0; batch < 256; batch++)
                        {
                            int input = value++ & 1023;
                            int result = Target(input);
                            if (result != (input + 1) * 2 + 3) throw new Exception("Endurance callback result differs at call " + calls);
                            calls++;
                        }
                        if ((int)retained.OriginalParameters["value"] != 0 || (int)retained["value"] != 1)
                            throw new Exception("Retained ParameterTable reused across calls");
                        retainedChecks++;
                    } while (timer.Elapsed.TotalSeconds < cycleEnd);
                    if (cycles % 7 == 0)
                    {
                        AccessTools.Method(Optimizer, "Stop").Invoke(null, null);
                        if (Target(11) != 27) throw new Exception("Native restoration changed live callbacks");
                        restores++;
                    }
                    service.Reset(); LuaPatcherService.instance = service;
                    if (Target(11) != 22 || service.registeredPatches.Count != 0) throw new Exception("Reset left active callbacks");
                    if (service.patchModuleBuilder.IsValueCreated) throw new Exception("Reset retained the old dynamic module builder");
                    cycles++;
                    if (cycles % 30 == 0 || timer.Elapsed.TotalSeconds >= seconds)
                    {
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        process.Refresh();
                        samples.Add(new { seconds = timer.Elapsed.TotalSeconds, calls, cycles,
                            managed_bytes = GC.GetTotalMemory(false), private_bytes = process.PrivateMemorySize64,
                            recent_weak_assemblies = weakAssemblies.Count, recent_assemblies_alive = weakAssemblies.Count(reference => reference.IsAlive) });
                        if (report != null) File.WriteAllText(report + ".progress.json", JsonSerializer.Serialize(Snapshot(false), new JsonSerializerOptions { WriteIndented = true }));
                        Console.WriteLine("stress elapsed_s=" + timer.Elapsed.TotalSeconds.ToString("F1") + " cycles=" + cycles + " calls=" + calls);
                    }
                }
                return Snapshot(true);
            }
            finally { AccessTools.Method(Optimizer, "Stop").Invoke(null, null); service.Dispose(); harmony.UnpatchSelf(); }
        }
    }
}
