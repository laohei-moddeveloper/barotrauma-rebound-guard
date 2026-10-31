using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace ReboundGuard
{
    // Hoist the native Apply event-type rejection ahead of nearby-target gathering.
    // Only this Update call site changes. Insertion/removal and custom subclasses
    // still call the original predicate and Apply implementations.
    public static class ContainerEffectFastPath
    {
        private static MethodInfo update, predicate, apply;
        private static MethodBase[] protectedMethods;
        private static bool installed;
        [ThreadStatic] private static bool safeInSimulation;
        [ThreadStatic] private static bool safetyChecked, safetyAllowed;
        private static long safetyChecks;
        private static long skipped;
        private static string reason = "not installed";

        internal static void Install(Harmony harmony)
        {
            if (!Settings.SkipIrrelevantContainerSearches) { reason = "disabled"; return; }
            Bind();
            if (!SupportedCodeMatches()) { reason = "unrecognized code; original behavior"; return; }
            harmony.Patch(update, transpiler: new HarmonyMethod(typeof(ContainerEffectFastPath), nameof(Transpiler)));
            harmony.Patch(AccessTools.Method(typeof(GameScreen), "Update", new[] { typeof(double) }),
                prefix: new HarmonyMethod(typeof(ContainerEffectFastPath), nameof(BeginSimulation)),
                finalizer: new HarmonyMethod(typeof(ContainerEffectFastPath), nameof(EndSimulation)));
            installed = true;
            Plugin.Log("container no-op nearby-search guard installed");
        }

        private static void Bind()
        {
            if (update != null) return;
            update = AccessTools.Method(typeof(ItemContainer), "Update");
            predicate = AccessTools.Method(typeof(ItemContainer), "ShouldApplyEffects");
            apply = typeof(StatusEffect).GetMethods().Single(m => m.Name == "Apply" && m.GetParameters().Length == 5 &&
                m.GetParameters()[3].ParameterType == typeof(IReadOnlyList<ISerializableEntity>));
            var methods = new List<MethodBase> { update, predicate, apply,
                AccessTools.Method(typeof(StatusEffect), "AddNearbyTargets"),
                AccessTools.Method(typeof(StatusEffect), "HasTargetType"),
                AccessTools.PropertyGetter(typeof(StatusEffect), "Range"),
                AccessTools.PropertyGetter(typeof(ItemContainer.ActiveContainedItem), "StatusEffect") };
            methods.AddRange(typeof(StatusEffect).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "IsValidTarget"));
            protectedMethods = methods.ToArray();
        }

        internal static bool SupportedCodeMatches()
        {
            Bind();
            if (AuditedBuild.CurrentGame) return true;
            if (typeof(Item).Assembly.GetName().Version != new Version(1, 13, 4, 0)) return false;
            bool client = typeof(Item).Assembly.GetName().Name == "Barotrauma";
            return Fingerprint(update) == (client ? "F0D3997735C26353D4861439BA94347935B982F695DF44B0AFFE0015ADC50A1F" : "9956365790E2E6FB60BB54F1AE0A6E7CB0460881E25212075CE2EAD45604C012") &&
                Fingerprint(predicate) == (client ? "D702388C2578DF22C533E43FCB7F575BE99FF9912D96F20BEC93CF15A113A877" : "2BC8BC87560F357FA9B09040B366F659ED3C0940A15C6DE85F2DF8146AE8F60B") &&
                Fingerprint(apply) == (client ? "C94563530DF28D5407CC0B28E9FCD6D16BC847959CF6859840933943DAC9F133" : "D3A26786A89A17F5D9B5E6D7BB1874B214796E3F25CA43BD1519B4D97AACD825");
        }

        private static string Fingerprint(MethodBase method) =>
            Convert.ToHexString(SHA256.HashData(method.GetMethodBody().GetILAsByteArray()));

        // Check once per synchronous simulation step, not once per contained item.
        // Calls from outside that scope never use the shortcut.
        internal static void BeginSimulation()
        {
            safeInSimulation = installed && Settings.SkipIrrelevantContainerSearches;
            safetyChecked = safetyAllowed = false;
            reason = safeInSimulation ? "installed; safety checked on demand" : "disabled";
        }
        private static bool EnsureSafety()
        {
            if (!safeInSimulation) return false;
            if (safetyChecked) return safetyAllowed;
            safetyChecked = true; safetyChecks++;
            foreach (MethodBase method in protectedMethods)
            {
                if (PatchSafety.HasForeignPatch(method))
                { reason = "original fallback: other patch on " + method.DeclaringType.Name + "." + method.Name; return false; }
            }
            safetyAllowed = true;
            reason = "eligible";
            return true;
        }
        internal static void EndSimulation() { safeInSimulation = false; }

        internal static bool IsIrrelevantNearbyEffect(StatusEffect effect)
        {
            if (effect == null || effect.GetType() != typeof(StatusEffect)) return false;
            ActionType type = effect.type;
            return type != ActionType.OnActive && type != ActionType.OnContaining && type != ActionType.OnWearing &&
                effect.Range > 0f && (effect.HasTargetType(StatusEffect.TargetType.NearbyItems) ||
                effect.HasTargetType(StatusEffect.TargetType.NearbyCharacters));
        }

        private static bool ShouldApplyDuringUpdate(ItemContainer container, ItemContainer.ActiveContainedItem active)
        {
            if (safeInSimulation && Settings.SkipIrrelevantContainerSearches && IsIrrelevantNearbyEffect(active.StatusEffect) && EnsureSafety())
            { skipped++; return false; }
            return container.ShouldApplyEffects(active);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            Bind();
            var code = instructions.ToList();
            if (code.Count(i => i.Calls(predicate)) != 1)
                throw new InvalidOperationException("Unknown container Update call site");
            foreach (var instruction in code)
            {
                if (!instruction.Calls(predicate)) continue;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(ContainerEffectFastPath), nameof(ShouldApplyDuringUpdate));
            }
            return code;
        }
        internal static string Report()
        {
            string report = "container search: " + reason + "; irrelevant_searches_skipped=" + skipped + "; safety_checks=" + safetyChecks;
            skipped = safetyChecks = 0;
            return report;
        }
        internal static void Reset() { installed = safeInSimulation = safetyChecked = safetyAllowed = false; skipped = safetyChecks = 0; reason = "not installed"; }
    }
}
