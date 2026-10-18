using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FarseerPhysics;
using FarseerPhysics.Collision.Shapes;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace ReboundGuardTests
{
    public static class PhysicsBenchmark
    {
        public static List<object> Run()
        {
            int position=FarseerPhysics.Settings.PositionIterations,velocity=FarseerPhysics.Settings.VelocityIterations;
            var results=new List<object>();
            try
            {
                FarseerPhysics.Settings.PositionIterations=1;
                FarseerPhysics.Settings.VelocityIterations=1;
                foreach(string scene in new[]{"independent_pairs","shared_static_hub"})
                    foreach(bool parallel in new[]{false,true})results.Add(Measure(scene,parallel,256));
            }
            finally
            {
                FarseerPhysics.Settings.PositionIterations=position;
                FarseerPhysics.Settings.VelocityIterations=velocity;
            }
            return results;
        }
        private static object Measure(string scene,bool parallel,int count)
        {
            var world=new World(Vector2.Zero);
            world.ContactManager.CollideMultithreadThreshold=parallel?64:int.MaxValue;
            var dynamicBodies=new List<Body>();
            var directions=new List<Vector2>();
            if(scene=="shared_static_hub")Add(world,Vector2.Zero,50,BodyType.Static);
            for(int i=0;i<count;i++)
            {
                if(scene=="shared_static_hub")
                {
                    float angle=i*MathHelper.TwoPi/count;
                    Vector2 normal=new Vector2((float)Math.Cos(angle),(float)Math.Sin(angle));
                    dynamicBodies.Add(Add(world,normal*50.15f,0.2f,BodyType.Dynamic));
                    directions.Add(-normal);
                }
                else
                {
                    Vector2 center=new Vector2((i%32)*2,(i/32)*2);
                    dynamicBodies.Add(Add(world,center,0.2f,BodyType.Dynamic));directions.Add(Vector2.UnitX);
                    dynamicBodies.Add(Add(world,center+new Vector2(0.39f,0),0.2f,BodyType.Dynamic));directions.Add(-Vector2.UnitX);
                }
            }
            var elapsed=new List<double>();var contactsMs=new List<double>();var solveMs=new List<double>();
            for(int frame=0;frame<160;frame++)
            {
                for(int i=0;i<dynamicBodies.Count;i++)dynamicBodies[i].LinearVelocity=directions[i]*0.5f;
                long start=Stopwatch.GetTimestamp();
                world.Step(1f/60f);
                double total=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;
                if(frame<40)continue;
                elapsed.Add(total);contactsMs.Add(world.ContactsUpdateTime.TotalMilliseconds);solveMs.Add(world.SolveUpdateTime.TotalMilliseconds);
            }
            elapsed.Sort();contactsMs.Sort();solveMs.Sort();
            bool compiledMultiCore=typeof(ContactManager).GetMethod("CollideMultiCore",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!=null;
            return new{scene,requested_contact_threshold=parallel?64:int.MaxValue,compiled_contact_multicore_available=compiledMultiCore,engine_diagnostics_compiled=FarseerPhysics.Settings.EnableDiagnostics,bodies=dynamicBodies.Count,contacts=world.ContactCount,
                step_median_ms=elapsed[elapsed.Count/2],step_p95_ms=elapsed[(int)Math.Ceiling(elapsed.Count*.95)-1],
                contacts_median_ms=contactsMs[contactsMs.Count/2],island_solve_median_ms=solveMs[solveMs.Count/2]};
        }
        private static Body Add(World world,Vector2 position,float radius,BodyType type)
        {
            var body=new Body();
            body.BodyType=type;
            body.Position=position;
            body.SleepingAllowed=false;
            body.CreateFixture(new CircleShape(radius,1f),Category.All,Category.All);
            world.Add(body,true);
            return body;
        }
    }
}
