using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ReboundGuard;

namespace ReboundGuardTests
{
    public static class CompatibilityTests
    {
        private static int checks;
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Target() { }
        private static void ExistingPrefix() { }
        private static void FailedPostfix() { }
        private static void ForeignPrefix() { }
        public static object Run()
        {
            var assembly = typeof(Plugin).Assembly;
            var build = assembly.GetType("ReboundGuard.AuditedBuild", true);
            Check((bool)AccessTools.PropertyGetter(build, "CurrentGame").Invoke(null, null), "Exact current native game/core binaries recognized");
            Check((bool)AccessTools.PropertyGetter(build, "CurrentPhysics").Invoke(null, null), "Exact current physics binary recognized");
            foreach (Type type in new[] { typeof(BodyMaintenance), typeof(ContainerEffectFastPath), typeof(NearbyItemIndex), typeof(ParallelConstraintPreparation) })
                Check((bool)AccessTools.Method(type, "SupportedCodeMatches").Invoke(null, null), "Unsupported current feature: " + type.Name);
            const string owner = "barotrauma.reboundguard.experimental.v040";
            var harmony = new Harmony(owner); var foreign = new Harmony("reboundguard.compatibility.foreign");
            MethodInfo target = AccessTools.Method(typeof(CompatibilityTests), nameof(Target));
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(CompatibilityTests), nameof(ExistingPrefix)));
                foreign.Patch(target, prefix: new HarmonyMethod(typeof(CompatibilityTests), nameof(ForeignPrefix)));
                Action fail = () => {
                    harmony.Patch(target, postfix: new HarmonyMethod(typeof(CompatibilityTests), nameof(FailedPostfix)));
                    throw new MissingMethodException("Simulated updated API");
                };
                bool reset = false;
                var installer = AccessTools.Method(assembly.GetType("ReboundGuard.FeatureInstaller", true), "TryInstall");
                Check(!(bool)installer.Invoke(null, new object[] { harmony, "deliberate failure", fail, (Action)(() => reset = true) }), "Failure reported");
                var info = Harmony.GetPatchInfo(target);
                Check(reset, "Failed feature state reset");
                Check(info.Prefixes.Any(patch => patch.owner == owner), "Previously installed own feature retained");
                Check(info.Prefixes.Any(patch => patch.owner == foreign.Id), "Foreign patch retained");
                Check(!info.Postfixes.Any(patch => patch.owner == owner), "Partial failing feature removed");
            }
            finally { harmony.UnpatchSelf(); foreign.UnpatchSelf(); }
            object lifecycle = PluginLifecycleTests.Run();
            return new { checks, lifecycle, limitation = "Exact downloaded Windows LuaCs native assemblies, managed methods under this host runtime; no live rendering/networking session." };
        }
    }
}
