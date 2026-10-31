using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Compatibility;
using MoonSharp.Interpreter;
using ReboundGuard;

namespace ReboundGuardTests
{
    public static class LuaDispatchSemanticsTests
    {
        private static int checks, stored, operatorCalls, foreignFactoryCalls;
        private static readonly List<string> Errors = new List<string>();
        private static readonly MethodInfo TargetMethod = AccessTools.Method(typeof(LuaDispatchSemanticsTests), nameof(Target));
        private static readonly MethodInfo Register = AccessTools.Method(typeof(LuaPatcherService), "Patch",
            new[] { typeof(string), typeof(MethodBase), typeof(LuaCsPatchFunc), typeof(ILuaCsHook.HookMethodType) });
        private static Type Optimizer => typeof(Plugin).Assembly.GetType("ReboundGuard.LuaBridgeOptimizer");
        private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); checks++; }
        [MethodImpl(MethodImplOptions.NoInlining)] public static int Target(int value, ref int changed) { changed += 3; return value + changed; }
        [MethodImpl(MethodImplOptions.NoInlining)] public static ref int RefReturn() => ref stored;
        public readonly struct ConversionTarget
        {
            public readonly int Value;
            public ConversionTarget(int value) { Value = value; }
        }
        public readonly struct ConversionSource
        {
            public readonly int Value;
            public ConversionSource(int value) { Value = value; }
            public static implicit operator ConversionTarget(ConversionSource value)
            {
                operatorCalls++;
                if (value.Value < 0) throw new InvalidOperationException("operator fixture");
                return new ConversionTarget(value.Value + 10);
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)] public static int Converted(ConversionTarget value) => value.Value;
        [MethodImpl(MethodImplOptions.NoInlining)] public static void Strings(string value, ref string changed) { changed += value; }
        private static ConversionTarget AlternateConversion(ConversionSource value) => new ConversionTarget(value.Value + 100);
        private static bool OverrideConversion(Type baseType, Type targetType, ref MethodInfo __result)
        {
            if (baseType != typeof(ConversionSource) || targetType != typeof(ConversionTarget)) return true;
            __result = AccessTools.Method(typeof(LuaDispatchSemanticsTests), nameof(AlternateConversion)); return false;
        }
        private static void ObserveForeignFactory() { foreignFactoryCalls++; }
        private static bool SuppressClear() => false;
        private static bool LogError(Exception __0) { Errors.Add(__0.GetType().Name + ":" + __0.Message); return false; }
        private static void RegisterPatch(LuaPatcherService service, string name, LuaCsPatchFunc callback, bool prefix)
        {
            Register.Invoke(service, new object[] { name, TargetMethod, callback, prefix ? ILuaCsHook.HookMethodType.Before : ILuaCsHook.HookMethodType.After });
            LuaPatcherService.instance = service;
        }
        private static string Scenario(bool optimized)
        {
            var harmony = new Harmony("barotrauma.reboundguard.experimental.v040");
            var service = new LuaPatcherService();
            var trace = new List<string>();
            bool compile = Settings.DirectLuaPatchFactory;
            try
            {
                Settings.DirectLuaPatchFactory = true;
                if (optimized) AccessTools.Method(Optimizer, "Install").Invoke(null, new object[] { harmony });
                harmony.Patch(AccessTools.Method(typeof(LuaCsLogger), "HandleException"),
                    prefix: new HarmonyMethod(typeof(LuaDispatchSemanticsTests), nameof(LogError)));
                void Reset() { service.Reset(); LuaPatcherService.instance = service; Errors.Clear(); }
                void Invoke(string name, int value = 5)
                {
                    int changed = 2; int result = Target(value, ref changed);
                    trace.Add(name + ":" + result + ":" + changed + ":" + string.Join(",", Errors));
                }

                // ParameterTable return and DynValue return both exist: inspect
                // actual native behavior rather than inferring source intent.
                RegisterPatch(service, "a", (instance, table) => { trace.Add("a"); table.ReturnValue = 31; return DynValue.NewNumber(41); }, false);
                RegisterPatch(service, "b", (instance, table) => { trace.Add("b"); return DynValue.NewNumber(43); }, false);
                Invoke("return-precedence"); Reset();

                RegisterPatch(service, "prevent", (instance, table) => {
                    table["value"] = 9; table["changed"] = 7; table.PreventExecution = true;
                    table.ReturnValue = 71; return DynValue.NewNumber(73);
                }, true);
                Invoke("prevent-and-result"); Reset();

                RegisterPatch(service, "throws", (instance, table) => { table["value"] = 99; throw new InvalidOperationException("callback fixture"); }, true);
                Invoke("callback-throws-before-writeback"); Reset();

                RegisterPatch(service, "bad-write", (instance, table) => { table["value"] = "invalid integer"; table["changed"] = 91; return DynValue.Void; }, true);
                Invoke("bad-argument-write"); Reset();

                RegisterPatch(service, "add-during-iteration", (instance, table) => {
                    trace.Add("add");
                    var entry = service.registeredPatches[LuaPatcherService.MethodKey.Create(TargetMethod)];
                    entry.Prefixes["late"] = new LuaPatcherService.LuaCsPatch { Identifier = "late", PatchFunc = (i, p) => { trace.Add("late"); return DynValue.Void; } };
                    return DynValue.Void;
                }, true);
                RegisterPatch(service, "second", (instance, table) => { trace.Add("second"); return DynValue.Void; }, true);
                Invoke("add-invalidates-enumerator"); Reset();

                RegisterPatch(service, "remove-during-iteration", (instance, table) => {
                    trace.Add("remove");
                    service.registeredPatches[LuaPatcherService.MethodKey.Create(TargetMethod)].Prefixes.Remove("second");
                    return DynValue.Void;
                }, true);
                RegisterPatch(service, "second", (instance, table) => { trace.Add("second"); return DynValue.Void; }, true);
                Invoke("remove-during-enumeration"); Reset();

                LuaPatcherService.ParameterTable retained = null;
                RegisterPatch(service, "retain", (instance, table) => { retained ??= table; table["value"] = (int)table["value"] + 1; return DynValue.Void; }, true);
                Invoke("retain-first"); retained["value"] = 150;
                Invoke("retain-next-is-independent");
                trace.Add("retained=" + retained["value"] + "/" + retained.OriginalParameters["value"]); Reset();

                int depth = 0;
                RegisterPatch(service, "recursive", (instance, table) => {
                    trace.Add("depth=" + depth);
                    if (depth++ == 0) { int inner = 1; trace.Add("inner=" + Target(7, ref inner) + "/" + inner); }
                    depth--; table["changed"] = (int)table["changed"] + 2; return DynValue.Void;
                }, true);
                Invoke("recursive-call"); Reset();

                RegisterPatch(service, "reset-in-callback", (instance, table) => {
                    trace.Add("reset"); service.Reset(); table["value"] = 17; return DynValue.Void;
                }, true);
                Invoke("reset-during-dispatch"); Invoke("after-reset"); Reset();

                var script = new Script();
                DynValue callback = script.DoString("return function(instance, p) return p.OriginalReturnValue + 4 end");
                RegisterPatch(service, "real-lua", (instance, table) => script.Call(callback, instance, table), false);
                Invoke("real-moonsharp-callback"); Reset();
                RegisterPatch(service, "explicit-nil", (instance, table) => DynValue.Nil, true);
                Invoke("native-explicit-nil-conversion"); Reset();

                MethodInfo converted = AccessTools.Method(typeof(LuaDispatchSemanticsTests), nameof(Converted));
                int sourceValue = 5; operatorCalls = 0;
                Register.Invoke(service, new object[] { "conversion", converted, (LuaCsPatchFunc)((instance, table) => {
                    table["value"] = new ConversionSource(sourceValue); return DynValue.Void;
                }), ILuaCsHook.HookMethodType.Before });
                LuaPatcherService.instance = service;
                for (int i = 0; i < 3; i++) { sourceValue = i + 5; trace.Add("operator=" + Converted(default)); }
                trace.Add("operator-calls=" + operatorCalls);
                var foreign = new Harmony("reboundguard.conversion.foreign");
                try
                {
                    foreign.Patch(AccessTools.Method(typeof(SigilExtensions), "GetImplicitOperatorMethod"),
                        prefix: new HarmonyMethod(typeof(LuaDispatchSemanticsTests), nameof(OverrideConversion)));
                    trace.Add("late-operator-patch=" + Converted(default));
                }
                finally { foreign.UnpatchSelf(); }
                trace.Add("operator-after-unpatch=" + Converted(default));
                sourceValue = -1; trace.Add("throwing-operator=" + Converted(new ConversionTarget(19)) + ":" + string.Join(",", Errors)); Reset();

                Register.Invoke(service, new object[] { "null-write", AccessTools.Method(typeof(LuaDispatchSemanticsTests), nameof(Strings)),
                    (LuaCsPatchFunc)((instance, table) => { table["value"] = null; table["changed"] = "modified"; return DynValue.Void; }), ILuaCsHook.HookMethodType.Before });
                LuaPatcherService.instance = service;
                string text = "original"; Strings("value", ref text);
                trace.Add("null-argument=" + text + ":" + string.Join(",", Errors)); Reset();

                if (optimized)
                {
                    var compiler = typeof(Plugin).Assembly.GetType("ReboundGuard.LuaDirectBridge");
                    Check(!(bool)AccessTools.Method(compiler, "Supports").Invoke(null, new object[] { AccessTools.Method(typeof(LuaDispatchSemanticsTests), nameof(RefReturn)) }),
                        "By-reference return uses the native factory fallback");
                }
                return string.Join("|", trace);
            }
            finally
            {
                AccessTools.Method(Optimizer, "Stop").Invoke(null, null); service.Dispose(); harmony.UnpatchSelf();
                Settings.DirectLuaPatchFactory = compile;
            }
        }
        public static object Run()
        {
            string native = Scenario(false), compiled = Scenario(true);
            Check(native == compiled, "Native/compiled callback behavior differs:\nnative=" + native + "\ncompiled=" + compiled);
            Check(native.Contains("real-moonsharp-callback:14:5:"), "Real Lua callback must execute rather than silently fall back: " + native);
            Check(native.Contains("operator-calls=3") && native.Contains("late-operator-patch=107"), "Conversions execute each time and observe later foreign patches");
            CheckFactoryConflicts();
            return new { checks, behavior = native, note = "Differential tests against real LuaCs: return precedence, callback/argument exceptions, live registration changes, retained tables, recursion, in-callback Reset and a real MoonSharp callback. No live game round." };
        }
        private static void CheckFactoryConflicts()
        {
            var harmony = new Harmony("barotrauma.reboundguard.experimental.v040");
            var foreign = new Harmony("reboundguard.factory.foreign");
            var service = new LuaPatcherService();
            try
            {
                AccessTools.Method(Optimizer, "Install").Invoke(null, new object[] { harmony });
                RegisterPatch(service, "initial", (instance, table) => DynValue.Void, true);
                var initial = service.registeredPatches[LuaPatcherService.MethodKey.Create(TargetMethod)];
                Check(initial.HarmonyPrefixMethod.DeclaringType.Name.StartsWith("DirectBridge"), "Direct wrapper installed before conflict");
                object module = service.patchModuleBuilder;
                foreign.Patch(AccessTools.Method(typeof(LuaPatcherService), "ClearAll"),
                    prefix: new HarmonyMethod(typeof(LuaDispatchSemanticsTests), nameof(SuppressClear)));
                service.Reset(); LuaPatcherService.instance = service;
                Check(service.registeredPatches.Count == 1 && ReferenceEquals(module, service.patchModuleBuilder), "Suppressed foreign ClearAll does not discard live bridge state/module");
                AccessTools.Method(Optimizer, "Stop").Invoke(null, null);
                Check(initial.HarmonyPrefixMethod.DeclaringType.Name.StartsWith("Patch_"), "Live bridge still restores after a foreign-suppressed clear");
                foreign.UnpatchSelf(); service.Reset();

                foreignFactoryCalls = 0;
                foreign.Patch(AccessTools.Method(typeof(LuaPatcherService), "CreateDynamicHarmonyPatch"),
                    prefix: new HarmonyMethod(typeof(LuaDispatchSemanticsTests), nameof(ObserveForeignFactory)));
                RegisterPatch(service, "foreign", (instance, table) => DynValue.Void, true);
                var fallback = service.registeredPatches[LuaPatcherService.MethodKey.Create(TargetMethod)];
                Check(foreignFactoryCalls == 2 && fallback.HarmonyPrefixMethod.GetParameters().Any(parameter => parameter.Name == "__originalMethod"),
                    "Later foreign factory patch receives both native factory calls");
            }
            finally
            {
                foreign.UnpatchSelf(); AccessTools.Method(Optimizer, "Stop").Invoke(null, null);
                service.Dispose(); harmony.UnpatchSelf();
            }
        }
    }
}
