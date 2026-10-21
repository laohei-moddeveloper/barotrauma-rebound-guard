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
        private static void CheckAccessGrants(MethodInfo bridge)
        {
            string[] grants = bridge.DeclaringType.Assembly.GetCustomAttributesData()
                .Where(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute")
                .Select(attribute => (string)attribute.ConstructorArguments.Single().Value).ToArray();
            Check(grants.Length > 0 && grants.Length == grants.Distinct(StringComparer.Ordinal).Count(), "bridge emits assembly access grants once per distinct assembly");
            Check(grants.Contains(typeof(LuaPatcherService).Assembly.GetName().Name) &&
                grants.Contains(typeof(LuaBridgeTests).Assembly.GetName().Name) &&
                grants.Contains(typeof(bool).Assembly.GetName().Name), "deduplication preserves required LuaCs, target and result assembly grants");
        }
        [MethodImpl(MethodImplOptions.NoInlining)] public static int IntTarget(int x,ref float factor){originalCalls++;factor+=.5f;return x+(int)factor;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static bool BoolTarget(){originalCalls++;return false;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static string StringTarget(string value){originalCalls++;return value;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static void VoidTarget(ref int value){originalCalls++;value+=3;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static int ThrowTarget(){throw new InvalidOperationException("fixture original");}
        [MethodImpl(MethodImplOptions.NoInlining)] public static int? NullableTarget(bool present){return present ? 7 : (int?)null;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static DateTime StructTarget(){return new DateTime(2001,2,3);}
        [MethodImpl(MethodImplOptions.NoInlining)] public static void OutTarget(out int value){value=19;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static int RetainedTarget(int value){return value+1;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static int ReentrantTarget(int value){return value+2;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static int OrderingTarget(int value){return value*2;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static int InvalidResultTarget(){return 3;}
        [MethodImpl(MethodImplOptions.NoInlining)] public static int LuaTarget(int value){return value;}
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
            var retained = new List<LuaPatcherService.ParameterTable>();
            Patch(service,"retained",Target(nameof(RetainedTarget)),(instance,table)=>{retained.Add(table);return DynValue.Void;},true);
            Check(RetainedTarget(4)==5 && RetainedTarget(8)==9,"retained-table fixture returns original values");
            Check(!ReferenceEquals(retained[0],retained[1]) && (int)retained[0]["value"]==4 && (int)retained[1]["value"]==8,
                "callbacks retain independent parameter tables and original dictionaries after return");
            retained[0]["value"] = 99;
            Check((int)retained[1]["value"]==8,"retained modified dictionary does not alias a later invocation");
            int depth = 0;
            Patch(service,"reentrant",Target(nameof(ReentrantTarget)),(instance,table)=>{
                if (depth++==0) { int nested=ReentrantTarget(10); Check(nested==12,"nested invocation result retained"); }
                depth--;return DynValue.Void;
            },true);
            Check(ReentrantTarget(3)==5 && depth==0,"same-target reentrant callback keeps separate live state");
            Patch(service,"invalid-result",Target(nameof(InvalidResultTarget)),(instance,table)=>{table.ReturnValue="wrong result type";return DynValue.Void;},false);
            bool invalidResult=false;
            try { InvalidResultTarget(); } catch (InvalidCastException) { invalidResult=true; }
            Check(invalidResult,"wrong typed result throws outside LuaCs exception handler");
            var script = new Script();
            DynValue lua = script.DoString("return function(instance, p) p['value'] = p['value'] + 3; p.PreventExecution = true; return 42 end");
            Patch(service,"actual-lua",Target(nameof(LuaTarget)),(instance,table)=>script.Call(lua, DynValue.Nil, UserData.Create(table)),true);
            Check(LuaTarget(7)==42,"actual MoonSharp callback reads/mutates parameters, overrides return and prevents execution");
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
        private static (string signature,double milliseconds,long bytes,bool moduleRecycled) Scenario(bool optimized, bool direct = false)
        {
            bool previousDirect=Settings.DirectLuaPatchFactory; Settings.DirectLuaPatchFactory=direct;
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
                    CheckAccessGrants(entry.HarmonyPrefixMethod);
                    Check(entry.HarmonyPrefixMethod.GetParameters().All(parameter=>parameter.Name!="__originalMethod"),"generic wrapper no longer requests per-call MethodBase");
                    Check(entry.HarmonyPostfixMethod.GetParameters().Single(parameter=>parameter.Name=="__result").ParameterType==typeof(bool).MakeByRefType(),"bool result forwarded without outer object reference");
                    if (direct) Check(!service.patchModuleBuilder.IsValueCreated,"direct generation does not build a redundant native module");
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
                if(optimized)
                {
                    Patch(service,"after-reset",Target(nameof(BoolTarget)),(instance,table)=>DynValue.NewBoolean(true),false);
                    Check(BoolTarget(),"new bridge works after context reset");
                    CheckAccessGrants(service.registeredPatches[key].HarmonyPostfixMethod);
                    service.Reset();
                }
                return(signature,benchmark.ms,benchmark.bytes,recycled);
            }
            finally
            {
                AccessTools.Method(Optimizer,"Stop").Invoke(null,null);service.Dispose();harmony.UnpatchSelf();Settings.DirectLuaPatchFactory=previousDirect;
            }
        }
        public static object Run()
        {
            checks=0;
            var baseline=Scenario(false);var previous=Scenario(true,false);var optimized=Scenario(true,true);
            Check(baseline.signature==optimized.signature,"complete callback order/results/ref mutations agree with native LuaCs");
            Check(baseline.signature==previous.signature,"previous two-stage bridge remains a supported comparison");
            Check(optimized.bytes<baseline.bytes,"generic bridge reduces real callback scaffold allocations");
            Check(optimized.bytes<previous.bytes,"direct bridge also reduces allocations against the already released bridge");
            var factoryNative=FactoryBenchmark(false,false);var factoryPrevious=FactoryBenchmark(true,false);var factoryDirect=FactoryBenchmark(true,true);
            Check(factoryNative.nativeModule && factoryPrevious.nativeModule && !factoryDirect.nativeModule,"direct factory alone avoids initial native module generation");
            return new {checks,calls_per_batch=20000,native_luacs_ms=baseline.milliseconds,optimized_bridge_ms=optimized.milliseconds,
                native_luacs_bytes=baseline.bytes,optimized_bridge_bytes=optimized.bytes,speedup=baseline.milliseconds/optimized.milliseconds,
                previous_bridge_ms=previous.milliseconds,previous_bridge_bytes=previous.bytes,
                factory_generation=new {wrappers=32,native_ms=factoryNative.ms,previous_bridge_ms=factoryPrevious.ms,direct_ms=factoryDirect.ms,
                    native_bytes=factoryNative.bytes,previous_bridge_bytes=factoryPrevious.bytes,direct_bytes=factoryDirect.bytes},
                reset_recycles_module=optimized.moduleRecycled,
                clear_all_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(AccessTools.Method(typeof(LuaPatcherService),"ClearAll").GetMethodBody().GetILAsByteArray())),
                reference_getter_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(AccessTools.PropertyGetter(typeof(AssemblyLoader),"AssemblyReferences").GetMethodBody().GetILAsByteArray())),
                reference_iterator_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(AccessTools.Method(AccessTools.PropertyGetter(typeof(AssemblyLoader),"AssemblyReferences").GetCustomAttribute<IteratorStateMachineAttribute>().StateMachineType,"MoveNext").GetMethodBody().GetILAsByteArray())),
                reference_dispose_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(AccessTools.Method(AccessTools.PropertyGetter(typeof(AssemblyLoader),"AssemblyReferences").GetCustomAttribute<IteratorStateMachineAttribute>().StateMachineType,"System.IDisposable.Dispose").GetMethodBody().GetILAsByteArray())),
                note="Actual LuaPatcherService wrappers; C# callback overhead benchmark plus an actual MoonSharp semantic fixture. Callback order, retained tables, reentrancy, arguments, PreventExecution, result types, reset and detach verified. Factory fixture excludes compilation, loading assets and Harmony target installation. Not whole-game FPS or loading-time proof."};
        }
        private static (double ms,long bytes,bool nativeModule) FactoryBenchmark(bool optimized,bool direct)
        {
            bool previous=Settings.DirectLuaPatchFactory;Settings.DirectLuaPatchFactory=direct;
            var harmony=new Harmony("barotrauma.reboundguard.experimental.v040");
            var service=new LuaPatcherService();
            MethodInfo factory=AccessTools.Method(typeof(LuaPatcherService),"CreateDynamicHarmonyPatch");
            try
            {
                if(optimized)AccessTools.Method(Optimizer,"Install").Invoke(null,new object[]{harmony});
                long allocated=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
                for(int i=0;i<32;i++)factory.Invoke(service,new object[]{"factory"+i,Target(nameof(IntTarget)),ILuaCsHook.HookMethodType.Before});
                return ((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency,GC.GetAllocatedBytesForCurrentThread()-allocated,service.patchModuleBuilder.IsValueCreated);
            }
            finally{AccessTools.Method(Optimizer,"Stop").Invoke(null,null);service.Dispose();harmony.UnpatchSelf();Settings.DirectLuaPatchFactory=previous;}
        }
    }
}
