using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ReboundGuard
{
    internal static class SubsystemProfiler
    {
        // Coarse entry points only. Every invocation avoids fixed-period alias.
        private sealed class Metric { internal long Ticks, Maximum, Count, StepTicks, WorstStepTicks, Allocated; }
        private struct Stamp { internal long Ticks, Allocated; }
        private static readonly Dictionary<MethodBase,Metric> Metrics = new Dictionary<MethodBase,Metric>();
        [ThreadStatic] private static bool inStep;
        internal static void Install(Harmony harmony)
        {
            if (typeof(Item).Assembly.GetName().Name == "DedicatedServer")
                Add(harmony, AccessTools.Method(AccessTools.TypeByName("Barotrauma.Networking.GameServer"), "Update", new[] { typeof(float) }));
            foreach (var pair in new[] { Tuple.Create(typeof(Character),"UpdateAll"), Tuple.Create(typeof(Character),"UpdateAnimAll"),
                Tuple.Create(typeof(StatusEffect),"UpdateAll"),Tuple.Create(typeof(MapEntity),"UpdateAll"),Tuple.Create(typeof(Ragdoll),"UpdateAll"),
                Tuple.Create(typeof(Powered),"UpdatePower") })
            {
                MethodInfo method=AccessTools.Method(pair.Item1,pair.Item2);
                Add(harmony,method);
            }
            Type world=AccessTools.TypeByName("FarseerPhysics.Dynamics.World");
            MethodInfo step=world==null?null:AccessTools.Method(world,"Step",new[]{typeof(float)});
            if(step!=null)
            {
                Add(harmony,step);
            }
        }
        internal static void Add(Harmony harmony,MethodBase method)
        {
            if(method==null || Metrics.ContainsKey(method))return;
            Metrics[method]=new Metric();
            harmony.Patch(method,prefix:new HarmonyMethod(typeof(SubsystemProfiler),nameof(Before)),finalizer:new HarmonyMethod(typeof(SubsystemProfiler),nameof(After)));
        }
        private static void Before(out Stamp __state)
        {
            __state = default;
            if (!Settings.Diagnostics || !Settings.DetailedProfiling) return;
            __state.Allocated = GC.GetAllocatedBytesForCurrentThread(); __state.Ticks = Stopwatch.GetTimestamp();
        }
        private static void After(MethodBase __originalMethod,Stamp __state)
        {
            if(__state.Ticks==0) return;
            Record(__originalMethod,Stopwatch.GetTimestamp()-__state.Ticks, GC.GetAllocatedBytesForCurrentThread()-__state.Allocated);
        }
        internal static void Record(MethodBase method,long elapsed,long allocated)
        {
            Metric metric=Metrics[method];
            Interlocked.Add(ref metric.Ticks,elapsed); Interlocked.Increment(ref metric.Count);
            Interlocked.Add(ref metric.Allocated,allocated);
            long previous=Interlocked.Read(ref metric.Maximum);
            while(elapsed>previous)
            {
                long observed=Interlocked.CompareExchange(ref metric.Maximum,elapsed,previous);
                if(observed==previous) break;
                previous=observed;
            }
            if(inStep) metric.StepTicks+=elapsed;
        }
        internal static void BeginStep() { foreach(Metric metric in Metrics.Values) metric.StepTicks=0; inStep=true; }
        internal static void EndStep() { inStep=false; }
        internal static void CaptureWorstStep() { foreach(Metric metric in Metrics.Values) metric.WorstStepTicks=metric.StepTicks; }
        internal static string WorstStepReport() => "worst step subsystem ms: "+string.Join("; ",Metrics.Select(pair=>pair.Key.DeclaringType.Name+"."+pair.Key.Name+"="+Milliseconds(pair.Value.WorstStepTicks)));
        private static string Milliseconds(long ticks) => (ticks*1000.0/Stopwatch.Frequency).ToString("F3",CultureInfo.InvariantCulture);
        internal static string Report()
        {
            return "subsystem timings (every invocation; nested totals overlap): "+string.Join("; ",Metrics.Select(pair=>
            {
                long count=Interlocked.Exchange(ref pair.Value.Count,0), ticks=Interlocked.Exchange(ref pair.Value.Ticks,0), maximum=Interlocked.Exchange(ref pair.Value.Maximum,0);
                long allocated=Interlocked.Exchange(ref pair.Value.Allocated,0);
                return pair.Key.DeclaringType.Name+"."+pair.Key.Name+" calls="+count+" avg_ms="+(count==0?0:ticks*1000.0/Stopwatch.Frequency/count).ToString("F3",CultureInfo.InvariantCulture)+" max_ms="+Milliseconds(maximum)+" thread_allocated_mb="+(allocated/1048576.0).ToString("F3",CultureInfo.InvariantCulture);
            }));
        }
        internal static void Reset() { inStep=false; Metrics.Clear(); }
    }
}
