using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace ReboundGuard
{
    internal static class LoadingLifecyclePatch
    {
        private static FieldInfo ownerField, stateField;
        internal static void Install(Harmony harmony)
        {
            if (!Settings.FixReferenceEnumerationLifetime) return;
            MethodInfo getter = AccessTools.PropertyGetter(typeof(AssemblyLoader), "AssemblyReferences");
            Type iterator = getter?.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
            MethodInfo move = iterator == null ? null : AccessTools.Method(iterator, "MoveNext");
            if (getter == null || move == null) return;
            string getterHash = Hash(getter), moveHash = Hash(move);
            bool known = (getterHash == "1E32B9D8EAFC563B3CFC28B66492E50EFF2C85B1E38CFBAFBA00365958933095" &&
                          moveHash == "56513853E098403628ACF56B223D7D60F6AA166713D866D2EDD470AA2098CC5A") ||
                         (getterHash == "7F911CC19640736421A1C8AB1E2ACC93C73EE536497CC475489C81D07648DB50" &&
                          moveHash == "3C9BF56EFDC0505877864F43E3D31955DE3AF886E5A40B882847C55B0F472D6F");
            MethodInfo dispose = AccessTools.Method(iterator, "System.IDisposable.Dispose");
            if (dispose != null)
            {
                string disposeHash = Hash(dispose);
                known &= disposeHash == "FB38E821D118062141EBF7B486D125A1499972F4196FBBEF7276987032236EF6" ||
                    disposeHash == "CEF37D46058E50D6A2B248D1794E3C56CC35F1328A902C90F2BA50A03EFCF8B8";
            }
            known |= AuditedBuild.CurrentGame;
            ownerField = AccessTools.Field(iterator, "<>4__this");
            stateField = AccessTools.Field(iterator, "<>1__state");
            if (!known || dispose == null || ownerField == null || stateField == null ||
                PatchSafety.HasForeignPatch(getter) || PatchSafety.HasForeignPatch(move) || PatchSafety.HasForeignPatch(dispose))
            { Plugin.Log("reference enumeration lifetime fix not installed: unknown code or competing patch"); return; }
            // The tiny getter can be inlined into already compiled consumers.
            // Patch the actual iterator methods instead of relying on that getter.
            harmony.Patch(dispose, prefix: new HarmonyMethod(typeof(LoadingLifecyclePatch), nameof(BeforeDispose)),
                finalizer: new HarmonyMethod(typeof(LoadingLifecyclePatch), nameof(AfterDispose)));
            Plugin.Log("reference enumeration lifetime fix installed: operation count released on early iterator disposal");
        }
        private static string Hash(MethodInfo method) => Convert.ToHexString(SHA256.HashData(method.GetMethodBody().GetILAsByteArray()));
        private static void BeforeDispose(object __instance, out AssemblyLoader __state)
        {
            int state = (int)stateField.GetValue(__instance);
            // Trusted compiler state machine: 2 = yielded reference; -3 = in
            // protected foreach. State 1 is the sentinel before incrementing.
            __state = state == 2 || state == -3 ? (AssemblyLoader)ownerField.GetValue(__instance) : null;
        }
        private static void AfterDispose(AssemblyLoader __state)
        {
            // Original Dispose still releases the inner enumerator and sets
            // its completed state. This adds only the missing counter release.
            if (__state != null) __state.AreOperationRunning = false;
        }
    }
}
