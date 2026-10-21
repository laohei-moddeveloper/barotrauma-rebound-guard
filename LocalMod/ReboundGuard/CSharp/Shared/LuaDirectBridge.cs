using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using MoonSharp.Interpreter;

namespace ReboundGuard
{
    // Single-generation equivalent of the audited LuaCs factory. No pooling:
    // callbacks may retain ParameterTable and its dictionaries after returning.
    internal static class LuaDirectBridge
    {
        private static readonly MethodInfo PrefixEnumerator = AccessTools.Method(typeof(LuaPatcherService.PatchedMethod), "GetPrefixEnumerator");
        private static readonly MethodInfo PostfixEnumerator = AccessTools.Method(typeof(LuaPatcherService.PatchedMethod), "GetPostfixEnumerator");
        internal static bool Supports(MethodBase original)
        {
            if (original.ContainsGenericParameters) return false;
            Type result = (original as MethodInfo)?.ReturnType;
            if (result != null && (result.IsByRef || result.IsPointer || result.IsFunctionPointer || result.IsByRefLike)) return false;
            foreach (ParameterInfo parameter in original.GetParameters())
            {
                Type type = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType;
                if (type.IsByRefLike || type.IsPointer || type.IsFunctionPointer) return false;
            }
            return true;
        }
        internal static MethodInfo Build(ModuleBuilder module, MethodBase original, bool prefix,
            Func<LuaPatcherService.PatchedMethod> lookup)
        {
            ParameterInfo[] parameters = original.GetParameters();
            Type returnType = (original as MethodInfo)?.ReturnType;
            bool hasResult = returnType != null && returnType != typeof(void);
            int offset = hasResult ? 2 : 1;
            var signature = new Type[parameters.Length + offset];
            signature[0] = typeof(object);
            if (hasResult) signature[1] = returnType.MakeByRefType();
            for (int i = 0; i < parameters.Length; i++)
                signature[i + offset] = parameters[i].ParameterType.IsByRef ? parameters[i].ParameterType : parameters[i].ParameterType.MakeByRefType();
            TypeBuilder type = module.DefineType("DirectBridge" + Guid.NewGuid().ToString("N"), TypeAttributes.Public);
            FieldBuilder live = type.DefineField("Lookup", typeof(Func<LuaPatcherService.PatchedMethod>), FieldAttributes.Public | FieldAttributes.Static);
            FieldBuilder luaCs = type.DefineField("LuaCs", typeof(LuaCsSetup), FieldAttributes.Public | FieldAttributes.Static);
            FieldBuilder resultType = type.DefineField("ResultType", typeof(Type), FieldAttributes.Public | FieldAttributes.Static);
            MethodBuilder method = type.DefineMethod(prefix ? "HarmonyPrefix" : "HarmonyPostfix", MethodAttributes.Public | MethodAttributes.Static,
                prefix ? typeof(bool) : typeof(void), signature);
            method.DefineParameter(1, ParameterAttributes.None, "__instance");
            if (hasResult) method.DefineParameter(2, ParameterAttributes.None, "__result");
            for (int i = 0; i < parameters.Length; i++) method.DefineParameter(i + offset + 1, ParameterAttributes.None, parameters[i].Name);

            ILGenerator il = method.GetILGenerator();
            LocalBuilder proceed = il.DeclareLocal(typeof(bool)), active = il.DeclareLocal(typeof(bool));
            LocalBuilder patches = il.DeclareLocal(typeof(LuaPatcherService.PatchedMethod));
            LocalBuilder dictionary = il.DeclareLocal(typeof(Dictionary<string, object>));
            LocalBuilder table = il.DeclareLocal(typeof(LuaPatcherService.ParameterTable));
            LocalBuilder boxedResult = il.DeclareLocal(typeof(object)), modified = il.DeclareLocal(typeof(Dictionary<string, object>));
            LocalBuilder value = il.DeclareLocal(typeof(object)), exception = il.DeclareLocal(typeof(Exception));
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc, proceed);
            System.Reflection.Emit.Label end = il.BeginExceptionBlock();
            il.Emit(OpCodes.Ldsfld, live); il.Emit(OpCodes.Callvirt, typeof(Func<LuaPatcherService.PatchedMethod>).GetMethod("Invoke"));
            il.Emit(OpCodes.Stloc, patches);
            System.Reflection.Emit.Label found = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, patches); il.Emit(OpCodes.Brtrue, found); il.Emit(OpCodes.Leave, end); il.MarkLabel(found);
            if (hasResult)
            {
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldobj, returnType);
                if (returnType.IsValueType) il.Emit(OpCodes.Box, returnType);
                il.Emit(OpCodes.Stloc, boxedResult);
            }
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc, active);
            il.Emit(OpCodes.Ldc_I4, parameters.Length);
            il.Emit(OpCodes.Newobj, typeof(Dictionary<string, object>).GetConstructor(new[] { typeof(int) }));
            il.Emit(OpCodes.Stloc, dictionary);
            for (int i = 0; i < parameters.Length; i++)
            {
                Type parameterType = signature[i + offset].GetElementType();
                il.Emit(OpCodes.Ldloc, dictionary); il.Emit(OpCodes.Ldstr, parameters[i].Name);
                il.Emit(OpCodes.Ldarg, i + offset); il.Emit(OpCodes.Ldobj, parameterType);
                if (parameterType.IsValueType) il.Emit(OpCodes.Box, parameterType);
                il.Emit(OpCodes.Callvirt, typeof(Dictionary<string, object>).GetMethod("Add"));
            }
            il.Emit(OpCodes.Ldloc, dictionary);
            il.Emit(OpCodes.Newobj, typeof(LuaPatcherService.ParameterTable).GetConstructor(new[] { typeof(Dictionary<string, object>) }));
            il.Emit(OpCodes.Stloc, table);
            if (hasResult && !prefix)
            {
                il.Emit(OpCodes.Ldloc, table); il.Emit(OpCodes.Ldloc, boxedResult);
                il.Emit(OpCodes.Callvirt, AccessTools.PropertySetter(typeof(LuaPatcherService.ParameterTable), "OriginalReturnValue"));
            }
            il.Emit(OpCodes.Ldloc, patches); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, table);
            il.Emit(OpCodes.Ldloca, boxedResult); il.Emit(OpCodes.Ldsfld, resultType);
            il.Emit(prefix ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(LuaDirectBridge), nameof(Dispatch)));
            il.Emit(OpCodes.Stloc, proceed);
            il.Emit(OpCodes.Ldloc, table);
            il.Emit(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(LuaPatcherService.ParameterTable), "ModifiedParameters"));
            il.Emit(OpCodes.Stloc, modified);
            for (int i = 0; i < parameters.Length; i++)
            {
                System.Reflection.Emit.Label next = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, modified); il.Emit(OpCodes.Ldstr, parameters[i].Name); il.Emit(OpCodes.Ldloca, value);
                il.Emit(OpCodes.Callvirt, typeof(Dictionary<string, object>).GetMethod("TryGetValue"));
                il.Emit(OpCodes.Brfalse, next);
                il.Emit(OpCodes.Ldarg, i + offset); il.Emit(OpCodes.Ldloc, value);
                Type parameterType = signature[i + offset].GetElementType();
                il.Emit(parameterType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, parameterType);
                il.Emit(OpCodes.Stobj, parameterType); il.MarkLabel(next);
            }
            il.Emit(OpCodes.Leave, end);
            il.BeginCatchBlock(typeof(Exception)); il.Emit(OpCodes.Stloc, exception);
            System.Reflection.Emit.Label noLogger = il.DefineLabel();
            il.Emit(OpCodes.Ldsfld, luaCs); il.Emit(OpCodes.Brfalse, noLogger);
            il.Emit(OpCodes.Ldloc, exception); il.Emit(OpCodes.Ldc_I4, (int)LuaCsMessageOrigin.LuaMod);
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(LuaCsLogger), "HandleException", new[] { typeof(Exception), typeof(LuaCsMessageOrigin) }));
            il.MarkLabel(noLogger); il.EndExceptionBlock();
            if (hasResult)
            {
                System.Reflection.Emit.Label untouched = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, active); il.Emit(OpCodes.Brfalse, untouched);
                // Harmony's native object-result unbox happens outside LuaCs's
                // catch. Preserve that boundary (invalid callback result throws).
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloc, boxedResult);
                il.Emit(returnType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, returnType);
                il.Emit(OpCodes.Stobj, returnType); il.MarkLabel(untouched);
            }
            if (prefix) il.Emit(OpCodes.Ldloc, proceed);
            il.Emit(OpCodes.Ret);
            Type built = type.CreateType();
            built.GetField("Lookup").SetValue(null, lookup);
            built.GetField("LuaCs").SetValue(null, LuaCsSetup.Instance);
            built.GetField("ResultType").SetValue(null, hasResult ? returnType : null);
            return built.GetMethod(method.Name);
        }
        private static bool Dispatch(LuaPatcherService.PatchedMethod patches, object instance, LuaPatcherService.ParameterTable table,
            ref object result, Type resultType, bool prefix)
        {
            // Another mod may patch the native enumeration AFTER these wrappers
            // were built. Observe it immediately; do not bypass its enumeration.
            if (PatchSafety.HasForeignPatch(prefix ? PrefixEnumerator : PostfixEnumerator))
            {
                using (var enumerator = prefix ? patches.GetPrefixEnumerator() : patches.GetPostfixEnumerator())
                    while (enumerator.MoveNext())
                        if (!InvokeCallback(enumerator.Current, instance, table, ref result, resultType)) return false;
                return true;
            }
            // Concrete ValueCollection enumerator stays a struct; no boxed
            // IEnumerator and no pooled object with an externally visible life.
            var callbacks = prefix ? patches.Prefixes : patches.Postfixes;
            foreach (var callback in callbacks.Values)
                if (!InvokeCallback(callback, instance, table, ref result, resultType)) return false;
            return true;
        }
        private static bool InvokeCallback(LuaPatcherService.LuaCsPatch callback, object instance, LuaPatcherService.ParameterTable table,
            ref object result, Type resultType)
        {
            DynValue luaResult = callback.PatchFunc(instance, table);
            if (resultType != null)
            {
                object tableResult = table.ReturnValue;
                // il.Break() is the IL debugger opcode, not a loop break.
                if (tableResult != null) result = tableResult;
                if (luaResult != null && !luaResult.IsVoid()) result = luaResult.ToObject(resultType);
            }
            return !table.PreventExecution;
        }
    }
}
