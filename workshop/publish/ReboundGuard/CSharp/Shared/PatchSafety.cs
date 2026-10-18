using System.Collections.ObjectModel;
using System.Reflection;
using HarmonyLib;

namespace ReboundGuard
{
    internal static class PatchSafety
    {
        // Patches.Owners builds a HashSet, five LINQ projections and a list.
        // Inspect the same five groups directly; never cache across steps.
        internal static bool HasForeignPatch(MethodBase method)
        {
            if (method == null) return true;
            return HasForeignPatch(Harmony.GetPatchInfo(method));
        }
        internal static bool HasForeignPatch(Patches patches) => patches != null &&
            (Foreign(patches.Prefixes) || Foreign(patches.Postfixes) || Foreign(patches.Transpilers) ||
             Foreign(patches.Finalizers) || Foreign(patches.ILManipulators));
        private static bool Foreign(ReadOnlyCollection<Patch> patches)
        {
            for (int i = 0; i < patches.Count; i++) if (patches[i].owner != Plugin.HarmonyId) return true;
            return false;
        }
    }
}
