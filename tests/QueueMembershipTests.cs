using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using ReboundGuard;

namespace ReboundGuardTests
{
    // The same suite runs in the portable .NET 8 host and the real-game harness.
    public static class QueueMembershipTests
    {
        private static int checks;
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Queue membership: " + message);
            checks++;
        }
        private static Func<Queue<T>, int> Version<T>()
        {
            var queue = Expression.Parameter(typeof(Queue<T>), "queue");
            return Expression.Lambda<Func<Queue<T>, int>>(Expression.Field(queue, "_version"), queue).Compile();
        }
        private static void Randomized()
        {
            var queues = new[] { new Queue<int>(), new Queue<int>() };
            var expected = new[] { new Queue<int>(), new Queue<int>() };
            var index = new QueueMembership<int>(Version<int>());
            var random = new Random(361871);
            for (int step = 0; step < 16000; step++)
            {
                int q = random.Next(2), value = random.Next(32);
                Queue<int> queue = queues[q], reference = expected[q];
                switch (random.Next(9))
                {
                    case 0: case 1: index.Enqueue(queue, value); reference.Enqueue(value); break;
                    case 2: queue.Enqueue(value); reference.Enqueue(value); break;
                    case 3:
                        if (reference.Count > 0) Check(index.Dequeue(queue) == reference.Dequeue(), "FIFO dequeue");
                        break;
                    case 4:
                        if (reference.Count > 0) { queue.Dequeue(); reference.Dequeue(); }
                        queue.Enqueue(value); reference.Enqueue(value); break;
                    case 5: queue.Clear(); reference.Clear(); break;
                    case 6: index.Reset(); break;
                    case 7:
                        if (reference.Count > 0) { queue.Dequeue(); reference.Dequeue(); }
                        index.Enqueue(queue, value); reference.Enqueue(value); break;
                }
                for (int other = 0; other < queues.Length; other++)
                {
                    Check(queues[other].SequenceEqual(expected[other]), "queue contents and order");
                    Check(index.Contains(queues[other], value) == expected[other].Contains(value), "membership after mutation or queue switch");
                    Check(index.Contains(queues[other], (value + 1) % 32) == expected[other].Contains((value + 1) % 32), "absent/present membership");
                }
            }
        }
        private static void NullsAndDuplicates()
        {
            var queue = new Queue<string>(new[] { null, "a", "a", null });
            var index = new QueueMembership<string>(Version<string>());
            Check(index.Contains(queue, null), "initial null");
            Check(index.Dequeue(queue) == null && index.Contains(queue, null), "duplicate null retained");
            Check(index.Dequeue(queue) == "a" && index.Contains(queue, "a"), "duplicate value retained");
            Check(index.Dequeue(queue) == "a" && !index.Contains(queue, "a"), "last value removed");
            Check(index.Dequeue(queue) == null && !index.Contains(queue, null), "last null removed");
            bool threw = false;
            try { index.Dequeue(queue); } catch (InvalidOperationException) { threw = true; }
            Check(threw && queue.Count == 0, "empty dequeue preserves native exception");
            index.Enqueue(queue, null); index.Enqueue(queue, null);
            Check(index.Contains(queue, null) && index.Dequeue(queue) == null && index.Contains(queue, null), "tracked null enqueues");
            queue.Clear(); queue.Enqueue("replacement");
            Check(!index.Contains(queue, null) && index.Contains(queue, "replacement"), "external clear");
            queue.Dequeue(); queue.Enqueue("changed");
            Check(!index.Contains(queue, "replacement") && index.Contains(queue, "changed"), "external same-size replacement");
            var nullable = new Queue<int?>(new int?[] { null, 3, null });
            var nullableIndex = new QueueMembership<int?>(Version<int?>());
            Check(nullableIndex.Contains(nullable, null), "nullable empty value");
            nullableIndex.Dequeue(nullable); nullableIndex.Dequeue(nullable);
            Check(nullableIndex.Contains(nullable, null) && !nullableIndex.Contains(nullable, 3), "nullable tracked removals");
            nullableIndex.Dequeue(nullable);
            Check(!nullableIndex.Contains(nullable, null), "nullable drained");
        }
        private sealed class Key
        {
            internal static long HashCalls;
            internal readonly int Id;
            internal bool Throw;
            internal Key(int id) { Id = id; }
            public override bool Equals(object other) => other is Key key && key.Id == Id;
            public override int GetHashCode()
            {
                HashCalls++;
                if (Throw) throw new InvalidOperationException("expected hash failure");
                return Id;
            }
        }
        private static long IncrementalWork()
        {
            const int count = 4096;
            var keys = Enumerable.Range(0, count).Select(i => new Key(i)).ToArray();
            var queue = new Queue<Key>(keys);
            var index = new QueueMembership<Key>(Version<Key>());
            Check(index.Contains(queue, new Key(0)), "equality uses value rather than identity");
            long before = Key.HashCalls;
            for (int i = 0; i < count; i++)
            {
                Check(index.Dequeue(queue).Id == i, "drain FIFO");
                Check(!index.Contains(queue, keys[i]), "drained item absent");
                if (i + 1 < count) Check(index.Contains(queue, keys[i + 1]), "remaining item present");
            }
            long hashes = Key.HashCalls - before;
            Check(hashes <= count * 8L, "drain/query work stays linear instead of rehashing the remaining queue");
            var equal = new Queue<Key>(new[] { new Key(7), new Key(7) });
            Check(index.Contains(equal, new Key(7)), "equal but distinct entries");
            index.Dequeue(equal);
            Check(index.Contains(equal, new Key(7)), "equal duplicate retained");
            index.Dequeue(equal);
            Check(!index.Contains(equal, new Key(7)), "equal duplicate removed");
            var bad = new Key(9) { Throw = true };
            bool threw = false;
            try { index.Enqueue(equal, bad); } catch (InvalidOperationException) { threw = true; }
            Check(threw && equal.Count == 1, "hash failure preserves completed native enqueue");
            bad.Throw = false;
            Check(index.Contains(equal, new Key(9)), "partial index is rebuilt after hash failure");
            return hashes;
        }
        private static object CountedHashWork()
        {
            var keys = Enumerable.Range(0, 4096).Select(i => new Key(i)).ToArray();
            var queue = new Queue<Key>(keys);
            var index = new QueueMembership<Key>(Version<Key>());
            long before = Key.HashCalls;
            Check(index.Contains(queue, keys[0]), "counted rebuild sees actual first key");
            long cold = Key.HashCalls - before;
            Check(cold <= keys.Length + 4, "count-map rebuild hashes each entry once");
            var duplicateQueue = new Queue<Key>(new[] { new Key(7) });
            Check(index.Contains(duplicateQueue, new Key(7)), "counted duplicate setup");
            before = Key.HashCalls;
            for (int i = 0; i < 1000; i++) index.Enqueue(duplicateQueue, new Key(7));
            long added = Key.HashCalls - before;
            Check(added <= 1000, "counted enqueue uses one dictionary probe");
            before = Key.HashCalls;
            for (int i = 0; i < 1000; i++) Check(index.Dequeue(duplicateQueue).Id == 7, "duplicate drain FIFO");
            long removed = Key.HashCalls - before;
            Check(removed <= 1000, "remaining duplicates decrement with one dictionary probe");
            Check(index.Contains(duplicateQueue, new Key(7)) && duplicateQueue.Count == 1, "last duplicate remains after counted drain");
            index.Dequeue(duplicateQueue);
            Check(!index.Contains(duplicateQueue, new Key(7)), "final counted duplicate removed");
            return new { cold_4096_hash_calls = cold, duplicate_1000_enqueue_hash_calls = added, duplicate_1000_dequeue_hash_calls = removed };
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference PopulateExpired(QueueMembershipCache<Key> cache)
        {
            var queue = new Queue<Key>(new[] { new Key(1) });
            cache.Get(queue).Contains(queue, new Key(1));
            cache.Finish();
            return new WeakReference(queue);
        }
        private static void CacheBoundaries()
        {
            var cache = new QueueMembershipCache<Key>(Version<Key>());
            var first = new Queue<Key>(Enumerable.Range(0, 4096).Select(i => new Key(i)));
            var second = new Queue<Key>(new[] { new Key(-1) });
            var firstIndex = cache.Get(first);
            Check(firstIndex.Contains(first, new Key(0)), "initial send indexed");
            cache.Finish();
            Check(cache.Get(second).Contains(second, new Key(-1)), "different client queue");
            cache.Finish();
            long before = Key.HashCalls;
            Check(ReferenceEquals(firstIndex, cache.Get(first)), "same queue reuses index across send/client boundaries");
            Check(cache.Get(first).Contains(first, new Key(0)), "warm send membership");
            Check(Key.HashCalls - before < 8, "warm send does not rescan native queue");
            first.Dequeue(); first.Enqueue(new Key(5000));
            cache.Finish();
            Check(!cache.Get(first).Contains(first, new Key(0)) && cache.Get(first).Contains(first, new Key(5000)), "external mutation between sends invalidates cache");
            cache.Finish();
            cache.Clear();
            Check(!ReferenceEquals(firstIndex, cache.Get(first)), "round/reset clears cached indexes even when native queue stays alive");
            Check(cache.Get(first).Contains(first, new Key(5000)), "fresh index after round/reset");
            cache.Finish();
            WeakReference expired = PopulateExpired(cache);
            for (int i = 0; i < 3 && expired.IsAlive; i++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            Check(!expired.IsAlive, "weak cache cannot retain disconnected queue even when its value references its key");
            GC.KeepAlive(cache);
        }
        // Exact previous helper, retained only as a benchmark control.
        private sealed class Legacy
        {
            private readonly HashSet<int> members = new HashSet<int>();
            private readonly Func<Queue<int>, int> version;
            private Queue<int> source;
            private int observed;
            private bool valid;
            internal Legacy(Func<Queue<int>, int> version) { this.version = version; }
            internal bool Contains(Queue<int> queue, int value)
            {
                int current = version(queue);
                if (!valid || source != queue || current != observed)
                {
                    members.Clear(); foreach (int item in queue) members.Add(item);
                    source = queue; observed = current; valid = true;
                }
                return members.Contains(value);
            }
            internal void Enqueue(Queue<int> queue, int value)
            {
                int before = version(queue); queue.Enqueue(value);
                if (valid && source == queue && observed == before) { members.Add(value); observed = version(queue); }
                else valid = false;
            }
            internal int Dequeue(Queue<int> queue) { int value = queue.Dequeue(); valid = false; return value; }
            internal void Reset() { source = null; members.Clear(); valid = false; }
        }
        private static double Median(Action action)
        {
            action(); action();
            var times = new double[7];
            for (int i = 0; i < times.Length; i++)
            {
                long start = Stopwatch.GetTimestamp(); action();
                times[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            }
            Array.Sort(times); return times[times.Length / 2];
        }
        private static object Benchmark(int count, string workload)
        {
            var queue = new Queue<int>(count);
            var version = Version<int>();
            var old = new Legacy(version);
            var current = new QueueMembership<int>(version);
            // Setup and initial indexing are included in both paths.
            Action legacy = () =>
            {
                queue.Clear(); old.Reset();
                for (int i = 0; i < count; i++) { if (!old.Contains(queue, i)) old.Enqueue(queue, i); }
                if (workload == "append") return;
                for (int i = 0; i < count; i++)
                {
                    old.Dequeue(queue); old.Contains(queue, i);
                    if (workload == "rotate") old.Enqueue(queue, i + count);
                }
            };
            Action incremental = () =>
            {
                queue.Clear(); current.Reset();
                for (int i = 0; i < count; i++) { if (!current.Contains(queue, i)) current.Enqueue(queue, i); }
                if (workload == "append") return;
                for (int i = 0; i < count; i++)
                {
                    current.Dequeue(queue); current.Contains(queue, i);
                    if (workload == "rotate") current.Enqueue(queue, i + count);
                }
            };
            double first = Median(legacy), after = Median(incremental), second = Median(legacy);
            return new { workload, items = count, legacy_first_ms = first, incremental_ms = after, legacy_second_ms = second };
        }
        private static object SendBenchmark(int count)
        {
            var version = Version<int>();
            var queue = new Queue<int>(count);
            var old = new Legacy(version);
            var cache = new QueueMembershipCache<int>(version);
            const int packets = 24, sentPerPacket = 16;
            Action legacy = () =>
            {
                queue.Clear(); old.Reset();
                for (int i = 0; i < count; i++) queue.Enqueue(i);
                for (int packet = 0; packet < packets; packet++)
                {
                    int first = packet * sentPerPacket;
                    for (int i = first; i < first + count; i++) if (!old.Contains(queue, i)) old.Enqueue(queue, i);
                    for (int i = 0; i < sentPerPacket; i++) old.Dequeue(queue);
                    old.Reset(); // Previous ClientWriteIngame finalizer.
                }
            };
            Action incremental = () =>
            {
                queue.Clear(); cache.Finish();
                for (int i = 0; i < count; i++) queue.Enqueue(i);
                for (int packet = 0; packet < packets; packet++)
                {
                    int first = packet * sentPerPacket;
                    for (int i = first; i < first + count; i++)
                        if (!cache.Get(queue).Contains(queue, i)) cache.Get(queue).Enqueue(queue, i);
                    for (int i = 0; i < sentPerPacket; i++) cache.Get(queue).Dequeue(queue);
                    cache.Finish();
                }
            };
            double firstMs = Median(legacy), after = Median(incremental), secondMs = Median(legacy);
            return new { workload = "send_batches", items = count, packets, sent_per_packet = sentPerPacket,
                legacy_first_ms = firstMs, incremental_ms = after, legacy_second_ms = secondMs };
        }
        public static object Run()
        {
            checks = 0;
            Randomized(); NullsAndDuplicates(); long hashes = IncrementalWork(); CacheBoundaries(); object counted = CountedHashWork();
            var rows = new List<object>();
            foreach (int count in new[] { 256, 2048, 8192 })
            {
                foreach (string workload in new[] { "append", "drain", "rotate" }) rows.Add(Benchmark(count, workload));
                rows.Add(SendBenchmark(count));
            }
            return new { checks, drain_4096_hash_calls = hashes, counted_hash_work = counted, benchmarks = rows,
                note = "Actual QueueMembership source; deterministic FIFO/membership checks and synthetic single-thread queues. Includes old-helper controls before and after each run. No game networking, packet replay, FPS or visible rubberband measurement." };
        }
    }
}
