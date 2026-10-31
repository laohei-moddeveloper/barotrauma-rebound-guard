using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ReboundGuard
{
    internal static class FeatureInstaller
    {
        private static IEnumerable<(MethodBase Original, MethodInfo Patch)> OwnPatches()
        {
            foreach (MethodBase original in PatchProcessor.GetAllPatchedMethods().ToArray())
            {
                Patches info = Harmony.GetPatchInfo(original);
                if (info == null) continue;
                foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.ILManipulators))
                    if (patch.owner == Plugin.HarmonyId) yield return (original, patch.PatchMethod);
            }
        }

        // A changed API/transpiler in one optional feature must not remove every
        // already installed optimization. Roll back only this installation's work.
        internal static bool TryInstall(Harmony harmony, string name, Action install, Action reset = null)
        {
            var previous = OwnPatches().ToHashSet();
            try { install(); return true; }
            catch (Exception error)
            {
                foreach (var patch in OwnPatches().Where(patch => !previous.Contains(patch)).ToArray())
                    harmony.Unpatch(patch.Original, patch.Patch);
                reset?.Invoke();
                Plugin.Log("feature fallback: " + name + "; " + error.GetType().Name + ": " + error.Message +
                    "; partial feature patches removed; other features retained");
                return false;
            }
        }
    }
}
