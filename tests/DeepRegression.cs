using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Barotrauma;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ReboundGuard;
using Settings = ReboundGuard.Settings;

[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Barotrauma")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("BarotraumaCore")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("DedicatedServer")]

namespace ReboundGuardTests
{
    public static class DeepRegression
    {
        private static int assertions;
        private static readonly Action<List<PhysicsBody>,int> ParallelUpdate =
            (Action<List<PhysicsBody>,int>)AccessTools.Method(typeof(BodyMaintenance),"RunParallel").CreateDelegate(typeof(Action<List<PhysicsBody>,int>));
        private static void Check(bool condition,string message)
        {
            if(!condition) throw new Exception("FAIL: "+message);
            assertions++;
        }
        public static string Run(string reportPath)
        {
            Check(Environment.Version.Major==8,"runs in actual .NET 8 runtime");
            TestMembership();
            TestBodyEquality();
            TestHarmony();
            var bodyBench = new List<object>();
            foreach(int count in new[]{256,2048,8192,32768})
            {
                var bodies=MakeBodies(count,913);
                for(int i=0;i<30;i++){Serial(bodies);ParallelUpdate(bodies,4);}
                var serial=Measure(()=>Serial(bodies),12,15);
                var parallel=Measure(()=>ParallelUpdate(bodies,4),12,15);
                bodyBench.Add(new{bodies=count,serial_median_ms=serial.median,parallel_median_ms=parallel.median,
                    serial_p95_ms=serial.p95,parallel_p95_ms=parallel.p95,speedup=serial.median/parallel.median});
            }
            var queueBench=new List<object>();
            var version=AccessTools.FieldRefAccess<Queue<object>,int>("_version");
            foreach(int count in new[]{256,2048,8192})
            {
                object[] items=Enumerable.Range(0,count).Select(i=>new object()).ToArray();
                var queue=new Queue<object>(count);
                var index=new QueueMembership<object>(q=>version(q));
                Action vanilla=()=>{queue.Clear();foreach(object item in items){if(!queue.Contains(item))queue.Enqueue(item);} };
                Action indexed=()=>{queue.Clear();index.Reset();foreach(object item in items){if(!index.Contains(queue,item))index.Enqueue(queue,item);} };
                for(int i=0;i<8;i++){vanilla();indexed();}
                var serial=Measure(vanilla,9,1);
                var fast=Measure(indexed,9,1);
                queueBench.Add(new{candidates=count,vanilla_median_ms=serial.median,indexed_median_ms=fast.median,
                    vanilla_p95_ms=serial.p95,indexed_p95_ms=fast.p95,speedup=serial.median/fast.median});
            }
            var report=new{date=DateTime.UtcNow.ToString("yyyy-MM-dd"),runtime=Environment.Version.ToString(),processors=Environment.ProcessorCount,
                assembly=typeof(PhysicsBody).Assembly.GetName().Name,version=typeof(PhysicsBody).Assembly.GetName().Version.ToString(),
                assertions,body_benchmark=bodyBench,queue_benchmark=queueBench,queue_incremental=QueueMembershipTests.Run(),existing_physics_benchmark=PhysicsBenchmark.Run(),ordered_solver_research=ConstraintSolverTests.Run(),constraint_preparation=ConstraintPreparationTests.Run(),world_replay=WorldReplayTests.Run(),world_performance=WorldPerformanceTests.Run(),nearby_item_index=NearbyItemIndexTests.Run(),container_effect_guard=ContainerEffectTests.Run(),plugin_lifecycle=PluginLifecycleTests.Run(),
                field_iteration=FieldIterationTests.Run(),frame_health=FrameHealthTests.Run(),bucket_stamp=BucketStampTests.Run(),native_mod_hot_paths=NativeModHotPathTests.Run(),generic_lua_bridge=LuaBridgeTests.Run(),loading_lifecycle=LoadingLifecycleTests.Run(),limitations="Isolated real game methods and synthetic queues/bodies. No loaded submarine, live combat, networking session, GPU or gameplay benchmark."};
            File.WriteAllText(reportPath,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            return "PASS: "+assertions+" assertions under .NET "+Environment.Version+"; "+reportPath;
        }

        private static void TestMembership()
        {
            var version=AccessTools.FieldRefAccess<Queue<int>,int>("_version");
            var index=new QueueMembership<int>(q=>version(q));
            var queue=new Queue<int>();
            var random=new Random(361871);
            for(int i=0;i<12000;i++)
            {
                int value=random.Next(80);
                switch(random.Next(8))
                {
                    case 0: case 1:index.Enqueue(queue,value);break;
                    case 2:queue.Enqueue(value);break;
                    case 3:if(queue.Count>0){int expected=queue.Peek();Check(index.Dequeue(queue)==expected,"FIFO dequeue agrees");}break;
                    case 4:if(queue.Count>0)queue.Dequeue();queue.Enqueue(value);break;
                    case 5:queue.Clear();break;
                    case 6:index.Reset();break;
                }
                Check(index.Contains(queue,value)==queue.Contains(value),"membership agrees after arbitrary mutations");
                Check(index.Contains(queue,(value+1)%80)==queue.Contains((value+1)%80),"second membership agrees");
            }
            // Explicit same-size replacement: the original shadow-set approach
            // that compares only Count would return a stale answer here.
            queue.Clear();queue.Enqueue(1);queue.Enqueue(2);index.Reset();
            Check(index.Contains(queue,1),"initial member");
            queue.Dequeue();queue.Enqueue(3);
            Check(!index.Contains(queue,1)&&index.Contains(queue,3),"same-count replacement invalidates index");
            queue.Clear();queue.Enqueue(4);queue.Enqueue(4);index.Reset();
            Check(index.Contains(queue,4),"duplicate initial members");
            Check(index.Dequeue(queue)==4&&index.Contains(queue,4),"duplicate dequeue keeps remaining membership");
            var strings=new Queue<string>();
            var strVersion=AccessTools.FieldRefAccess<Queue<string>,int>("_version");
            var strIndex=new QueueMembership<string>(q=>strVersion(q));
            strIndex.Enqueue(strings,null);
            Check(strIndex.Contains(strings,null),"null queue entries supported");
            strIndex.Dequeue(strings);
            Check(!strIndex.Contains(strings,null),"null removed");
        }

        private static List<PhysicsBody> MakeBodies(int count,int seed)
        {
            var list=new List<PhysicsBody>(count);
            var random=new Random(seed);
            FieldInfo farseerType=AccessTools.Field(typeof(Body),"_bodyType");
            MethodInfo setBody=AccessTools.PropertySetter(typeof(PhysicsBody),"FarseerBody");
            FieldInfo enabled=AccessTools.Field(typeof(PhysicsBody),"isEnabled");
            FieldInfo offset=AccessTools.Field(typeof(PhysicsBody),"drawOffset");
            FieldInfo rotation=AccessTools.Field(typeof(PhysicsBody),"rotationOffset");
            for(int i=0;i<count;i++)
            {
                var body=(PhysicsBody)RuntimeHelpers.GetUninitializedObject(typeof(PhysicsBody));
                var physics=(Body)RuntimeHelpers.GetUninitializedObject(typeof(Body));
                farseerType.SetValue(physics,i%7==0?BodyType.Static:BodyType.Dynamic);
                setBody.Invoke(body,new object[]{physics});
                enabled.SetValue(body,i%5!=0);
                offset.SetValue(body,new Vector2((float)random.NextDouble()*8-4,(float)random.NextDouble()*8-4));
                rotation.SetValue(body,(float)random.NextDouble()*10-5);
                body.PositionSmoothingFactor=i%3==0?0.8f:(float?)null;
                body.SuppressSmoothRotationCalls=i%2==0;
                list.Add(body);
            }
            return list;
        }

        private static void Serial(List<PhysicsBody> bodies)
        {
            foreach(PhysicsBody body in bodies)
                if((body.Enabled||body.UserData is Character)&&body.BodyType!=BodyType.Static)body.Update();
        }
        private static void TestBodyEquality()
        {
            var serial=MakeBodies(8192,14730);
            var parallel=MakeBodies(8192,14730);
            string[] fields={"drawOffset","rotationOffset","prevPosition","prevRotation","PositionSmoothingFactor","_suppressSmoothRotationCalls","smoothRotationSuppressionCounter"};
            FieldInfo[] members=fields.Select(field=>AccessTools.Field(typeof(PhysicsBody),field)).ToArray();
            foreach(int workers in new[]{1,2,4})
            {
                for(int tick=0;tick<6;tick++)
                {
                    Serial(serial);ParallelUpdate(parallel,workers);
                    for(int i=0;i<serial.Count;i++)
                        foreach(FieldInfo member in members)
                        {
                            Check(Equals(member.GetValue(serial[i]),member.GetValue(parallel[i])),"body state equality including disabled/static bodies: "+member.Name);
                        }
                }
            }
            Check(PhysicsBody.List.Count==0,"test bodies never registered in game world");
        }

        private static void TestHarmony()
        {
            Check((bool)AccessTools.Method(typeof(BodyMaintenance),"SupportedCodeMatches").Invoke(null,null),"actual game loop and body code match trusted fingerprints");
            MethodInfo update=AccessTools.Method(typeof(GameScreen),"Update",new[]{typeof(double)});
            var instructions=PatchProcessor.GetOriginalInstructions(update);
            var output=BodyMaintenance.Transpiler(instructions).ToList();
            Check(output.Count==instructions.Count,"body loop instruction count preserved");
            Check(output.Count(i=>i.operand is MethodInfo method&&method.Name=="RunOrEnumerate")==1,"one body maintenance loop selected");
            bool rejected=false;
            try{BodyMaintenance.Transpiler(new List<CodeInstruction>()).ToList();}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"unknown body loop refused");
            var harmony=new Harmony("reboundguard.deep.regression");
            int originalThreshold=Settings.MinimumBodiesForParallel;
            try
            {
                harmony.Patch(update,transpiler:new HarmonyMethod(typeof(BodyMaintenance),nameof(BodyMaintenance.Transpiler)));
                Check(Harmony.GetPatchInfo(update).Owners.Contains(harmony.Id),"body parallel wrapper emits in game runtime");
                AccessTools.Field(typeof(BodyMaintenance),"installed").SetValue(null,true);
                AccessTools.Field(typeof(BodyMaintenance),"target").SetValue(null,update);
                Settings.MinimumBodiesForParallel=1;
                var one=MakeBodies(1,1);
                var enumerate=(Func<List<PhysicsBody>,List<PhysicsBody>.Enumerator>)AccessTools.Method(typeof(BodyMaintenance),"RunOrEnumerate").CreateDelegate(typeof(Func<List<PhysicsBody>,List<PhysicsBody>.Enumerator>));
                var enumerator=enumerate(one);
                Check(enumerator.MoveNext(),"third-party GameScreen patch forces vanilla enumerator");
                enumerator.Dispose();
                Type serverPatch=typeof(Settings).Assembly.GetType("ReboundGuard.NetworkQueuePatch");
                if(serverPatch!=null)
                {
                    Type gameServer=typeof(PhysicsBody).Assembly.GetType("Barotrauma.Networking.GameServer");
                    MethodInfo write=AccessTools.Method(gameServer,"ClientWriteIngame");
                    harmony.Patch(write,transpiler:new HarmonyMethod(serverPatch,"Transpiler"),finalizer:new HarmonyMethod(serverPatch,"Finish"));
                    Check(Harmony.GetPatchInfo(write).Owners.Contains(harmony.Id),"server queue wrapper emits in game runtime");
                    Type inputPatch=typeof(Settings).Assembly.GetType("ReboundGuard.InputBacklogMonitor");
                    MethodInfo input=AccessTools.Method(typeof(Character),"ServerReadInput");
                    harmony.Patch(input,transpiler:new HarmonyMethod(inputPatch,"Transpiler"));
                    Check(Harmony.GetPatchInfo(input).Owners.Contains(harmony.Id),"input trim observer emits in game runtime");
                }
            }
            finally{harmony.UnpatchSelf();Settings.MinimumBodiesForParallel=originalThreshold;AccessTools.Method(typeof(BodyMaintenance),"Reset").Invoke(null,null);}
            Check(Harmony.GetPatchInfo(update)==null||!Harmony.GetPatchInfo(update).Owners.Contains(harmony.Id),"Harmony cleanup");
        }
        private static (double median,double p95) Measure(Action action,int samples,int loops)
        {
            var values=new double[samples];
            for(int sample=0;sample<samples;sample++)
            {
                long start=Stopwatch.GetTimestamp();
                for(int loop=0;loop<loops;loop++)action();
                values[sample]=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency/loops;
            }
            Array.Sort(values);
            return(values[samples/2],values[Math.Min(samples-1,(int)Math.Ceiling(samples*0.95)-1)]);
        }
    }
}
