using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;

namespace ReboundGuard
{
    public partial class Plugin
    {
        static partial void InstallServerPatch(Harmony patcher)
        {
            NetworkQueuePatch.Install(patcher);
            InputBacklogMonitor.Install(patcher);
            if (Settings.DetailedProfiling) SubsystemProfiler.Add(patcher,AccessTools.Method(typeof(GameServer),"Update",new[]{typeof(float)}));
        }
        static partial void ReportServer() { Log(NetworkQueuePatch.Report()); Log(InputBacklogMonitor.Report()); }
    }

    public static class NetworkQueuePatch
    {
        private static readonly AccessTools.FieldRef<Queue<Entity>,int> Version = AccessTools.FieldRefAccess<Queue<Entity>,int>("_version");
        [ThreadStatic] private static QueueMembership<Entity> index;
        private static long lookups;
        private static int maxPending;
        internal static void Install(Harmony harmony)
        {
            if (!Settings.OptimizePositionQueue || typeof(GameServer).Assembly.GetName().Version != new Version(1,13,4,0)) { return; }
            harmony.Patch(AccessTools.Method(typeof(GameServer), "ClientWriteIngame"),
                transpiler: new HarmonyMethod(typeof(NetworkQueuePatch), nameof(Transpiler)),
                finalizer: new HarmonyMethod(typeof(NetworkQueuePatch), nameof(Finish)));
            Plugin.Log("server position queue membership index installed; FIFO and packet budgets unchanged");
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            MethodInfo contains = typeof(Queue<Entity>).GetMethod("Contains");
            MethodInfo enqueue = typeof(Queue<Entity>).GetMethod("Enqueue");
            MethodInfo dequeue = typeof(Queue<Entity>).GetMethod("Dequeue");
            if (code.Count(i=>i.Calls(contains)) != 3 || code.Count(i=>i.Calls(enqueue)) != 3 || code.Count(i=>i.Calls(dequeue)) != 2)
                throw new InvalidOperationException("Unknown position-queue IL");
            foreach (var instruction in code)
            {
                string replacement = instruction.Calls(contains) ? nameof(Contains) : instruction.Calls(enqueue) ? nameof(Enqueue) : instruction.Calls(dequeue) ? nameof(Dequeue) : null;
                if (replacement == null) { continue; }
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(NetworkQueuePatch), replacement);
            }
            return code;
        }
        private static QueueMembership<Entity> Index => index ?? (index = new QueueMembership<Entity>(queue => Version(queue)));
        public static bool Contains(Queue<Entity> queue, Entity entity)
        {
            lookups++;
            maxPending = Math.Max(maxPending,queue.Count);
            return Settings.OptimizePositionQueue ? Index.Contains(queue,entity) : queue.Contains(entity);
        }
        public static void Enqueue(Queue<Entity> queue, Entity entity)
        {
            if (Settings.OptimizePositionQueue) Index.Enqueue(queue,entity); else queue.Enqueue(entity);
        }
        public static Entity Dequeue(Queue<Entity> queue) { return Settings.OptimizePositionQueue ? Index.Dequeue(queue) : queue.Dequeue(); }
        public static void Finish() { index?.Reset(); }
        internal static string Report()
        {
            string result = "server position queue: membership_checks="+lookups+"; max_pending="+maxPending;
            lookups=0; maxPending=0;
            return result;
        }
    }
}
