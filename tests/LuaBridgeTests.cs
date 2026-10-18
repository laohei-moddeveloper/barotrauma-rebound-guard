using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Compatibility;
using HarmonyLib;
using MoonSharp.Interpreter;
using ReboundGuard;

namespace ReboundGuardTests
{
    public static class LuaBridgeTests
    {
        private static int checks, originalCalls;
        private static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
        private static Type Optimizer => typeof(Plugin).Assembly.GetType("ReboundGuard.LuaBridgeOptimizer");
        private static readonly MethodInfo Register = AccessTools.Method(typeof(LuaPatcherService),"Patch",new[]{typeof(string),typeof(MethodBase),typeof(LuaCsPatchFunc),typeof(ILuaCsHook.HookMethodType)});
        private static void Patch(LuaPatcherService service,string id,MethodBase target,LuaCsPatchFunc callback,bool before)
        {
            Register.Invoke(service,new object[]{id,target,callback,before?ILuaCsHook.HookMethodType.Before:ILuaCsHook.HookMethodType.After});
            // LuaCsSetup.Instance can lazily create its own service in an isolated
            // host. Real gameplay has one active service; make that invariant explicit.
            LuaPatcherService.instance=service;
        }
        private static MethodInfo Target(string name)=>AccessTools.Method(typeof(LuaBridgeTests),name);
        [MethodImpl(MethodImplOptions.NoInlining)] public static int IntTarget(int x,ref float factor){originalCalls++;factor+=.5f;return x+(int)factor;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool BoolTarget(){originalCalls++;return false;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static string StringTarget(string value){originalCalls++;return value;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static void VoidTarget(ref int value){originalCalls++;value+=3;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static int ThrowTarget(){throw new InvalidOperationException("fixture original");}
        [MethodImpl(MethodImplOptions.NoInlining)] public static int? NullableTarget(bool present){return present ? 7 : (int?)null;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static DateTime StructTarget(){return new DateTime(2001,2,3);}
        [MethodImpl(MethodImplOptions.NoInlining)] public static void OutTarget(out int value){value=19;}
        public sealed class InstanceFixture
        {
            public int Value;
            [MethodImpl(MethodImplOptions.NoInlining)] public InstanceFixture(int value){Value=value;}
            [MethodImpl(MethodImplOptions.NoInlining)] public int Read(){return Value;}
        }
        private static void ExtraSemantics(LuaPatcherService service, bool optimized)
        {
            Patch(service,"nullable",Target(nameof(NullableTarget)),(instance,table)=>DynValue.Void,false);
            Check(NullableTarget(true)==7 && NullableTarget(false)==null,"nullable value and null retained; optimized="+optimized);
            Patch(service,"struct",Target(nameof(StructTarget)),(instance,table)=>DynValue.Void,false);
            Check(StructTarget()==new DateTime(2001,2,3),"struct return retained");
            Patch(service,"out",Target(nameof(OutTarget)),(instance,table)=>{table["value"]=23;return DynValue.Void;},false);
            OutTarget(out int value);Check(value==23,"out parameter mutation retained");
            Patch(service,"constructor",typeof(InstanceFixture).GetConstructor(new[]{typeof(int)}),(instance,table)=>{
                Check(instance is InstanceFixture,"constructor instance forwarded");table["value"]=37;return DynValue.Void;
            },true);
            var fixture=new InstanceFixture(5);Check(fixture.Value==37,"constructor parameter mutation retained");
            Patch(service,"instance",AccessTools.Method(typeof(InstanceFixture),nameof(InstanceFixture.Read)),(instance,table)=>{
                Check(ReferenceEquals(instance,fixture),"instance identity retained");return DynValue.Void;
            },false);
            Check(fixture.Read()==37,"instance method return retained");
        }
        private static (double ms,long bytes) Measure(Action run)
        {
            for(int i=0;i<500;i++)run();var times=new List<(double ms,long bytes)>();
            for(int repeat=0;repeat<7;repeat++){
                long allocated=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
                for(int i=0;i<20000;i++)run();
                times.Add(((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency,GC.GetAllocatedBytesForCurrentThread()-allocated));
            }
            return times.OrderBy(value=>value.ms).ElementAt(3);
        }
        private static (string signature,double milliseconds,long bytes,bool moduleRecycled) Scenario(bool optimized)
        {
            var harmony=new Harmony("barotrauma.reboundguard.experimental.v040");
            var service=new LuaPatcherService();var events=new List<string>();
            try
            {
                if(optimized) AccessTools.Method(Optimizer,"Install").Invoke(null,new object[]{harmony});
                Patch(service,"mutate",Target(nameof(IntTarget)),(instance,table)=>{
                    events.Add("before:"+table["x"]);table["x"]=7;table["factor"]=2f;return DynValue.Void;
                },true);
                Patch(service,"after",Target(nameof(IntTarget)),(instance,table)=>{
                    events.Add("after:"+table.OriginalReturnValue);return DynValue.NewNumber((int)table.OriginalReturnValue+4);
                },false);
                originalCalls=0;float factor=1;Exception firstError=null;
                EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observe=(sender,error)=>{if(firstError==null)firstError=error.Exception;};
                AppDomain.CurrentDomain.FirstChanceException+=observe;
                int result;try{result=IntTarget(1,ref factor);}finally{AppDomain.CurrentDomain.FirstChanceException-=observe;}
                string signature=result+":"+factor+":"+originalCalls+":"+string.Join(",",events);events.Clear();
                Check(result==13 && factor==2.5f && originalCalls==1,"int result/ref parameter mutations retained; optimized="+optimized+"; "+signature+"; error="+firstError);
                Patch(service,"replace-before",Target(nameof(IntTarget)),(instance,table)=>{table.PreventExecution=true;return DynValue.NewNumber(40);},true);
                originalCalls=0;factor=1;result=IntTarget(3,ref factor);
                signature+="|skip:"+result+":"+factor+":"+originalCalls;Check(originalCalls==0,"PreventExecution retained");
                Patch(service,"boolean",Target(nameof(BoolTarget)),(instance,table)=>DynValue.NewBoolean(true),false);
                Check(BoolTarget(),"typed bool result retained");signature+="|bool:"+BoolTarget();
                Patch(service,"string",Target(nameof(StringTarget)),(instance,table)=>{
                    Check((string)table.OriginalReturnValue=="old","original reference return retained");return DynValue.NewString("new");
                },false);
                signature+="|string:"+StringTarget("old");
                Patch(service,"void",Target(nameof(VoidTarget)),(instance,table)=>{table["value"]=10;return DynValue.Void;},true);
                int value=2;VoidTarget(ref value);signature+="|void:"+value;Check(value==13,"void target/ref mutation retained");
                Patch(service,"throw",Target(nameof(ThrowTarget)),(instance,table)=>DynValue.Void,true);
                bool threw=false;try{ThrowTarget();}catch(InvalidOperationException){threw=true;}Check(threw,"original target exception preserved");
                ExtraSemantics(service,optimized);

                // No Lua VM here: isolate the real generated LuaCs scaffolding.
                bool seen=false;Action run=()=>{seen=BoolTarget();};var benchmark=Measure(run);Check(seen,"benchmark callback active");
                var key=LuaPatcherService.MethodKey.Create(Target(nameof(BoolTarget)));
                var entry=service.registeredPatches[key];
                entry.Postfixes.Clear();Check(!BoolTarget(),"empty phase immediately returns original result");
                Patch(service,"boolean-readded",Target(nameof(BoolTarget)),(instance,table)=>DynValue.NewBoolean(true),false);
                Check(BoolTarget(),"new callback immediately observed after empty phase");
                if(optimized)
                {
                    Check(entry.HarmonyPrefixMethod.GetParameters().All(parameter=>parameter.Name!="__originalMethod"),"generic wrapper no longer requests per-call MethodBase");
                    Check(entry.HarmonyPostfixMethod.GetParameters().Single(parameter=>parameter.Name=="__result").ParameterType==typeof(bool).MakeByRefType(),"bool result forwarded without outer object reference");
                    AccessTools.Method(Optimizer,"Stop").Invoke(null,null);
                    Check(BoolTarget(),"detaching optimizer retains Lua callback behavior");
                    Check(entry.HarmonyPrefixMethod.DeclaringType.Name.StartsWith("Patch_"),"detaching restores native registered wrappers");
                }
                // Exercise module recycling with optimized hooks still installed.
                object oldModule=service.patchModuleBuilder;
                service.Reset();
                bool recycled=!ReferenceEquals(oldModule,service.patchModuleBuilder) && !service.patchModuleBuilder.IsValueCreated;
                Check(!BoolTarget(),"service reset removes callbacks and patches");
                if(optimized) Check(recycled,"service reset releases the old dynamic module builder");
                return(signature,benchmark.ms,benchmark.bytes,recycled);
            }
            finally
            {
                AccessTools.Method(Optimizer,"Stop").Invoke(null,null);service.Dispose();harmony.UnpatchSelf();
            }
        }
        public static object Run()
        {
            var baseline=Scenario(false);var optimized=Scenario(true);
            Check(baseline.signature==optimized.signature,"complete callback order/results/ref mutations agree with native LuaCs");
            Check(optimized.bytes<baseline.bytes,"generic bridge reduces real callback scaffold allocations");
            return new {checks,calls_per_batch=20000,native_luacs_ms=baseline.milliseconds,optimized_bridge_ms=optimized.milliseconds,
                native_luacs_bytes=baseline.bytes,optimized_bridge_bytes=optimized.bytes,speedup=baseline.milliseconds/optimized.milliseconds,
                reset_recycles_module=optimized.moduleRecycled,
                clear_all_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(AccessTools.Method(typeof(LuaPatcherService),"ClearAll").GetMethodBody().GetILAsByteArray())),
                reference_getter_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(AccessTools.PropertyGetter(typeof(AssemblyLoader),"AssemblyReferences").GetMethodBody().GetILAsByteArray())),
                reference_iterator_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(AccessTools.Method(AccessTools.PropertyGetter(typeof(AssemblyLoader),"AssemblyReferences").GetCustomAttribute<IteratorStateMachineAttribute>().StateMachineType,"MoveNext").GetMethodBody().GetILAsByteArray())),
                reference_dispose_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(AccessTools.Method(AccessTools.PropertyGetter(typeof(AssemblyLoader),"AssemblyReferences").GetCustomAttribute<IteratorStateMachineAttribute>().StateMachineType,"System.IDisposable.Dispose").GetMethodBody().GetILAsByteArray())),
                note="Actual LuaPatcherService generated wrappers and C# callbacks; isolates generic bridge overhead without Lua VM cost. Callback order, arguments, PreventExecution, result types, reset and detach verified. Not whole-game FPS or loading-time proof."};
        }
    }
}
