using System;
using System.Diagnostics;
using System.Linq;
using Barotrauma.LuaCs;
using HarmonyLib;
using ReboundGuard;

namespace ReboundGuardTests
{
    public static class LoadingLifecycleTests
    {
        private static int checks;
        private static void Check(bool condition,string reason){if(!condition)throw new Exception(reason);checks++;}
        private static AssemblyLoader Create()
        {
            var loader=new AssemblyLoader(new IAssemblyLoaderService.LoaderInitData(Guid.NewGuid(),"RG test",true,null,null,null,null));
            var assembly=typeof(Plugin).Assembly;
            loader._loadedAssemblyData[assembly]=new AssemblyLoader.AssemblyData(assembly,assembly.Location);
            return loader;
        }
        private static (double milliseconds,int stranded) Scenario(bool optimized)
        {
            var harmony=new Harmony("barotrauma.reboundguard.experimental.v040");
            AssemblyLoader loader=null;
            try
            {
                if(optimized)AccessTools.Method(typeof(Plugin).Assembly.GetType("ReboundGuard.LoadingLifecyclePatch"),"Install").Invoke(null,new object[]{harmony});
                loader=Create();
                var expected=loader._loadedAssemblyData.Values.Single().AssemblyReference;
                Check(loader.AssemblyReferences.Single()==expected,"complete enumeration reference/order retained");
                Check(loader._operationsRunning==0,"complete enumeration releases count");
                var iterator=loader.AssemblyReferences.GetEnumerator();
                Check(loader._operationsRunning==0,"enumeration remains lazy");
                Check(iterator.MoveNext() && ReferenceEquals(iterator.Current,expected),"first live reference retained");
                Check(loader._operationsRunning==1,"active iteration protects loader");
                iterator.Dispose();
                int stranded=loader._operationsRunning;
                Check(stranded==(optimized?0:1),"native stranded count reproduced / corrected count released; optimized="+optimized+"; count="+stranded);
                if(optimized)
                {
                    iterator.Dispose();Check(loader._operationsRunning==0,"double dispose does not decrement twice");
                    using(var first=loader.AssemblyReferences.GetEnumerator())
                    using(var second=loader.AssemblyReferences.GetEnumerator())
                    {
                        first.MoveNext();second.MoveNext();Check(loader._operationsRunning==2,"nested consumers have separate counts");
                        first.Dispose();Check(loader._operationsRunning==1,"one consumer does not release another");
                    }
                    Check(loader._operationsRunning==0,"nested consumers fully released");
                    using(var empty=new AssemblyLoader(new IAssemblyLoaderService.LoaderInitData(Guid.NewGuid(),"RG empty",true,null,null,null,null)))
                    {
                        using(var sentinel=empty.AssemblyReferences.GetEnumerator())
                        {Check(sentinel.MoveNext() && sentinel.Current==null,"empty null sentinel retained");}
                        Check(empty._operationsRunning==0,"sentinel has no operation count to release");
                    }
                    var noStart=loader.AssemblyReferences.GetEnumerator();noStart.Dispose();
                    Check(loader._operationsRunning==0,"disposing before MoveNext does not decrement");
                }
                var clock=Stopwatch.StartNew();loader.Dispose();clock.Stop();
                Check(loader.IsDisposed,"real loader still disposed");
                return(clock.Elapsed.TotalMilliseconds,stranded);
            }
            finally{loader?.Dispose();harmony.UnpatchSelf();}
        }
        public static object Run()
        {
            var native=Scenario(false);var optimized=Scenario(true);
            Check(native.milliseconds>=1900,"native loader waits its two-second deadline after stranded enumeration");
            return new{checks,native_dispose_ms=native.milliseconds,optimized_dispose_ms=optimized.milliseconds,
                native_stranded_operations=native.stranded,optimized_stranded_operations=optimized.stranded,
                note="Actual AssemblyLoader with one metadata reference; disposing an early-ended iterator reproduces the native two-second unload wait. Native full GC is retained. Does not establish a complete game loading-time improvement."};
        }
    }
}
