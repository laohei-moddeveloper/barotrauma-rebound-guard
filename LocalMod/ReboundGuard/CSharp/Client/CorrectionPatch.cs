using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ReboundGuard
{
    public partial class Plugin
    {
        static partial void InstallClientPatch(Harmony patcher)
        {
            ClientFrameProfiler.Install(patcher);
            ExitLifetimePatch.Install(patcher);
            Version version = typeof(Ragdoll).Assembly.GetName().Version;
            if (version != new Version(1, 13, 4, 0))
            {
                Log("correction disabled: supported assembly version is 1.13.4.0; found " + version);
                return;
            }
            patcher.Patch(AccessTools.Method(typeof(Ragdoll), "UpdateNetPlayerPositionProjSpecific"),
                transpiler: new HarmonyMethod(typeof(Plugin), nameof(TranspileCorrection)));
            ClientPatchInstalled = true;
        }

        // Patch exactly the >0.5 correction branch. Other SetPosition calls are untouched.
        // Reject an unknown IL shape rather than guessing a local-variable number.
        public static IEnumerable<CodeInstruction> TranspileCorrection(
            IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            var candidates = new List<Tuple<int, int>>();
            for (int i = 4; i < code.Count - 18; i++)
            {
                if (code[i].opcode != OpCodes.Ldc_R4 || !(code[i].operand is float threshold) || threshold != 0.5f)
                    continue;
                if (code[i + 1].opcode.FlowControl != FlowControl.Cond_Branch) { continue; }
                if (!(code[i - 3].operand is MethodInfo length) || length.DeclaringType != typeof(Vector2) || length.Name != "Length")
                    continue;
                int errorLocal = LocalIndex(code[i - 4]);
                if (errorLocal < 0 || __originalMethod.GetMethodBody().LocalVariables[errorLocal].LocalType != typeof(Vector2))
                    continue;
                int call = -1, clear = -1;
                for (int j = i + 2; j < Math.Min(i + 20, code.Count); j++)
                {
                    if (!(code[j].operand is MethodInfo method)) { continue; }
                    if (method.Name == "Clear" && method.DeclaringType == typeof(List<CharacterStateInfo>)) { clear = j; }
                    if (method.Name == "SetPosition" && method.DeclaringType == typeof(Ragdoll)) { call = j; break; }
                }
                if (clear < 0 || call < 5) { continue; }
                if (code[call - 4].opcode != OpCodes.Ldc_I4_1 || code[call - 3].opcode != OpCodes.Ldc_I4_0 ||
                    code[call - 2].opcode != OpCodes.Ldc_I4_0 || code[call - 1].opcode != OpCodes.Ldc_I4_1)
                    continue;
                if (!(code[call - 5].operand is MethodInfo getter) || getter.DeclaringType != typeof(PosInfo) || getter.Name != "get_Position")
                    continue;
                candidates.Add(Tuple.Create(call - 4, errorLocal));
            }
            if (candidates.Count != 1)
                throw new InvalidOperationException("Unknown correction IL; expected one branch, found " + candidates.Count);
            int insertion = candidates[0].Item1;
            var first = new CodeInstruction(OpCodes.Ldarg_0);
            first.labels.AddRange(code[insertion].labels);
            code[insertion].labels.Clear();
            code.InsertRange(insertion, new[] {
                first,
                new CodeInstruction(OpCodes.Ldloc, candidates[0].Item2),
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Plugin), nameof(ChooseCorrectionTarget)))
            });
            return code;
        }

        private static int LocalIndex(CodeInstruction instruction)
        {
            if (instruction.opcode != OpCodes.Ldloca && instruction.opcode != OpCodes.Ldloca_S) { return -1; }
            if (instruction.operand is LocalBuilder builder) { return builder.LocalIndex; }
            if (instruction.operand is LocalVariableInfo info) { return info.LocalIndex; }
            if (instruction.operand is int index) { return index; }
            if (instruction.operand is byte small) { return small; }
            return -1;
        }

        private static Vector2 ChooseCorrectionTarget(Vector2 historical, Ragdoll ragdoll, Vector2 error, float lowestSubPos)
        {
            LargeCorrections++;
            Vector2 current = ragdoll.Collider.SimPosition;
            Vector2 target = historical;
            Character character = ragdoll.Character;
            if (Settings.ApplyExperimentalCorrection && GameMain.Client?.Character == character &&
                character.CanMove && !character.IsDead && !character.IsUnconscious &&
                ragdoll.ColliderControlsMovement && !character.IsKeyDown(InputType.Ragdoll) &&
                character.AnimController.Anim == AnimController.Animation.None && character.SelectedCharacter == null &&
                !ragdoll.IsHangingWithRope && !ragdoll.IsHoldingToRope && !ragdoll.IsDraggedWithRope &&
                CorrectionMath.TryProject(historical, current, error, out Vector2 projected))
            {
                // Stay within one hull and one coordinate space. Keep vanilla transitions,
                // out-of-submarine corrections and platform/ragdoll corrections intact.
                Hull hull = character.CurrentHull;
                if (hull != null && character.Submarine == hull.Submarine &&
                    SameHull(historical, hull, lowestSubPos) &&
                    SameHull(historical - error, hull, lowestSubPos) &&
                    SameHull(current, hull, lowestSubPos) && SameHull(projected, hull, lowestSubPos))
                {
                    target = projected;
                    AdjustedCorrections++;
                }
            }
            double displacement = Vector2.Distance(current, historical);
            CorrectionTelemetry.Observe(GameMain.Client?.Character == character, displacement, error.Length());
            WorstOriginalJump = Math.Max(WorstOriginalJump, displacement);
            WorstAdjustedJump = Math.Max(WorstAdjustedJump, Vector2.Distance(current, target));
            return target;
        }

        private static bool SameHull(Vector2 point, Hull hull, float lowestSubPos)
        {
            return Hull.FindHull(ConvertUnits.ToDisplayUnits(point), hull, point.Y < lowestSubPos) == hull;
        }
    }
}
