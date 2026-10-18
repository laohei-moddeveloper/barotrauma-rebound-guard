using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using FarseerPhysics;
using FarseerPhysics.Common;
using FarseerPhysics.Collision.Shapes;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ReboundGuard;
using Config=ReboundGuard.Settings;

namespace ReboundGuardTests
{
    public static class ConstraintSolverTests
    {
        private static readonly MethodInfo Clone=typeof(object).GetMethod("MemberwiseClone",BindingFlags.NonPublic|BindingFlags.Instance);
        private static Type solverType;
        private static FieldInfo constraints,velocities,countField;
        private static Action<object,int,int> range;
        private static int checks;
        private static void Check(bool condition,string message){if(!condition)throw new Exception("FAIL: "+message);checks++;}

        public static object Run()
        {
            bool enabled=Config.ParallelConstraintSolver;
            int minimum=Config.MinimumConstraintsForParallel,width=Config.MinimumConstraintBatchWidth;
            try
            {
                Check((bool)AccessTools.Method(typeof(ConstraintSolverResearch),"SupportedCodeMatches").Invoke(null,null),"actual Farseer velocity solver fingerprints");
                solverType=typeof(World).Assembly.GetType("FarseerPhysics.Dynamics.Contacts.ContactSolver");
                constraints=AccessTools.Field(solverType,"_velocityConstraints");velocities=AccessTools.Field(solverType,"_velocities");countField=AccessTools.Field(solverType,"_count");
                range=(Action<object,int,int>)AccessTools.Field(typeof(ConstraintSolverResearch),"runRange").GetValue(null);
                Config.ParallelConstraintSolver=true;Config.MinimumConstraintsForParallel=1;Config.MinimumConstraintBatchWidth=1;
                TestGraph();
                object template=Template();
                foreach(string graph in new[]{"disjoint","chain","hub","grid"})
                {
                    object serial=MakeSolver(template,384,graph);
                    object parallel=CloneSolver(serial);
                    for(int iteration=0;iteration<4;iteration++)
                    {
                        range(serial,0,384);
                        ConstraintSolverResearch.MarkReset(parallel);
                        Check(ConstraintSolverResearch.TryRun(parallel),"research path executes "+graph);
                        Check(State(serial).SequenceEqual(State(parallel)),"bit-exact native velocity/impulse results: "+graph);
                    }
                }
                Config.MinimumConstraintBatchWidth=128;
                Check(!ConstraintSolverResearch.TryRun(MakeSolver(template,512,"hub")),"shared-body hub falls back to serial");
                Check(!ConstraintSolverResearch.TryRun(MakeSolver(template,512,"chain")),"dependency chain falls back to serial");
                var solve=AccessTools.Method(solverType,"SolveVelocityConstraints",Type.EmptyTypes);
                var harmony=new Harmony("barotrauma.reboundguard.experimental.v040");
                try
                {
                    harmony.Patch(solve,prefix:new HarmonyMethod(typeof(ConstraintSolverResearch),"BeforeSolve"));
                    object serial=MakeSolver(template,512,"disjoint"),parallel=CloneSolver(serial);
                    Config.ParallelConstraintSolver=false;solve.Invoke(serial,null);
                    Config.ParallelConstraintSolver=true;long before=ConstraintSolverResearch.ParallelCalls;solve.Invoke(parallel,null);
                    Check(ConstraintSolverResearch.ParallelCalls>before,"actual patched public solver invokes ordered workers");
                    Check(State(serial).SequenceEqual(State(parallel)),"patched public solver native results bit-exact");
                }
                finally{harmony.UnpatchSelf();}
                var benchmarks=new List<object>();
                foreach(string graph in new[]{"disjoint","grid","hub"})
                {
                    int n=4096;
                    object serial=MakeSolver(template,n,graph),parallel=CloneSolver(serial);
                    Action baseline=()=>range(serial,0,n);
                    Action optimized=()=>{ConstraintSolverResearch.MarkReset(parallel);if(!ConstraintSolverResearch.TryRun(parallel))range(parallel,0,n);};
                    for(int i=0;i<20;i++){baseline();optimized();}
                    double original=Median(baseline),cached=Median(optimized);
                    var coldTimes=new double[11];
                    for(int sample=0;sample<coldTimes.Length;sample++)
                    {
                        object cold=MakeSolver(template,n,graph);long start=Stopwatch.GetTimestamp();
                        if(!ConstraintSolverResearch.TryRun(cold))range(cold,0,n);
                        coldTimes[sample]=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;
                    }
                    Array.Sort(coldTimes);
                    benchmarks.Add(new{graph,constraints=n,serial_ms=original,cached_schedule_ms=cached,cold_schedule_median_ms=coldTimes[5],cold_schedule_p95_ms=coldTimes[10],speedup_cached=original/cached});
                }
                return new{checks,contact_points=2,benchmarks,default_enabled=false,
                    note="Calls installed native private range solver. Bitwise comparisons cover synthetic dependency graphs, not a complete live multiplayer round. Cold topology construction is included separately."};
            }
            finally{Config.ParallelConstraintSolver=enabled;Config.MinimumConstraintsForParallel=minimum;Config.MinimumConstraintBatchWidth=width;}
        }

        private static void TestGraph()
        {
            for(int seed=0;seed<12;seed++)
            {
                var random=new Random(seed);int n=1000,bodies=400;
                int[] a=new int[n],b=new int[n];for(int i=0;i<n;i++){a[i]=random.Next(bodies);b[i]=random.Next(bodies);}
                float[] serial=Enumerable.Range(0,bodies).Select(i=>i*.123f).ToArray(),parallel=(float[])serial.Clone();
                int[] seen=new int[n],last=Enumerable.Repeat(-1,bodies).ToArray();
                Action<float[],int> step=(state,i)=>{float x=state[a[i]],y=state[b[i]];state[a[i]]=x*.93f+y*.07f;state[b[i]]=y*.96f-x*.04f;};
                for(int i=0;i<n;i++)step(serial,i);
                var schedule=new OrderedConstraintSchedule();Check(schedule.Refresh(n,bodies,i=>a[i],i=>b[i]),"random graph builds");
                schedule.Execute((from,to)=>{for(int i=from;i<to;i++){if(last[a[i]]>=i||last[b[i]]>=i)throw new Exception("Dependency inversion");last[a[i]]=last[b[i]]=i;System.Threading.Interlocked.Increment(ref seen[i]);step(parallel,i);}},4,1);
                Check(serial.Select(BitConverter.SingleToInt32Bits).SequenceEqual(parallel.Select(BitConverter.SingleToInt32Bits)),"random arithmetic bit-exact");
                Check(seen.All(value=>value==1),"each constraint executes exactly once");
                a[0]=random.Next(bodies);Check(schedule.Refresh(n,bodies,i=>a[i],i=>b[i]),"changed graph refreshes");
            }
        }
        private static object Template()
        {
            var world=new World(Vector2.Zero);
            var vertices=new Vertices(new[]{new Vector2(-.5f,-.5f),new Vector2(.5f,-.5f),new Vector2(.5f,.5f),new Vector2(-.5f,.5f)});
            for(int i=0;i<2;i++)
            {
                Body body=world.CreateBody(new Vector2(i*.9f,0),0,BodyType.Dynamic,true);
                body.CreateFixture(new PolygonShape(vertices,1f),Category.All,Category.All);
            }
            world.Step(1f/60f);
            object island=AccessTools.PropertyGetter(typeof(World),"Island").Invoke(world,null);
            object solver=AccessTools.Field(island.GetType(),"_contactSolver").GetValue(island);
            object constraint=((Array)constraints.GetValue(solver)).GetValue(0);
            Check((int)AccessTools.Field(constraint.GetType(),"pointCount").GetValue(constraint)==2,"real polygon template contains two contact points");
            return solver;
        }
        private static object MakeSolver(object template,int count,string graph)
        {
            object solver=Clone.Invoke(template,null);
            Array input=(Array)constraints.GetValue(template);
            Type element=input.GetType().GetElementType();Array contacts=Array.CreateInstance(element,count);
            FieldInfo indexA=AccessTools.Field(element,"indexA"),indexB=AccessTools.Field(element,"indexB");
            for(int i=0;i<count;i++)
            {
                object item=CloneConstraint(input.GetValue(0));int a,b;
                if(graph=="chain"){a=i;b=i+1;}
                else if(graph=="hub"){a=0;b=i+1;}
                else if(graph=="grid"){a=i%1024;b=(a+(i%2==0?1:32))%1024;}
                else{a=i*2;b=i*2+1;}
                indexA.SetValue(item,a);indexB.SetValue(item,b);contacts.SetValue(item,i);
            }
            constraints.SetValue(solver,contacts);countField.SetValue(solver,count);
            Type velocityType=velocities.FieldType.GetElementType();Array state=Array.CreateInstance(velocityType,count*2+2);
            FieldInfo v=AccessTools.Field(velocityType,"v"),w=AccessTools.Field(velocityType,"w");var random=new Random(173);
            for(int i=0;i<state.Length;i++){object value=Activator.CreateInstance(velocityType);v.SetValue(value,new Vector2((float)random.NextDouble()*3-1.5f,(float)random.NextDouble()*3-1.5f));w.SetValue(value,(float)random.NextDouble()-.5f);state.SetValue(value,i);}
            velocities.SetValue(solver,state);return solver;
        }
        private static object CloneConstraint(object source)
        {
            object copy=Clone.Invoke(source,null);FieldInfo points=AccessTools.Field(source.GetType(),"points");Array original=(Array)points.GetValue(source),values=(Array)original.Clone();
            for(int i=0;i<values.Length;i++)values.SetValue(Clone.Invoke(original.GetValue(i),null),i);
            points.SetValue(copy,values);return copy;
        }
        private static object CloneSolver(object source)
        {
            object copy=Clone.Invoke(source,null);Array original=(Array)constraints.GetValue(source),values=(Array)original.Clone();
            for(int i=0;i<values.Length;i++)values.SetValue(CloneConstraint(original.GetValue(i)),i);
            constraints.SetValue(copy,values);velocities.SetValue(copy,((Array)velocities.GetValue(source)).Clone());return copy;
        }
        private static int[] State(object solver)
        {
            var bits=new List<int>();Array state=(Array)velocities.GetValue(solver);
            foreach(object velocity in state){Vector2 v=(Vector2)AccessTools.Field(velocity.GetType(),"v").GetValue(velocity);bits.Add(BitConverter.SingleToInt32Bits(v.X));bits.Add(BitConverter.SingleToInt32Bits(v.Y));bits.Add(BitConverter.SingleToInt32Bits((float)AccessTools.Field(velocity.GetType(),"w").GetValue(velocity)));}
            foreach(object constraint in(Array)constraints.GetValue(solver))foreach(object point in(Array)AccessTools.Field(constraint.GetType(),"points").GetValue(constraint))
            {bits.Add(BitConverter.SingleToInt32Bits((float)AccessTools.Field(point.GetType(),"normalImpulse").GetValue(point)));bits.Add(BitConverter.SingleToInt32Bits((float)AccessTools.Field(point.GetType(),"tangentImpulse").GetValue(point)));}
            return bits.ToArray();
        }
        private static double Median(Action action)
        {
            var times=new double[13];for(int sample=0;sample<times.Length;sample++){long start=Stopwatch.GetTimestamp();for(int i=0;i<10;i++)action();times[sample]=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency/10;}Array.Sort(times);return times[times.Length/2];
        }
    }
}
