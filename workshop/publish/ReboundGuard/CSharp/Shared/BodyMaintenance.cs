using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Barotrauma;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using HarmonyLib;

namespace ReboundGuard
{
    // Only PhysicsBody.Update's object-local bookkeeping. NOT the collision solver,
    // ragdoll animation, game items, AI, water or electrical simulation.
    public static class BodyMaintenance
    {
        private static readonly List<PhysicsBody> Empty = new List<PhysicsBody>();
        private sealed class Work
        {
            internal int Count, Chunks;
            internal readonly ParallelOptions Options = new ParallelOptions { TaskScheduler = TaskScheduler.Default };
            internal readonly Action<int> Chunk;
            internal Work(List<PhysicsBody> bodies)
            {
                Chunk = chunk =>
                {
                    int from = (int)((long)Count * chunk / Chunks), to = (int)((long)Count * (chunk + 1) / Chunks);
                    for (int i = from; i < to; i++)
                    {
                        PhysicsBody body = bodies[i];
                        if ((body.Enabled || body.UserData is Character) && body.BodyType != BodyType.Static) body.Update();
                    }
                };
            }
        }
        private static readonly ConditionalWeakTable<List<PhysicsBody>, Work> Workspaces = new ConditionalWeakTable<List<PhysicsBody>, Work>();
        private static bool installed, safe;
        private static int parallelFrames;
        private static double parallelMs;
        private static string safetyReason = "not installed";
        private static string eligibleReason;
        private static int reasonThreshold = -1;
        private static MethodInfo target;
        private static MethodBase[] protectedMethods;

        internal static void Install(Harmony harmony)
        {
            if (!Settings.ParallelBodyMaintenance) { safetyReason = "disabled"; return; }
            target = AccessTools.Method(typeof(GameScreen), "Update", new[] { typeof(double) });
            if (!SupportedCodeMatches())
            {
                safetyReason = "serial fallback: unrecognized maintenance/game-loop code";
                Plugin.Log(safetyReason);
                return;
            }
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(BodyMaintenance), nameof(Transpiler)));
            installed = true;
            RefreshSafety();
            Plugin.Log("body maintenance: " + safetyReason);
        }

        internal static bool SupportedCodeMatches()
        {
            if (AuditedBuild.CurrentGame && AuditedBuild.CurrentPhysics) return true;
            if (typeof(PhysicsBody).Assembly.GetName().Version != new Version(1, 13, 4, 0)) return false;
            bool client = typeof(PhysicsBody).Assembly.GetName().Name == "Barotrauma";
            string expectedBody = client ? "100E0C6293027DEAE42FD054BE1F882807EDBB5B1E9F4593C22C9AA90926F126" : "D36CBDCC45FA3AF88359BBEE5FA7A02E16222C15CDD80108D6F4F71171FDB4C0";
            string expectedLoop = client ? "DFE2B1F546335575171B9D16DE973CECFD0A2EE260C11643416FA2FAF07179B3" : "0D35B40204D175A05880E143D69213D93B65820F2EF6A45326A54D1B9768A160";
            MethodInfo loop = AccessTools.Method(typeof(GameScreen), "Update", new[] { typeof(double) });
            string loopHash = Fingerprint(loop);
            // Installed LuaCs builds use different metadata tokens in this method.
            // Cecil-resolved instructions match the audited loop on both sides.
            bool knownLoop = loopHash == expectedLoop ||
                (client && loopHash == "12348DE39AC4F5AB15B3217BC724B121E6C6E77396DEFF389F9DBE7D56058CAD") ||
                (!client && loopHash == "9E2BE7E5A3E6BB41F74452A4F3F7593A1A3E200D83964849786A772DD939CFCD");
            return Fingerprint(AccessTools.Method(typeof(PhysicsBody), "Update")) == expectedBody && knownLoop;
        }
        private static string Fingerprint(MethodBase method)
        {
            byte[] bytes = method?.GetMethodBody()?.GetILAsByteArray();
            return bytes == null ? "" : Convert.ToHexString(SHA256.HashData(bytes));
        }

        internal static void RefreshSafety()
        {
            safe = installed;
            if (!installed) { return; }
            if (reasonThreshold != Settings.MinimumBodiesForParallel)
            { reasonThreshold = Settings.MinimumBodiesForParallel; eligibleReason = "eligible; threshold=" + reasonThreshold; }
            safetyReason = eligibleReason;
            // Third-party patches may add shared side effects to otherwise local
            // operations, or alter the loop that our empty enumerator bypasses.
            if (protectedMethods == null)
            {
            var methods = new List<MethodBase> { target,
                AccessTools.Method(typeof(PhysicsBody), "Update"),
                AccessTools.PropertyGetter(typeof(PhysicsBody), "Enabled"),
                AccessTools.PropertyGetter(typeof(PhysicsBody), "UserData"),
                AccessTools.PropertyGetter(typeof(PhysicsBody), "BodyType"),
                AccessTools.PropertyGetter(typeof(PhysicsBody), "SuppressSmoothRotationCalls"),
                AccessTools.PropertySetter(typeof(PhysicsBody), "SuppressSmoothRotationCalls") };
            Type config = AccessTools.TypeByName("Barotrauma.Networking.NetConfig");
            if (config != null)
            {
                methods.Add(AccessTools.Method(config, "InterpolateSimPositionError"));
                methods.Add(AccessTools.Method(config, "InterpolateRotationError"));
            }
            protectedMethods = methods.ToArray();
            }
            foreach (MethodBase method in protectedMethods)
            {
                if (method == null) { safe = false; safetyReason = "missing protected method"; return; }
                if (PatchSafety.HasForeignPatch(method))
                {
                    // Read-only profiling patches are conservatively treated as
                    // conflicts too. Correctness takes priority over parallelism.
                    safe = false;
                    safetyReason = "serial fallback: other patch on " + method.DeclaringType.Name + "." + method.Name;
                    return;
                }
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            MethodInfo enumerate = typeof(List<PhysicsBody>).GetMethod("GetEnumerator", Type.EmptyTypes);
            MethodInfo update = AccessTools.Method(typeof(PhysicsBody), "Update", Type.EmptyTypes);
            int candidate = -1, matches = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (!code[i].Calls(enumerate)) { continue; }
                for (int j = i + 1; j < Math.Min(code.Count, i + 45); j++)
                {
                    if (code[j].Calls(enumerate)) { break; }
                    if (code[j].Calls(update)) { candidate = i; matches++; break; }
                }
            }
            if (matches != 1) { throw new InvalidOperationException("Unknown PhysicsBody maintenance loop: " + matches); }
            // Preserve every label and exception block on the original instruction.
            code[candidate].opcode = OpCodes.Call;
            code[candidate].operand = AccessTools.Method(typeof(BodyMaintenance), nameof(RunOrEnumerate));
            return code;
        }

        private static List<PhysicsBody>.Enumerator RunOrEnumerate(List<PhysicsBody> bodies)
        {
            if (!Settings.ParallelBodyMaintenance || !installed || Environment.ProcessorCount < 4 ||
                bodies.Count < Settings.MinimumBodiesForParallel)
                return bodies.GetEnumerator();
            RefreshSafety();
            if (!safe) return bodies.GetEnumerator();
            long start = Stopwatch.GetTimestamp();
            RunParallel(bodies, Math.Min(Settings.MaximumWorkers, Math.Max(1, Environment.ProcessorCount / 2)));
            parallelMs += (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            parallelFrames++;
            // Parallel.For is synchronous: all writes complete before the original
            // method proceeds to GameSession, AI, items, animation or World.Step.
            return Empty.GetEnumerator();
        }

        internal static void RunParallel(List<PhysicsBody> bodies, int workers)
        {
            Work work = Workspaces.GetValue(bodies, key => new Work(key));
            work.Count = bodies.Count;
            work.Chunks = Math.Min(Math.Max(1, workers), Math.Max(1, work.Count));
            work.Options.MaxDegreeOfParallelism = work.Chunks;
            Parallel.For(0, work.Chunks, work.Options, work.Chunk);
        }

        internal static string Report()
        {
            RefreshSafety();
            string result = safetyReason + "; enabled=" + Settings.ParallelBodyMaintenance +
                "; parallel_frames=" + parallelFrames + "; total_ms=" + parallelMs.ToString("F2",System.Globalization.CultureInfo.InvariantCulture);
            parallelFrames = 0; parallelMs = 0;
            return result;
        }
        internal static void Reset() { installed = safe = false; parallelFrames = 0; parallelMs = 0; protectedMethods = null; Workspaces.Clear(); }
    }
}
