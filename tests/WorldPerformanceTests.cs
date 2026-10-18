using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FarseerPhysics;
using FarseerPhysics.Collision.Shapes;
using FarseerPhysics.Common;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ReboundGuard;
using Config = ReboundGuard.Settings;

namespace ReboundGuardTests
{
    public static class WorldPerformanceTests
    {
        private sealed class Scene
        {
            internal readonly World World = new World(Vector2.Zero);
            internal readonly Body[] Bodies;
            internal readonly Vector2[] Positions;
            internal Scene(int count, bool dense)
            {
                Bodies = new Body[count]; Positions = new Vector2[count];
                if (!dense)
                {
                    var floor = new Body { BodyType = BodyType.Static, Position = new Vector2(0, -.48f) };
                    floor.CreateFixture(new PolygonShape(new Vertices(new[] { new Vector2(-count, -.25f), new Vector2(count, -.25f), new Vector2(count, .25f), new Vector2(-count, .25f) }), 1f), Category.All, Category.All);
                    World.Add(floor, true);
                }
                for (int i = 0; i < count; i++)
                {
                    Positions[i] = dense ? new Vector2((i % 32) * .49f, (i / 32) * .49f) : new Vector2(i * .9f - count * .45f, 0);
                    var body = new Body { BodyType = BodyType.Dynamic, Position = Positions[i], SleepingAllowed = false };
                    body.CreateFixture(new PolygonShape(new Vertices(new[] { new Vector2(-.25f, -.25f), new Vector2(.25f, -.25f), new Vector2(.25f, .25f), new Vector2(-.25f, .25f) }), 1f), Category.All, Category.All);
                    World.Add(body, true); Bodies[i] = body;
                }
            }
            internal void Reset(int frame)
            {
                for (int i = 0; i < Bodies.Length; i++)
                { Bodies[i].Position = Positions[i]; Bodies[i].Rotation = 0; Bodies[i].LinearVelocity = new Vector2((i % 3 - 1) * .1f, -.05f); Bodies[i].AngularVelocity = (frame % 7 - 3) * .01f; }
            }
            internal void State(int[] bits)
            {
                int i = 0;
                foreach (Body body in Bodies)
                {
                    bits[i++] = BitConverter.SingleToInt32Bits(body.Position.X); bits[i++] = BitConverter.SingleToInt32Bits(body.Position.Y);
                    bits[i++] = BitConverter.SingleToInt32Bits(body.Rotation); bits[i++] = BitConverter.SingleToInt32Bits(body.LinearVelocity.X);
                    bits[i++] = BitConverter.SingleToInt32Bits(body.LinearVelocity.Y); bits[i++] = BitConverter.SingleToInt32Bits(body.AngularVelocity);
                }
                bits[i] = World.ContactCount;
            }
        }
        private static (double median, double p95, int maxContacts) Measure(Scene scene, int[][] reference, bool write)
        {
            var times = new double[100]; int maxContacts = 0;
            var state = new int[scene.Bodies.Length * 6 + 1];
            for (int frame = 0; frame < 130; frame++)
            {
                scene.Reset(frame); long start = Stopwatch.GetTimestamp(); scene.World.Step(1f / 60f);
                double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                scene.State(state);
                if (write) Array.Copy(state, reference[frame], state.Length);
                else if (!state.SequenceEqual(reference[frame])) throw new Exception("Complete world performance replay differs at frame " + frame);
                if (frame >= 30) times[frame - 30] = ms;
                maxContacts = Math.Max(maxContacts, scene.World.ContactCount);
            }
            Array.Sort(times); return (times[50], times[94], maxContacts);
        }
        public static object Run()
        {
            bool enabled = Config.ParallelConstraintPreparation;
            int threshold = Config.MinimumContactsForParallelPreparation;
            var harmony = new Harmony("barotrauma.reboundguard.experimental.v040");
            var rows = new List<object>();
            try
            {
                Config.ParallelConstraintPreparation = true; Config.MinimumContactsForParallelPreparation = 1024;
                foreach (var setup in new[] { (name: "dense_1024", count: 1024, dense: true), (name: "dense_4096", count: 4096, dense: true), (name: "shared_floor_1024", count: 1024, dense: false) })
                {
                    int length = setup.count * 6 + 1;
                    int[][] reference = Enumerable.Range(0, 130).Select(_ => new int[length]).ToArray();
                    var baseline = Measure(new Scene(setup.count, setup.dense), reference, true);
                    AccessTools.Method(typeof(ParallelConstraintPreparation), "Install").Invoke(null, new object[] { harmony });
                    long before = ParallelConstraintPreparation.ParallelCalls;
                    var optimized = Measure(new Scene(setup.count, setup.dense), reference, false);
                    long calls = ParallelConstraintPreparation.ParallelCalls - before;
                    Config.ParallelConstraintPreparation = false;
                    var hookedSerial = Measure(new Scene(setup.count, setup.dense), reference, false);
                    harmony.UnpatchSelf(); Config.ParallelConstraintPreparation = true;
                    var secondBaseline = Measure(new Scene(setup.count, setup.dense), reference, false);
                    rows.Add(new { scene = setup.name, bodies = setup.count, contacts = baseline.maxContacts, unpatched_median_ms = baseline.median,
                        unpatched_second_median_ms = secondBaseline.median, parallel_median_ms = optimized.median, patched_serial_median_ms = hookedSerial.median,
                        unpatched_p95_ms = baseline.p95, parallel_p95_ms = optimized.p95, parallel_prepare_calls = calls,
                        speedup = baseline.median / optimized.median, all_130_steps_bit_exact = true });
                }
                return new { rows, velocity_iterations = FarseerPhysics.Settings.VelocityIterations, position_iterations = FarseerPhysics.Settings.PositionIterations,
                    note = "Whole installed World.Step, original iteration counts. Synthetic stress: bodies reset before each step to maintain contacts; reset/state comparison excluded from timings. Two unpatched baselines bracket patched runs. Not a loaded submarine or multiplayer round." };
            }
            finally { harmony.UnpatchSelf(); Config.ParallelConstraintPreparation = enabled; Config.MinimumContactsForParallelPreparation = threshold; }
        }
    }
}
