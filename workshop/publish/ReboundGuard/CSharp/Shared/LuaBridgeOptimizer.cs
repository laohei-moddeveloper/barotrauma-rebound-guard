using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Compatibility;
using HarmonyLib;

namespace ReboundGuard
{
    // Generic LuaCs patch scaffolding optimization. The original generated body
    // still dispatches all callbacks and applies all parameter/result changes.
    internal static class LuaBridgeOptimizer
    {
        private sealed class State
        {
            internal LuaPatcherService.MethodKey Key;
            internal bool Prefix;
            internal MethodInfo Generated;
            internal MethodInfo Bridge;
            internal MethodBase Target;
            internal string Identifier;
            internal LuaPatcherService.PatchedMethod GetCallbacks()
            {
                // Read the live registry each call: removals/re-additions and
                // reloads must not leave a cached callback dictionary in use.
                var service = LuaPatcherService.instance;
                if (!service.registeredPatches.TryGetValue(Key, out var entry)) return null;
                if ((Prefix ? entry.Prefixes.Count : entry.Postfixes.Count) != 0) return entry;
                Interlocked.Increment(ref emptyCallsAvoided);
                return null;
            }
            internal bool HasCallbacks()
            {
                try
                {
                    LuaPatcherService service = LuaPatcherService.instance;
                    if (service == null) return true; // retain native error handling
                    if (!service.registeredPatches.TryGetValue(Key, out var entry)) return false;
                    bool active = Prefix ? entry.Prefixes.Count != 0 : entry.Postfixes.Count != 0;
                    if (!active) Interlocked.Increment(ref emptyCallsAvoided);
                    return active;
                }
                catch { return true; }
            }
        }
        private sealed class Context
        {
            internal ModuleBuilder Module;
            internal readonly List<State> States = new List<State>();
            internal readonly HashSet<string> AccessGrants = new HashSet<string>(StringComparer.Ordinal);
        }
        private static readonly ConditionalWeakTable<LuaPatcherService, Context> Contexts = new ConditionalWeakTable<LuaPatcherService, Context>();
        private static readonly MethodInfo Factory = AccessTools.Method(typeof(LuaPatcherService), "CreateDynamicHarmonyPatch");
        private static readonly MethodInfo ClearAll = AccessTools.Method(typeof(LuaPatcherService), "ClearAll");
        private static long wrappersBuilt, directWrappersBuilt, emptyCallsAvoided, resets;
        [ThreadStatic] private static bool restoringNative;
        internal static void Install(Harmony harmony)
        {
            if (!Settings.OptimizeLuaBridge || Factory == null || ClearAll == null) return;
            // The factory is checked independently of unrelated GameScreen code.
            string hash = Convert.ToHexString(SHA256.HashData(Factory.GetMethodBody().GetILAsByteArray()));
            bool supported = hash == "FDDE4BAB2C7F247FFAFFCEFF29FC9E60838E7BC8CEC9FD9B48516EDDE3CA30BE" ||
                hash == "D3CD997DE4AF581816D86F5F6A6E809FAD18B4EB8E3B456B9A92C50A7BBB71B7" ||
                // The installed LuaCs factory has the same resolved instructions
                // and locals as the audited builds; only raw metadata tokens differ.
                hash == "F75580569D4AF4C2D30753CCCA54BBFFC76DD44A5EFD3E0BC934C38CB1C23D2A" ||
                hash == "CD852EB93E71D235BD698AFE705B0915692FDFE8C6506B106A963021787E8E2F";
            string clearHash = Convert.ToHexString(SHA256.HashData(ClearAll.GetMethodBody().GetILAsByteArray()));
            supported &= clearHash == "1A598A5252DA28324FDAD98DA2FF72DC33FF2CD373A90C7D1282471DA024B057" ||
                clearHash == "A9CEFCABE087A9FCDE7CBCF3A4F3CED4907590FD9545766D1243AB5F83C4D41F";
            supported |= AuditedBuild.CurrentGame;
            if (!supported || PatchSafety.HasForeignPatch(Factory) || PatchSafety.HasForeignPatch(ClearAll))
            { Plugin.Log("Lua bridge optimization not installed: unsupported factory or competing patch; factory_sha256=" + hash); return; }
            harmony.Patch(Factory, prefix: new HarmonyMethod(typeof(LuaBridgeOptimizer), nameof(BeforeFactory)),
                postfix: new HarmonyMethod(typeof(LuaBridgeOptimizer), nameof(AfterFactory)));
            harmony.Patch(ClearAll, postfix: new HarmonyMethod(typeof(LuaBridgeOptimizer), nameof(AfterClear)));
            Plugin.Log("generic Lua bridge optimization installed; direct_factory=" + Settings.DirectLuaPatchFactory + "; callbacks and independent parameter tables retained");
        }
        private static bool BeforeFactory(LuaPatcherService __instance, string identifier, MethodBase original, ILuaCsHook.HookMethodType hookType, ref MethodInfo __result)
        {
            if (restoringNative || !Settings.OptimizeLuaBridge || !Settings.DirectLuaPatchFactory || !LuaDirectBridge.Supports(original)) return true;
            if (PatchSafety.HasForeignPatch(Factory) || PatchSafety.HasForeignPatch(ClearAll)) return true;
            try
            {
                Context context = Contexts.GetValue(__instance, _ => new Context());
                EnsureModule(context);
                var types = original.GetParameters().Select(p => p.ParameterType)
                    .Append((original as MethodInfo)?.ReturnType).Append(original.DeclaringType)
                    .Append(typeof(LuaDirectBridge)).Append(typeof(LuaPatcherService));
                foreach (Type type in types) GrantAccess(context, type);
                var state = new State { Key = LuaPatcherService.MethodKey.Create(original), Prefix = hookType == ILuaCsHook.HookMethodType.Before,
                    Target = original, Identifier = identifier };
                MethodInfo bridge = LuaDirectBridge.Build(context.Module, original, state.Prefix, state.GetCallbacks);
                state.Bridge = bridge; context.States.Add(state);
                __result = bridge;
                Interlocked.Increment(ref wrappersBuilt); Interlocked.Increment(ref directWrappersBuilt);
                return false;
            }
            catch (Exception error)
            {
                Plugin.Log("direct Lua factory fallback: " + error.GetType().Name + "; " + original.Name);
                return true;
            }
        }
        private static void EnsureModule(Context context)
        {
            if (context.Module != null) return;
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ReboundGuard.LuaBridge." + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
            context.Module = assembly.DefineDynamicModule("Bridge");
        }
        private static void GrantAccess(Context context, Type type)
        {
            if (type == null) return;
            if (type.HasElementType) { GrantAccess(context, type.GetElementType()); return; }
            string name = type.Assembly.GetName().Name;
            if (context.AccessGrants.Add(name))
            {
                try
                {
                    var access = typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).GetConstructor(new[] { typeof(string) });
                    ((AssemblyBuilder)context.Module.Assembly).SetCustomAttribute(new CustomAttributeBuilder(access, new object[] { name }));
                }
                catch { context.AccessGrants.Remove(name); throw; }
            }
            if (type.IsGenericType) foreach (Type argument in type.GetGenericArguments()) GrantAccess(context, argument);
        }
        private static void AfterFactory(LuaPatcherService __instance, MethodBase original, ILuaCsHook.HookMethodType hookType, ref MethodInfo __result)
        {
            if (restoringNative || Settings.DirectLuaPatchFactory || !Settings.OptimizeLuaBridge || __result == null ||
                PatchSafety.HasForeignPatch(Factory) || PatchSafety.HasForeignPatch(ClearAll)) return;
            MethodInfo generated = __result;
            try { __result = Build(__instance, original, generated, hookType == ILuaCsHook.HookMethodType.Before); }
            catch (Exception error) { __result = generated; Plugin.Log("Lua bridge factory fallback: " + error.GetType().Name + "; " + original.Name); }
        }
        private static MethodInfo Build(LuaPatcherService service, MethodBase original, MethodInfo generated, bool prefix)
        {
            ParameterInfo[] old = generated.GetParameters();
            if (old.Length < 2 || old[0].Name != "__originalMethod" || old[0].ParameterType != typeof(MethodBase)) return generated;
            Type resultType = (original as MethodInfo)?.ReturnType;
            if (resultType != null && (resultType.IsByRef || resultType.IsPointer || resultType.IsByRefLike)) return generated;
            Context context = Contexts.GetValue(service, _ => new Context());
            EnsureModule(context);
            var state = new State { Key = LuaPatcherService.MethodKey.Create(original), Prefix = prefix, Generated = generated, Target = original };
            var access = typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).GetConstructor(new[] { typeof(string) });
            foreach (string name in old.Select(parameter => (parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType).Assembly.GetName().Name)
                .Append(resultType?.Assembly.GetName().Name).Append(typeof(LuaPatcherService).Assembly.GetName().Name).Append(original.DeclaringType?.Assembly.GetName().Name).Where(name => name != null).Distinct())
            {
                // These attributes belong to the assembly, not each wrapper.
                // Re-emitting the same grant for every hook grows metadata and
                // repeats reflection work while loading large mod combinations.
                if (context.AccessGrants.Contains(name)) continue;
                ((AssemblyBuilder)context.Module.Assembly).SetCustomAttribute(new CustomAttributeBuilder(access, new object[] { name }));
                context.AccessGrants.Add(name); // Only cache a successful emission.
            }
            TypeBuilder type = context.Module.DefineType("Bridge" + Guid.NewGuid().ToString("N"), TypeAttributes.Public);
            FieldBuilder metadata = type.DefineField("Original", typeof(MethodBase), FieldAttributes.Public | FieldAttributes.Static);
            FieldBuilder guard = type.DefineField("Guard", typeof(Func<bool>), FieldAttributes.Public | FieldAttributes.Static);
            // LuaCs.ClearAll expects this field on every registered wrapper type.
            type.DefineField("LuaCs", typeof(Barotrauma.LuaCsSetup), FieldAttributes.Public | FieldAttributes.Static);
            Type[] signature = old.Skip(1).Select(parameter => parameter.Name == "__result" ? resultType.MakeByRefType() : parameter.ParameterType).ToArray();
            if (signature.Any(parameter => (parameter.IsByRef ? parameter.GetElementType() : parameter).IsByRefLike)) return generated;
            MethodBuilder method = type.DefineMethod(prefix ? "HarmonyPrefix" : "HarmonyPostfix", MethodAttributes.Public | MethodAttributes.Static, generated.ReturnType, signature);
            for (int i = 1; i < old.Length; i++) method.DefineParameter(i, old[i].Attributes, old[i].Name);
            ILGenerator il = method.GetILGenerator(); Label active = il.DefineLabel();
            il.Emit(OpCodes.Ldsfld, guard); il.Emit(OpCodes.Callvirt, typeof(Func<bool>).GetMethod("Invoke")); il.Emit(OpCodes.Brtrue, active);
            if (prefix) il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret); il.MarkLabel(active);
            int result = Array.FindIndex(old, parameter => parameter.Name == "__result");
            LocalBuilder boxed = null, returned = prefix ? il.DeclareLocal(typeof(bool)) : null;
            if (result >= 0)
            {
                boxed = il.DeclareLocal(typeof(object));
                il.Emit(OpCodes.Ldarg, result - 1); il.Emit(OpCodes.Ldobj, resultType);
                if (resultType.IsValueType) il.Emit(OpCodes.Box, resultType);
                il.Emit(OpCodes.Stloc, boxed);
            }
            il.Emit(OpCodes.Ldsfld, metadata);
            for (int i = 1; i < old.Length; i++)
            {
                if (i == result) il.Emit(OpCodes.Ldloca, boxed);
                else il.Emit(OpCodes.Ldarg, i - 1);
            }
            il.Emit(OpCodes.Call, generated);
            if (prefix) il.Emit(OpCodes.Stloc, returned);
            if (result >= 0)
            {
                il.Emit(OpCodes.Ldarg, result - 1); il.Emit(OpCodes.Ldloc, boxed);
                il.Emit(resultType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, resultType);
                il.Emit(OpCodes.Stobj, resultType);
            }
            if (prefix) il.Emit(OpCodes.Ldloc, returned);
            il.Emit(OpCodes.Ret);
            Type built = type.CreateType();
            built.GetField("Original").SetValue(null, original);
            built.GetField("Guard").SetValue(null, (Func<bool>)state.HasCallbacks);
            built.GetField("LuaCs").SetValue(null, Barotrauma.LuaCsSetup.Instance);
            context.States.Add(state); Interlocked.Increment(ref wrappersBuilt);
            state.Bridge = built.GetMethod(method.Name);
            return state.Bridge;
        }
        private static void AfterClear(LuaPatcherService __instance, bool __runOriginal)
        {
            if (!__runOriginal || PatchSafety.HasForeignPatch(ClearAll)) return;
            if (Contexts.TryGetValue(__instance, out Context context))
            {
                foreach (State state in context.States) state.Generated?.DeclaringType.GetField("LuaCs")?.SetValue(null, null);
                Contexts.Remove(__instance); Interlocked.Increment(ref resets);
            }
            if (Settings.RecycleLuaPatchModules)
                __instance.patchModuleBuilder = new Lazy<ModuleBuilder>(__instance.CreateModuleBuilder);
        }
        internal static void Stop()
        {
            foreach (var pair in Contexts.ToArray())
            {
                LuaPatcherService service = pair.Key;
                foreach (State state in pair.Value.States)
                {
                    if (!service.registeredPatches.TryGetValue(state.Key, out var entry)) continue;
                    MethodInfo current = state.Prefix ? entry.HarmonyPrefixMethod : entry.HarmonyPostfixMethod;
                    if (current != state.Bridge) continue;
                    if (state.Generated == null)
                    {
                        // Native generation is deferred until detaching, instead
                        // of generating TWO wrapper types for every initial hook.
                        bool previous = restoringNative; restoringNative = true;
                        try { state.Generated = (MethodInfo)Factory.Invoke(service, new object[] { state.Identifier, state.Target,
                            state.Prefix ? ILuaCsHook.HookMethodType.Before : ILuaCsHook.HookMethodType.After }); }
                        finally { restoringNative = previous; }
                    }
                    // Restore native wrappers without touching any callback dictionary.
                    var info = Harmony.GetPatchInfo(state.Target);
                    Patch oldPatch = (state.Prefix ? info?.Prefixes : info?.Postfixes)?.FirstOrDefault(patch => patch.PatchMethod == state.Bridge);
                    if (oldPatch == null)
                    {
                        AccessTools.Field(typeof(LuaPatcherService.PatchedMethod), state.Prefix ? "<HarmonyPrefixMethod>k__BackingField" : "<HarmonyPostfixMethod>k__BackingField").SetValue(entry, state.Generated);
                        continue;
                    }
                    var restore = new HarmonyMethod(state.Generated);
                    if (oldPatch != null) { restore.priority = oldPatch.priority; restore.before = oldPatch.before; restore.after = oldPatch.after; }
                    service.harmony.Unpatch(state.Target, state.Bridge);
                    if (state.Prefix) service.harmony.Patch(state.Target, prefix: restore);
                    else service.harmony.Patch(state.Target, postfix: restore);
                    AccessTools.Field(typeof(LuaPatcherService.PatchedMethod), state.Prefix ? "<HarmonyPrefixMethod>k__BackingField" : "<HarmonyPostfixMethod>k__BackingField").SetValue(entry, state.Generated);
                }
                Contexts.Remove(service);
            }
        }
        internal static string Report() => "generic Lua bridge: wrappers_built=" + Interlocked.Exchange(ref wrappersBuilt, 0) +
            "; direct_wrappers_built=" + Interlocked.Exchange(ref directWrappersBuilt, 0) +
            "; empty_phases_avoided=" + Interlocked.Exchange(ref emptyCallsAvoided, 0) + "; contexts_released=" + Interlocked.Exchange(ref resets, 0);
    }
}
