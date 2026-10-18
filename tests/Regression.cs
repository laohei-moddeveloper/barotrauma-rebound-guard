using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ReboundGuard;

[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Barotrauma")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("BarotraumaCore")]

namespace ReboundGuardTests
{
    public static class Regression
    {
        private static int passed;
        private static void Check(bool condition, string name)
        {
            if (!condition) { throw new Exception("FAIL: " + name); }
            passed++;
        }
        public static string Run()
        {
            Vector2 target;
            // At acknowledgment: client=10, server=9. Current prediction=12.
            // The authoritative error is -1, so correction is 12-1=11, rather than 9.
            Check(CorrectionMath.TryProject(new Vector2(9, 1), new Vector2(12, 1), new Vector2(-1, 0), out target)
                && target == new Vector2(11, 1), "preserves unacknowledged travel and applies full server error");
            Check(!CorrectionMath.TryProject(Vector2.Zero, Vector2.One, new Vector2(0.5f, 0), out target), "vanilla small-error branch untouched");
            Check(!CorrectionMath.TryProject(Vector2.Zero, new Vector2(4, 0), new Vector2(-2.01f, 0), out target), "large corrections preserved");
            Check(!CorrectionMath.TryProject(Vector2.Zero, new Vector2(10, 0), new Vector2(-1, 0), out target), "excess travel rejected");
            Check(!CorrectionMath.TryProject(Vector2.Zero, new Vector2(1, 0), new Vector2(-1, 0), out target), "no travel does not change vanilla");
            Check(!CorrectionMath.TryProject(Vector2.Zero, new Vector2(1, 0), new Vector2(1, 0), out target), "larger projected jumps rejected");
            Check(!CorrectionMath.TryProject(new Vector2(float.NaN, 0), Vector2.Zero, Vector2.One, out target), "NaN rejected");
            Check(!CorrectionMath.TryProject(Vector2.Zero, new Vector2(float.PositiveInfinity, 0), Vector2.One, out target), "infinity rejected");

            var random = new Random(14730);
            for (int i = 0; i < 2000; i++)
            {
                Vector2 history = new Vector2((float)random.NextDouble() * 10, 1);
                Vector2 current = history + new Vector2((float)random.NextDouble() * 4 - 2, 0);
                Vector2 error = new Vector2((float)random.NextDouble() * 4 - 2, 0);
                if (CorrectionMath.TryProject(history, current, error, out target))
                {
                    Check(Vector2.Distance(target, current + error) < 0.00001f &&
                        Vector2.Distance(current, target) < Vector2.Distance(current, history), "projection invariants");
                }
            }
            MethodInfo original = AccessTools.Method(typeof(Ragdoll), "UpdateNetPlayerPositionProjSpecific");
            var code = PatchProcessor.GetOriginalInstructions(original);
            var patched = Plugin.TranspileCorrection(code, original).ToList();
            Check(patched.Count == code.Count + 4, "exactly one four-instruction injection");
            Check(patched.Count(c => c.operand is MethodInfo m && m.Name == "ChooseCorrectionTarget") == 1, "only one helper call");
            Check(code.Count(c => c.operand is MethodInfo m && m.Name == "SetPosition") ==
                patched.Count(c => c.operand is MethodInfo m && m.Name == "SetPosition"), "all vanilla SetPosition calls retained");
            bool refused = false;
            try { Plugin.TranspileCorrection(new List<CodeInstruction>(), original).ToList(); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "unknown IL fails closed");
            var wrongThreshold = PatchProcessor.GetOriginalInstructions(original);
            foreach (var instruction in wrongThreshold)
                if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float f && f == 0.5f) instruction.operand = 0.6f;
            refused = false;
            try { Plugin.TranspileCorrection(wrongThreshold, original).ToList(); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "changed threshold fails closed");
            var duplicate = PatchProcessor.GetOriginalInstructions(original);
            duplicate.AddRange(PatchProcessor.GetOriginalInstructions(original));
            refused = false;
            try { Plugin.TranspileCorrection(duplicate, original).ToList(); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "ambiguous branch fails closed");
            var harmony = new Harmony("reboundguard.offline.regression");
            try
            {
                harmony.Patch(original, transpiler: new HarmonyMethod(typeof(Plugin), nameof(Plugin.TranspileCorrection)));
                Check(Harmony.GetPatchInfo(original).Owners.Contains(harmony.Id), "Harmony emits patch against installed game method");
                MethodInfo update = AccessTools.Method(typeof(GameScreen), "Update", new[] { typeof(double) });
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(Plugin), "BeforeStep"),
                    postfix: new HarmonyMethod(typeof(Plugin), "AfterStep"));
                Check(Harmony.GetPatchInfo(update).Owners.Contains(harmony.Id), "monitor patch emits against installed GameScreen.Update");
            }
            finally { harmony.UnpatchSelf(); }
            Check(Harmony.GetPatchInfo(original) == null || !Harmony.GetPatchInfo(original).Owners.Contains(harmony.Id), "test patches removed");
            return "PASS: " + passed + " assertions, including randomized math, installed IL shape, Harmony emission and cleanup. No game simulation or multiplayer session was run.";
        }
    }
}
