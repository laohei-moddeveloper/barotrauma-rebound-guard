using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FarseerPhysics.Dynamics.Contacts;
using HarmonyLib;

namespace ReboundGuard
{
    // Initialization reads shared velocities but writes only the current contact's
    // own constraint/point objects. No body integration, callbacks or solver
    // iterations are moved out of the main thread.
    public static class ParallelConstraintPreparation
    {
        private sealed class Work
        {
            internal int Count, Workers;
            internal readonly ParallelOptions Options = new ParallelOptions { TaskScheduler = TaskScheduler.Default };
            internal readonly Action<int> Chunk;
            internal Work(ContactSolver solver)
            {
                Chunk = chunk => runRange(solver, (int)((long)Count * chunk / Workers),
                    (int)((long)Count * (chunk + 1) / Workers));
            }
        }
        private static readonly ConditionalWeakTable<ContactSolver, Work> Workspaces = new ConditionalWeakTable<ContactSolver, Work>();
        private static readonly MethodInfo Original = AccessTools.Method(typeof(ContactSolver), "InitializeVelocityConstraints");
        private static Action<ContactSolver, int, int> runRange;
        private static MethodBase[] protectedMethods;
        private static long parallelCalls, constraintsProcessed, elapsedTicks;
        private static long observedCalls, belowThreshold, cpuFallbacks, conflictFallbacks;
        private static int maximumContacts;
        private static bool installed;
        private static string lastConflict = "none";
        public static long ParallelCalls => parallelCalls;

        internal static bool SupportedCodeMatches()
        {
            if (AuditedBuild.CurrentPhysics) return true;
            if (Convert.ToHexString(SHA256.HashData(Original.GetMethodBody().GetILAsByteArray())) !=
                "BE4F1BF0B8D0C6A16EEA85A92CFBD124874D732DAC4FD96868DB8A006E4FA87D") return false;
            using (var file = File.OpenRead(typeof(ContactSolver).Assembly.Location))
            {
                string hash = Convert.ToHexString(SHA256.HashData(file));
                // All 1,973 resolved method bodies match the audited Farseer
                // binary in the installed LuaCs server package.
                return hash == "20851BA49BADF3F9037D887654C3F45018885268D76A0FB7D1099CB059610B44" ||
                    hash == "BD4AADF7604F993700953203571585EDE3E2ACAC01EA9900CD236AA6AF9F9E11";
            }
        }
        internal static void Bind()
        {
            if (runRange != null) return;
            if (!SupportedCodeMatches()) throw new InvalidOperationException("Unrecognized native constraint initialization");
            runRange = NativeLoopRange.Create(Original, out protectedMethods);
            protectedMethods = protectedMethods.Concat(new[] { AccessTools.Method(typeof(ContactSolver), "Reset") })
                .Concat(new[] { typeof(ContactSolver), typeof(ContactVelocityConstraint), typeof(ContactPositionConstraint), typeof(VelocityConstraintPoint) }
                    .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)))
                .ToArray();
        }
        internal static void Install(Harmony harmony)
        {
            if (!Settings.ParallelConstraintPreparation || !SupportedCodeMatches()) return;
            Bind();
            harmony.Patch(Original, prefix: new HarmonyMethod(typeof(ParallelConstraintPreparation), nameof(Before)));
            installed = true;
            Plugin.Log("native constraint preparation parallel path installed");
        }
        private static bool Before(ContactSolver __instance) => !TryRun(__instance);
        public static bool TryRun(ContactSolver solver)
        {
            observedCalls++; maximumContacts = Math.Max(maximumContacts, solver._count);
            if (!Settings.ParallelConstraintPreparation) return false;
            if (solver._count < Settings.MinimumContactsForParallelPreparation) { belowThreshold++; return false; }
            if (Environment.ProcessorCount < 4 || Settings.MaximumWorkers < 2) { cpuFallbacks++; return false; }
            Bind();
            foreach (MethodBase method in protectedMethods)
            {
                if (PatchSafety.HasForeignPatch(method))
                { conflictFallbacks++; lastConflict = method.DeclaringType.Name + "." + method.Name; return false; }
            }
            Work work = Workspaces.GetValue(solver, key => new Work(key));
            work.Count = solver._count;
            work.Workers = Math.Min(Settings.MaximumWorkers, Math.Max(1, Environment.ProcessorCount / 2));
            work.Options.MaxDegreeOfParallelism = work.Workers;
            long start = Stopwatch.GetTimestamp();
            Parallel.For(0, work.Workers, work.Options, work.Chunk);
            elapsedTicks += Stopwatch.GetTimestamp() - start;
            parallelCalls++; constraintsProcessed += solver._count;
            return true;
        }
        internal static string Report()
        {
            string text = "constraint preparation: parallel_calls=" + parallelCalls + "; contacts=" + constraintsProcessed +
                "; parallel_wall_ms=" + (elapsedTicks * 1000.0 / Stopwatch.Frequency).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                "; installed=" + installed + "; enabled=" + Settings.ParallelConstraintPreparation + "; observed_islands=" + observedCalls + "; max_contacts_per_island=" + maximumContacts +
                "; threshold=" + Settings.MinimumContactsForParallelPreparation + "; below_threshold=" + belowThreshold + "; cpu_fallbacks=" + cpuFallbacks + "; conflict_fallbacks=" + conflictFallbacks + "; last_conflict=" + lastConflict;
            ResetCounters(); return text;
        }
        private static void ResetCounters() { parallelCalls = constraintsProcessed = elapsedTicks = observedCalls = belowThreshold = cpuFallbacks = conflictFallbacks = 0; maximumContacts = 0; lastConflict = "none"; }
        internal static void Reset() { installed = false; ResetCounters(); }
    }
}
