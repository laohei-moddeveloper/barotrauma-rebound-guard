using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using ReboundGuard;

namespace ReboundGuardTests
{
    public static class QueueChurnTests
    {
        private static int checks;
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        private static readonly AccessTools.FieldRef<Queue<string>, int> Version = AccessTools.FieldRefAccess<Queue<string>, int>("_version");

        // The previous 0.4.0 strategy rebuilt after each dequeue. Retain it only
        // as a performance baseline for the same complete mixed FIFO workload.
        private sealed class Legacy
        {
            private readonly HashSet<string> members = new HashSet<string>();
            private bool valid;
            internal bool Contains(Queue<string> queue, string value)
            {
                if (!valid) { members.Clear(); foreach (string item in queue) members.Add(item); valid = true; }
                return members.Contains(value);
            }
            internal void Dequeue(Queue<string> queue) { queue.Dequeue(); valid = false; }
        }
        private static double Median(Action action)
        {
            action(); action();
            double[] samples = new double[7];
            for (int sample = 0; sample < samples.Length; sample++)
            {
                long start = Stopwatch.GetTimestamp(); action();
                samples[sample] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            }
            Array.Sort(samples); return samples[samples.Length / 2];
        }
        public static object Run()
        {
            var random = new Random(423601);
            var queue = new Queue<string>();
            for (int i = 0; i < 64; i++) queue.Enqueue(i % 3 == 0 ? null : (i % 7).ToString());
            var index = new QueueMembership<string>(value => Version(value));
            for (int step = 0; step < 30000; step++)
            {
                string value = random.Next(5) == 0 ? null : random.Next(32).ToString();
                switch (random.Next(9))
                {
                    case 0: case 1: index.Enqueue(queue, value); break;
                    case 2: queue.Enqueue(value); break;
                    case 3: case 4:
                        if (queue.Count > 0) { var expected = queue.Peek(); Check(index.Dequeue(queue) == expected, "FIFO result differs"); }
                        break;
                    case 5: if (queue.Count > 0) queue.Dequeue(); queue.Enqueue(value); break;
                    case 6:
                        queue.Clear();
                        for (int i = 0; i < 64; i++) queue.Enqueue(i % 3 == 0 ? null : random.Next(32).ToString());
                        break;
                    case 7: index.Reset(); break;
                }
                for (int probe = 0; probe < 3; probe++)
                {
                    string search = probe == 0 ? null : probe == 1 ? value : random.Next(32).ToString();
                    Check(index.Contains(queue, search) == queue.Contains(search), "Membership differs after mixed indexed/external mutation");
                }
            }
            queue.Clear(); index.Reset();
            foreach (string value in new[] { null, null, "duplicate", "duplicate" }) index.Enqueue(queue, value);
            Check(index.Contains(queue, null), "Null indexed");
            index.Dequeue(queue); Check(index.Contains(queue, null), "Remaining null retained");
            index.Dequeue(queue); Check(!index.Contains(queue, null), "Final null removed");
            index.Dequeue(queue); Check(index.Contains(queue, "duplicate"), "Remaining duplicate retained");
            index.Dequeue(queue); Check(!index.Contains(queue, "duplicate"), "Final duplicate removed");
            var alternate = new Queue<string>(new[] { "other" });
            Check(index.Contains(alternate, "other") && !index.Contains(queue, "other"), "Switching queues invalidates membership");
            bool threw = false;
            try { index.Dequeue(queue); } catch (InvalidOperationException) { threw = true; }
            Check(threw && queue.Count == 0, "Empty dequeue retains native exception");

            var benchmarks = new List<object>();
            foreach (int count in new[] { 256, 2048, 8192 })
            {
                string[] items = Enumerable.Range(0, count).Select(i => i.ToString()).ToArray();
                Action native = () => { var pending = new Queue<string>(items); while (pending.Count > 0) { if (!pending.Contains("missing")) pending.Dequeue(); } };
                Action legacy = () => { var pending = new Queue<string>(items); var old = new Legacy(); while (pending.Count > 0) { if (!old.Contains(pending, "missing")) old.Dequeue(pending); } };
                Action optimized = () => { var pending = new Queue<string>(items); var current = new QueueMembership<string>(value => Version(value)); while (pending.Count > 0) { if (!current.Contains(pending, "missing")) current.Dequeue(pending); } };
                double nativeMs = Median(native), legacyMs = Median(legacy), optimizedMs = Median(optimized);
                benchmarks.Add(new { pending = count, native_ms = nativeMs, v040_ms = legacyMs, counted_ms = optimizedMs,
                    speedup_over_native = nativeMs / optimizedMs, speedup_over_v040 = legacyMs / optimizedMs });
            }
            return new { checks, benchmarks, limitation = "Synthetic complete FIFO drain with interleaved missing-member lookups; includes queue/index creation. Not a packet-writing, whole-game or multiplayer latency benchmark." };
        }
    }
}
