using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace ReboundGuard
{
    internal static class LuaArgumentConversion
    {
        private sealed class Conversion { internal MethodInfo Method; }
        private static readonly ConditionalWeakTable<Type, ConditionalWeakTable<Type, Conversion>> Cache = new ConditionalWeakTable<Type, ConditionalWeakTable<Type, Conversion>>();
        private static readonly MethodInfo Original = AccessTools.Method(typeof(SigilExtensions), "GetImplicitOperatorMethod");
        private static readonly Func<Type, Type, MethodInfo> Find = (Func<Type, Type, MethodInfo>)Original.CreateDelegate(typeof(Func<Type, Type, MethodInfo>));
        internal static T Convert<T>(object value)
        {
            // Match native LoadLocalAndCast, including null failure and reflection
            // exception wrapping. Cache metadata, never an operator's result.
            Type source = value.GetType();
            MethodInfo method;
            if (PatchSafety.HasForeignPatch(Original)) method = Find(source, typeof(T));
            else
            {
                if (source == typeof(T)) return (T)value;
                method = Cache.GetValue(source, _ => new ConditionalWeakTable<Type, Conversion>())
                    .GetValue(typeof(T), target => new Conversion { Method = Find(source, target) }).Method;
            }
            return method == null ? (T)value : (T)method.Invoke(null, new[] { value });
        }
    }
}
