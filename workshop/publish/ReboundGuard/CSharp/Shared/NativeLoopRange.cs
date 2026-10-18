using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using FarseerPhysics.Dynamics.Contacts;
using HarmonyLib;
using Label = System.Reflection.Emit.Label;

namespace ReboundGuard
{
    // Clone the installed native method, changing only its outer loop bounds.
    // No decompiled arithmetic or game binaries are distributed in the mod.
    internal static class NativeLoopRange
    {
        internal static Action<ContactSolver, int, int> Create(MethodInfo original, out MethodBase[] callees)
        {
            if (original.ReturnType != typeof(void) || original.GetParameters().Length != 0 ||
                original.GetMethodBody().ExceptionHandlingClauses.Count != 0)
                throw new InvalidOperationException("Unsupported native loop signature");
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ReboundGuard.NativeRanges." + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
            var access = typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).GetConstructor(new[] { typeof(string) });
            foreach (string name in new[] { typeof(ContactSolver).Assembly.GetName().Name, typeof(Microsoft.Xna.Framework.Vector2).Assembly.GetName().Name })
                assembly.SetCustomAttribute(new CustomAttributeBuilder(access, new object[] { name }));
            TypeBuilder type = assembly.DefineDynamicModule("NativeRanges").DefineType("NativeRanges", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            var method = type.DefineMethod("RG_Range_" + original.Name, MethodAttributes.Public | MethodAttributes.Static, typeof(void),
                new[] { typeof(ContactSolver), typeof(int), typeof(int) });
            method.SetImplementationFlags(MethodImplAttributes.IL | MethodImplAttributes.Managed | MethodImplAttributes.AggressiveOptimization);
            ILGenerator il = method.GetILGenerator();
            var code = PatchProcessor.GetOriginalInstructions(original, il);
            if (code.Count < 5 || code[0].opcode != OpCodes.Ldc_I4_0 || !code[1].opcode.Name.StartsWith("stloc"))
                throw new InvalidOperationException("Unrecognized native loop initializer");
            FieldInfo count = AccessTools.Field(typeof(ContactSolver), "_count");
            int end = code.FindIndex(i => i.opcode == OpCodes.Ldfld && Equals(i.operand, count));
            if (end < 1 || code.Count(i => i.opcode == OpCodes.Ldfld && Equals(i.operand, count)) != 1 ||
                code[end - 1].opcode != OpCodes.Ldarg_0)
                throw new InvalidOperationException("Unrecognized native loop limit");
            code[0].opcode = OpCodes.Ldarg_1;
            code[end - 1].opcode = OpCodes.Ldarg_2;
            code[end].opcode = OpCodes.Nop; code[end].operand = null;
            callees = CollectCallees(original, code);
            foreach (CodeInstruction instruction in code)
            {
                if (instruction.blocks.Count != 0) throw new InvalidOperationException("Unexpected native exception block");
                foreach (Label label in instruction.labels) il.MarkLabel(label);
                Emit(il, instruction);
            }
            return (Action<ContactSolver, int, int>)type.CreateType().GetMethod(method.Name).CreateDelegate(typeof(Action<ContactSolver, int, int>));
        }

        private static MethodBase[] CollectCallees(MethodInfo original, List<CodeInstruction> code)
        {
            var methods = new HashSet<MethodBase> { original };
            var pending = new Queue<MethodBase>();
            foreach (var instruction in code)
                if (instruction.operand is MethodBase called && methods.Add(called)) pending.Enqueue(called);
            // Include transitive engine/vector helpers: a custom hook may add
            // shared effects to arithmetic that was originally pure.
            var vectorAssembly = typeof(Microsoft.Xna.Framework.Vector2).Assembly;
            while (pending.Count > 0)
            {
                MethodBase called = pending.Dequeue();
                if ((called.Module.Assembly != typeof(ContactSolver).Assembly && called.Module.Assembly != vectorAssembly) || called.GetMethodBody() == null) continue;
                foreach (var instruction in PatchProcessor.GetOriginalInstructions(called))
                    if (instruction.operand is MethodBase child && methods.Add(child)) pending.Enqueue(child);
            }
            return methods.ToArray();
        }
        private static void Emit(ILGenerator il, CodeInstruction instruction)
        {
            OpCode op = instruction.opcode;
            switch (instruction.operand)
            {
                case null: il.Emit(op); break;
                case LocalBuilder local: il.Emit(op, local); break;
                case Label label: il.Emit(op, label); break;
                case Label[] labels: il.Emit(op, labels); break;
                case FieldInfo field: il.Emit(op, field); break;
                case MethodInfo method: il.Emit(op, method); break;
                case ConstructorInfo constructor: il.Emit(op, constructor); break;
                case Type type: il.Emit(op, type); break;
                case int number: il.Emit(op, number); break;
                case sbyte number: il.Emit(op, number); break;
                case byte number: il.Emit(op, number); break;
                case short number: il.Emit(op, number); break;
                case long number: il.Emit(op, number); break;
                case float number: il.Emit(op, number); break;
                case double number: il.Emit(op, number); break;
                case string value: il.Emit(op, value); break;
                default: throw new InvalidOperationException("Unsupported native operand: " + instruction.operand.GetType());
            }
        }
    }
}
