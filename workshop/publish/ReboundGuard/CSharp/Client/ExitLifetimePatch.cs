using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using Barotrauma;
using HarmonyLib;

namespace ReboundGuard
{
    // Native OnExiting disposed LuaCs before Client.Quit restored/unloaded content.
    // Defer that one Dispose to immediately after Quit, before Steam shutdown.
    internal static class ExitLifetimePatch
    {
        [ThreadStatic] private static LuaCsSetup pending;
        private static readonly MethodInfo Original = AccessTools.Method(typeof(GameMain), "OnExiting");
        internal static void Install(Harmony harmony)
        {
            if (!Settings.FixExitDisposalOrder || !SupportedCodeMatches() || PatchSafety.HasForeignPatch(Original))
            { Plugin.Log("exit lifetime fix not installed: disabled, unknown code or conflicting patch"); return; }
            harmony.Patch(Original, transpiler: new HarmonyMethod(typeof(ExitLifetimePatch), nameof(Transpiler)),
                finalizer: new HarmonyMethod(typeof(ExitLifetimePatch), nameof(FlushDeferred)));
            Plugin.Log("exit lifetime fix installed: Client.Quit/content restoration before LuaCs.Dispose; before Steam shutdown");
        }
        internal static bool SupportedCodeMatches() => AuditedBuild.CurrentGame || Original?.GetMethodBody() != null &&
            Convert.ToHexString(SHA256.HashData(Original.GetMethodBody().GetILAsByteArray())) == "AF417428D630C0AF1487F63ED7C0360DE609F05B892080EF8F181BC8F1B62B28";
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int[] dispose = Enumerable.Range(0, code.Count).Where(i => code[i].operand is MethodInfo method && method.DeclaringType == typeof(LuaCsSetup) && method.Name == "Dispose").ToArray();
            int[] quit = Enumerable.Range(0, code.Count).Where(i => code[i].operand is MethodInfo method && method.DeclaringType?.FullName == "Barotrauma.Networking.GameClient" && method.Name == "Quit").ToArray();
            int[] steam = Enumerable.Range(0, code.Count).Where(i => code[i].operand is MethodInfo method && method.DeclaringType?.Name == "SteamManager" && method.Name == "ShutDown").ToArray();
            if (dispose.Length != 1 || quit.Length != 1 || steam.Length != 1 || dispose[0] >= quit[0] || quit[0] >= steam[0])
                throw new InvalidOperationException("Unknown client exit lifetime sequence; no partial patch");
            code[dispose[0]].opcode = OpCodes.Call;
            code[dispose[0]].operand = AccessTools.Method(typeof(ExitLifetimePatch), nameof(DeferDispose));
            var flush = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ExitLifetimePatch), nameof(FlushDeferred)));
            flush.labels.AddRange(code[steam[0]].labels); code[steam[0]].labels.Clear();
            flush.blocks.AddRange(code[steam[0]].blocks); code[steam[0]].blocks.Clear();
            code.Insert(steam[0], flush);
            return code;
        }
        private static void DeferDispose(LuaCsSetup instance) { pending = instance; }
        private static void FlushDeferred()
        {
            LuaCsSetup instance = pending; pending = null;
            if (instance == null) return;
            try { instance.Dispose(); }
            catch (Exception error) { DebugConsole.ThrowError("Error while disposing of LuaCsForBarotrauma: " + error.Message + " | " + error.StackTrace); }
        }
    }
}
