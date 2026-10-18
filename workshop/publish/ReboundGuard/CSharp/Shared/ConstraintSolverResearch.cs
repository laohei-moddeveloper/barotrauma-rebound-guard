using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Barotrauma;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Label=System.Reflection.Emit.Label;

namespace ReboundGuard
{
    public static class ConstraintSolverResearch
    {
        private sealed class Plan
        {
            internal readonly OrderedConstraintSchedule Schedule=new OrderedConstraintSchedule();
            internal bool Dirty=true;
            internal Action<int,int> Run;
            internal Func<int,int> ReadA,ReadB;
            internal Func<int[],int[],int,bool> Matches;
            internal Action<int[],int[],int> Copy;
        }
        private static readonly ConditionalWeakTable<object,Plan> Plans=new ConditionalWeakTable<object,Plan>();
        private static Type solverType;
        private static MethodInfo solve,range,reset;
        private static Func<object,int> readCount,readBodyCount;
        private static Func<object,int,int> readA,readB;
        private static Action<object,int,int> runRange;
        private static Func<object,int[],int[],int,bool> matches,copyIndices;
        private static bool bound;
        private static long parallelCalls,fallbackCalls;
        public static long ParallelCalls=>parallelCalls;

        internal static void Install(Harmony harmony)
        {
            if(!Settings.ParallelConstraintSolver)return;
            Bind();
            if(!SupportedCodeMatches())throw new InvalidOperationException("Unknown Farseer velocity solver code");
            harmony.Patch(reset,postfix:new HarmonyMethod(typeof(ConstraintSolverResearch),nameof(MarkReset)));
            harmony.Patch(solve,prefix:new HarmonyMethod(typeof(ConstraintSolverResearch),nameof(BeforeSolve)));
            Plugin.Log("research constraint solver enabled; ordered dependency batches, full original arithmetic");
        }

        internal static bool SupportedCodeMatches()
        {
            Bind();
            return Hash(solve)=="F10B85123C66F12553DB977A52A83C6C16D3DA7F5AB96ACED64E0F7B6402CAAD"&&
                Hash(range)=="B8F6C140C03D1075F79C5B3DFC46CB94EE9BAB8130B9FF60935EC3FA3A300D17";
        }
        private static string Hash(MethodBase method)=>Convert.ToHexString(SHA256.HashData(method.GetMethodBody().GetILAsByteArray()));

        private static void Bind()
        {
            if(bound)return;
            solverType=typeof(World).Assembly.GetType("FarseerPhysics.Dynamics.Contacts.ContactSolver",true);
            solve=AccessTools.Method(solverType,"SolveVelocityConstraints",Type.EmptyTypes);
            range=AccessTools.Method(solverType,"SolveVelocityConstraints",new[]{typeof(int),typeof(int)});
            reset=AccessTools.Method(solverType,"Reset");
            readCount=ReadIntField("_count",false);
            readBodyCount=ReadIntField("_velocities",true);
            readA=ReadConstraintIndex("indexA");readB=ReadConstraintIndex("indexB");
            matches=BulkIndices(false);copyIndices=BulkIndices(true);
            var method=new DynamicMethod("RG_RunOriginalVelocityRange",typeof(void),new[]{typeof(object),typeof(int),typeof(int)},typeof(ConstraintSolverResearch).Module,true);
            ILGenerator il=method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,solverType);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_2);il.Emit(OpCodes.Call,range);il.Emit(OpCodes.Ret);
            runRange=(Action<object,int,int>)method.CreateDelegate(typeof(Action<object,int,int>));
            bound=true;
        }
        private static Func<object,int[],int[],int,bool> BulkIndices(bool copy)
        {
            FieldInfo array=AccessTools.Field(solverType,"_velocityConstraints");Type element=array.FieldType.GetElementType();
            var method=new DynamicMethod(copy?"RG_CopyIndices":"RG_MatchIndices",typeof(bool),new[]{typeof(object),typeof(int[]),typeof(int[]),typeof(int)},typeof(ConstraintSolverResearch).Module,true);
            ILGenerator il=method.GetILGenerator();LocalBuilder values=il.DeclareLocal(array.FieldType),index=il.DeclareLocal(typeof(int));
            Label body=il.DefineLabel(),condition=il.DefineLabel(),different=il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,solverType);il.Emit(OpCodes.Ldfld,array);il.Emit(OpCodes.Stloc,values);
            il.Emit(OpCodes.Ldc_I4_0);il.Emit(OpCodes.Stloc,index);il.Emit(OpCodes.Br,condition);il.MarkLabel(body);
            for(int side=0;side<2;side++)
            {
                if(copy){il.Emit(side==0?OpCodes.Ldarg_1:OpCodes.Ldarg_2);il.Emit(OpCodes.Ldloc,index);}
                il.Emit(OpCodes.Ldloc,values);il.Emit(OpCodes.Ldloc,index);il.Emit(OpCodes.Ldelem_Ref);il.Emit(OpCodes.Ldfld,AccessTools.Field(element,side==0?"indexA":"indexB"));
                if(copy)il.Emit(OpCodes.Stelem_I4);
                else{il.Emit(side==0?OpCodes.Ldarg_1:OpCodes.Ldarg_2);il.Emit(OpCodes.Ldloc,index);il.Emit(OpCodes.Ldelem_I4);il.Emit(OpCodes.Bne_Un,different);}
            }
            il.Emit(OpCodes.Ldloc,index);il.Emit(OpCodes.Ldc_I4_1);il.Emit(OpCodes.Add);il.Emit(OpCodes.Stloc,index);
            il.MarkLabel(condition);il.Emit(OpCodes.Ldloc,index);il.Emit(OpCodes.Ldarg_3);il.Emit(OpCodes.Blt,body);il.Emit(OpCodes.Ldc_I4_1);il.Emit(OpCodes.Ret);
            il.MarkLabel(different);il.Emit(OpCodes.Ldc_I4_0);il.Emit(OpCodes.Ret);
            return(Func<object,int[],int[],int,bool>)method.CreateDelegate(typeof(Func<object,int[],int[],int,bool>));
        }
        private static Func<object,int> ReadIntField(string field,bool length)
        {
            var method=new DynamicMethod("RG_Read_"+field,typeof(int),new[]{typeof(object)},typeof(ConstraintSolverResearch).Module,true);
            ILGenerator il=method.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,solverType);il.Emit(OpCodes.Ldfld,AccessTools.Field(solverType,field));
            if(length){il.Emit(OpCodes.Ldlen);il.Emit(OpCodes.Conv_I4);}il.Emit(OpCodes.Ret);
            return(Func<object,int>)method.CreateDelegate(typeof(Func<object,int>));
        }
        private static Func<object,int,int> ReadConstraintIndex(string name)
        {
            FieldInfo array=AccessTools.Field(solverType,"_velocityConstraints");Type element=array.FieldType.GetElementType();
            if(element.IsValueType)throw new InvalidOperationException("Unknown constraint storage");
            var method=new DynamicMethod("RG_Read_"+name,typeof(int),new[]{typeof(object),typeof(int)},typeof(ConstraintSolverResearch).Module,true);
            ILGenerator il=method.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,solverType);il.Emit(OpCodes.Ldfld,array);
            il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldelem_Ref);il.Emit(OpCodes.Ldfld,AccessTools.Field(element,name));il.Emit(OpCodes.Ret);
            return(Func<object,int,int>)method.CreateDelegate(typeof(Func<object,int,int>));
        }
        private static bool BeforeSolve(object __instance)
        {
            return !TryRun(__instance);
        }
        public static void MarkReset(object __instance)
        {
            Plan plan=Plans.GetValue(__instance,_=>new Plan());plan.Dirty=true;
        }
        public static bool TryRun(object solver)
        {
            Bind();
            if(!Settings.ParallelConstraintSolver||Environment.ProcessorCount<4){fallbackCalls++;return false;}
            int count=readCount(solver);
            if(count<Settings.MinimumConstraintsForParallel){fallbackCalls++;return false;}
            foreach(MethodBase method in new MethodBase[]{solve,range,reset})
            {
                if(PatchSafety.HasForeignPatch(method)){fallbackCalls++;return false;}
            }
            Plan plan=Plans.GetValue(solver,_=>new Plan());
            if(plan.Run==null)
            {
                plan.Run=(start,end)=>runRange(solver,start,end);
                plan.ReadA=i=>readA(solver,i);plan.ReadB=i=>readB(solver,i);
                plan.Matches=(a,b,n)=>matches(solver,a,b,n);plan.Copy=(a,b,n)=>copyIndices(solver,a,b,n);
            }
            if(plan.Dirty)
            {
                if(!plan.Schedule.RefreshBulk(count,readBodyCount(solver),plan.Matches,plan.Copy)){fallbackCalls++;return false;}
                plan.Dirty=false;
            }
            if(!plan.Schedule.WorthParallel(Settings.MinimumConstraintsForParallel,Settings.MinimumConstraintBatchWidth))
            {fallbackCalls++;return false;}
            plan.Schedule.Execute(plan.Run,Math.Min(Math.Max(1,Settings.MaximumWorkers),Environment.ProcessorCount/2),Settings.MinimumConstraintBatchWidth);
            parallelCalls++;return true;
        }
        internal static string Report()
        {
            string result="research velocity solver: enabled="+Settings.ParallelConstraintSolver+"; parallel_calls="+parallelCalls+"; serial_calls="+fallbackCalls;
            parallelCalls=fallbackCalls=0;return result;
        }
    }
}
