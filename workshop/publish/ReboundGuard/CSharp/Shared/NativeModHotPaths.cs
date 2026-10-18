using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Barotrauma;
using HarmonyLib;
using MoonSharp.Interpreter;

namespace ReboundGuard
{
    // Opted into by the exact, audited Lua source replacements. No global Lua
    // disabling, stale classification cache or automatic deletion of other hooks.
    public static class NativeModHotPaths
    {
        internal const string Owner = "barotrauma.reboundguard.nativecompat.v040";
        private static readonly Harmony Patcher = new Harmony(Owner);
        private static readonly Dictionary<string, Action> Fallbacks = new Dictionary<string, Action>();
        private static bool ready, isHumanInstalled, buffsInstalled;
        private static long humanCalls, buffCalls, prevented;
        private static readonly Identifier Grip = "tsm_straight_grip_buff".ToIdentifier();
        private static readonly Identifier Exo = "tsm_buff_dsdj_exo_bag".ToIdentifier();
        private static readonly Identifier Analgesic = "tsm_analgesic_buff".ToIdentifier();

        internal static void Start() { ready = true; }
        public static bool InstallMudraptorIsHumanLua(DynValue restoreLua) => restoreLua?.Type == DataType.Function &&
            InstallMudraptorIsHuman(() => restoreLua.Function.OwnerScript.Call(restoreLua, Array.Empty<DynValue>()));
        public static bool InstallTSMBuffHooksLua(DynValue restoreLua) => restoreLua?.Type == DataType.Function &&
            InstallTSMBuffHooks(() => restoreLua.Function.OwnerScript.Call(restoreLua, Array.Empty<DynValue>()));
        public static bool InstallMudraptorIsHuman(Action restoreLua)
        {
            if (!ready || !Settings.NativeKnownModHotPaths || restoreLua == null) return false;
            if (isHumanInstalled) { Fallbacks["mudraptor"] = restoreLua; return true; }
            MethodInfo getter = AccessTools.PropertyGetter(typeof(Character), "IsHuman");
            if (getter == null || getter.ReturnType != typeof(bool) || PatchSafety.HasForeignPatch(getter)) return false;
            Patcher.Patch(getter, postfix: new HarmonyMethod(typeof(NativeModHotPaths), nameof(AfterIsHuman)));
            Fallbacks["mudraptor"] = restoreLua; isHumanInstalled = true;
            Plugin.Log("native mod hot path installed: Mudraptor IsHuman; original_result OR live Params.UseHumanAI; Lua callback replaced at its source");
            return true;
        }
        private static void AfterIsHuman(Character __instance, ref bool __result)
        {
            Interlocked.Increment(ref humanCalls);
            if (!__result && __instance.Params != null) __result = __instance.Params.UseHumanAI;
        }
        public static bool InstallTSMBuffHooks(Action restoreLua)
        {
            if (!ready || !Settings.NativeKnownModHotPaths || restoreLua == null) return false;
            if (buffsInstalled) { Fallbacks["tsm"] = restoreLua; return true; }
            MethodInfo movement = AccessTools.Method(typeof(Character), "CalculateMovementPenalty");
            MethodInfo aim = AccessTools.Method(typeof(AnimController), "GetAimWobble");
            if (movement == null || aim == null || movement.ReturnType != typeof(float) || aim.ReturnType != typeof(float) ||
                PatchSafety.HasForeignPatch(movement) || PatchSafety.HasForeignPatch(aim)) return false;
            try
            {
                Patcher.Patch(movement, prefix: new HarmonyMethod(typeof(NativeModHotPaths), nameof(BeforeMovement)));
                Patcher.Patch(aim, prefix: new HarmonyMethod(typeof(NativeModHotPaths), nameof(BeforeAim)));
            }
            catch
            {
                Patcher.Unpatch(movement, AccessTools.Method(typeof(NativeModHotPaths), nameof(BeforeMovement)));
                Patcher.Unpatch(aim, AccessTools.Method(typeof(NativeModHotPaths), nameof(BeforeAim)));
                throw;
            }
            Fallbacks["tsm"] = restoreLua; buffsInstalled = true;
            Plugin.Log("native mod hot paths installed: TSM movement penalty and aim wobble; three live affliction checks retained");
            return true;
        }
        internal static bool HasTSMBuff(Character character)
        {
            CharacterHealth health = character?.CharacterHealth;
            return health != null && (AtLeastOne(health, Grip) || AtLeastOne(health, Exo) || AtLeastOne(health, Analgesic));
        }
        private static bool AtLeastOne(CharacterHealth health, Identifier identifier)
        {
            Affliction affliction = health.GetAffliction(identifier);
            return affliction != null && affliction.Strength >= 1f;
        }
        private static bool BeforeMovement(Character __instance, ref float __result) => ContinueOrZero(__instance, ref __result);
        private static bool BeforeAim(AnimController __instance, ref float __result) => ContinueOrZero(__instance.Character, ref __result);
        private static bool ContinueOrZero(Character character, ref float result)
        {
            Interlocked.Increment(ref buffCalls);
            if (!HasTSMBuff(character)) return true;
            result = 0f; Interlocked.Increment(ref prevented); return false;
        }
        internal static string Report() => "native mod hot paths: mudraptor=" + isHumanInstalled + "; tsm=" + buffsInstalled +
            "; is_human_calls=" + Interlocked.Exchange(ref humanCalls, 0) + "; buff_calls=" + Interlocked.Exchange(ref buffCalls, 0) +
            "; original_methods_prevented_by_existing_buff_rule=" + Interlocked.Exchange(ref prevented, 0);
        internal static void Stop(bool restoreLua)
        {
            ready = false; Patcher.UnpatchSelf(); isHumanInstalled = buffsInstalled = false;
            bool canRestore = false;
            if (restoreLua && Fallbacks.Count > 0 && !IsGameExiting())
            {
                try { canRestore = LuaCsSetup.Instance?.EventService != null && !LuaCsSetup.Instance.EventService.IsDisposed; }
                catch { /* LuaCs may already be tearing down its services. */ }
            }
            if (canRestore)
            {
                foreach (Action fallback in Fallbacks.Values)
                {
                    try { fallback(); }
                    catch (Exception error) { Plugin.Log("native hot path Lua restoration failed: " + error); }
                }
            }
            Fallbacks.Clear(); humanCalls = buffCalls = prevented = 0;
        }
        private static bool IsGameExiting()
        {
            MethodInfo getter = AccessTools.PropertyGetter(typeof(GameMain), "IsExiting");
            return getter != null && (bool)getter.Invoke(null, null);
        }
    }
}
