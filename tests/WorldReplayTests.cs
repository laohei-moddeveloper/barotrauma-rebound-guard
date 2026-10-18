using System;
using System.Collections.Generic;
using System.Linq;
using FarseerPhysics;
using FarseerPhysics.Collision.Shapes;
using FarseerPhysics.Common;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Joints;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ReboundGuard;
using Config = ReboundGuard.Settings;

namespace ReboundGuardTests
{
    public static class WorldReplayTests
    {
        private sealed class Scene
        {
            internal readonly World World = new World(new Vector2(0, -9.8f));
            internal readonly List<Body> Bodies = new List<Body>();
            internal readonly List<string> Events = new List<string>();
            internal Body Add(Vector2 position, bool square, BodyType type = BodyType.Dynamic)
            {
                int id = Bodies.Count;
                var body = new Body { BodyType = type, Position = position, SleepingAllowed = false, UserData = id };
                Shape shape = square ? new PolygonShape(new Vertices(new[] { new Vector2(-.25f, -.25f), new Vector2(.25f, -.25f), new Vector2(.25f, .25f), new Vector2(-.25f, .25f) }), 1f) : new CircleShape(.25f, 1f);
                var fixture = body.CreateFixture(shape, Category.All, Category.All);
                fixture.Friction = .4f;
                fixture.OnCollision += (a, b, contact) => { Events.Add(id + ":" + b.Body.UserData); return true; };
                World.Add(body, true); Bodies.Add(body); return body;
            }
        }
        private static Scene Make(string name)
        {
            var scene = new Scene();
            for (int i = 0; i < 28; i++) scene.Add(new Vector2(i * .49f - 7f, -1), true, BodyType.Static);
            for (int i = 0; i < 120; i++) scene.Add(new Vector2((i % 20) * .48f - 5f, (i / 20) * .51f), name != "mixed" || i % 2 == 0);
            if (name == "jointed")
                for (int i = 28; i < 147; i += 2)
                    scene.World.Add(new DistanceJoint(scene.Bodies[i], scene.Bodies[i + 1], Vector2.Zero, Vector2.Zero, false));
            if (name == "moving_platform") scene.Add(new Vector2(0, 3.4f), true, BodyType.Kinematic);
            return scene;
        }
        private static void Change(Scene scene, int tick, string name)
        {
            if (tick == 60) for (int i = 0; i < 16; i++) scene.Add(new Vector2(i * .49f - 3, 5), i % 2 == 0);
            if (tick == 140)
                for (int i = 40; i < 48; i++) scene.World.Remove(scene.Bodies[i]);
            if (tick % 23 == 0)
                for (int i = 50; i < 80; i++)
                { scene.Bodies[i].LinearVelocity += new Vector2((i % 3 - 1) * .4f, 1.2f); scene.Bodies[i].AngularVelocity += .1f; }
            if (name == "moving_platform")
            {
                Body platform = scene.Bodies[148];
                platform.LinearVelocity = new Vector2(tick % 90 < 45 ? 1f : -1f, 0);
            }
        }
        private static int[] State(Scene scene)
        {
            var values = new List<int>();
            foreach (Body body in scene.Bodies)
            {
                foreach (float value in new[] { body.Position.X, body.Position.Y, body.Rotation, body.LinearVelocity.X, body.LinearVelocity.Y, body.AngularVelocity })
                    values.Add(BitConverter.SingleToInt32Bits(value));
            }
            values.Add(scene.World.ContactCount); return values.ToArray();
        }
        public static object Run()
        {
            bool enabled = Config.ParallelConstraintSolver;
            bool preparationEnabled = Config.ParallelConstraintPreparation;
            int minimum = Config.MinimumConstraintsForParallel, width = Config.MinimumConstraintBatchWidth;
            int preparationMinimum = Config.MinimumContactsForParallelPreparation;
            var harmony = new Harmony("barotrauma.reboundguard.experimental.v040");
            var results = new List<object>();
            try
            {
                Config.ParallelConstraintSolver = true; Config.MinimumConstraintsForParallel = 1; Config.MinimumConstraintBatchWidth = 1;
                Config.ParallelConstraintPreparation = true; Config.MinimumContactsForParallelPreparation = 1;
                AccessTools.Method(typeof(ConstraintSolverResearch), "Install").Invoke(null, new object[] { harmony });
                AccessTools.Method(typeof(ParallelConstraintPreparation), "Install").Invoke(null, new object[] { harmony });
                foreach (string mode in new[] { "ordered_velocity", "preparation" })
                foreach (string name in new[] { "stack", "mixed", "moving_platform", "jointed" })
                {
                    var serial = Make(name); var parallel = Make(name);
                    long before = mode == "preparation" ? ParallelConstraintPreparation.ParallelCalls : ConstraintSolverResearch.ParallelCalls;
                    for (int tick = 0; tick < 240; tick++)
                    {
                        Change(serial, tick, name); Change(parallel, tick, name);
                        Config.ParallelConstraintSolver = Config.ParallelConstraintPreparation = false; serial.World.Step(1f / 60f);
                        Config.ParallelConstraintSolver = mode == "ordered_velocity"; Config.ParallelConstraintPreparation = mode == "preparation"; parallel.World.Step(1f / 60f);
                        if (!State(serial).SequenceEqual(State(parallel))) throw new Exception("World replay state differs: " + name + " tick " + tick);
                        if (!serial.Events.SequenceEqual(parallel.Events)) throw new Exception("World replay collision callback order differs: " + name + " tick " + tick);
                    }
                    long calls = (mode == "preparation" ? ParallelConstraintPreparation.ParallelCalls : ConstraintSolverResearch.ParallelCalls) - before;
                    if (calls == 0 || serial.Events.Count == 0) throw new Exception("World replay did not exercise native solver/callbacks: " + name);
                    results.Add(new { mode, scene = name, steps = 240, bodies_created = serial.Bodies.Count, bodies_removed = 8, joints_created = name == "jointed" ? 60 : 0, collision_callbacks = serial.Events.Count, parallel_range_calls = calls, state_and_callback_order = "bit-exact each step" });
                }
                return new { scenes = results, note = "Complete installed Farseer World.Step on synthetic scenes, including changing contacts, scripted impulses, generation/removal and a kinematic body. Thresholds deliberately lowered to exercise scheduling. No Barotrauma ragdolls, Lua hooks, loaded submarines or networking." };
            }
            finally { harmony.UnpatchSelf(); Config.ParallelConstraintSolver = enabled; Config.MinimumConstraintsForParallel = minimum; Config.MinimumConstraintBatchWidth = width; Config.ParallelConstraintPreparation = preparationEnabled; Config.MinimumContactsForParallelPreparation = preparationMinimum; }
        }
    }
}
