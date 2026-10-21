using System;
using System.Collections.Generic;
using System.Linq;
using ReboundGuard;

namespace ReboundGuardTests
{
    public static class BucketStampTests
    {
        private static int checks;
        private static void Check(bool value, string message)
        {
            if (!value) throw new Exception("Bucket stamp: " + message);
            checks++;
        }
        private static BucketStamp Stamp(Dictionary<int, long> revisions, int[] keys)
        {
            var result = new BucketStamp();
            foreach (int key in keys) if (revisions.TryGetValue(key, out long version)) result.Include(version);
            return result;
        }
        public static object Run()
        {
            checks = 0;
            var revisions = new Dictionary<int, long> { [1] = 1, [2] = 2 };
            int[] targets = { 1, 2, 3 };
            var prior = Stamp(revisions, targets);
            revisions.Remove(1);
            Check(!prior.Equals(Stamp(revisions, targets)), "removal of a non-maximum bucket invalidates");
            revisions[3] = 3;
            Check(!prior.Equals(Stamp(revisions, targets)), "balanced deletion/insertion invalidates");
            prior = Stamp(revisions, targets);
            revisions[100] = 4;
            Check(prior.Equals(Stamp(revisions, targets)), "unrelated tag mutation preserves candidate stamp");
            revisions.Remove(2); revisions[2] = 5;
            Check(!prior.Equals(Stamp(revisions, targets)), "recreated bucket invalidates without a tombstone table");

            // Property: equal stamps must imply equal dependent bucket state.
            // Every changed/inserted bucket receives a globally newer revision.
            // Values model complete bucket contents/order, not just their counts.
            var random = new Random(719041);
            var contents = new Dictionary<int, int[]>();
            revisions.Clear();
            long clock = 0;
            int[][] targetSets = { new[] { 0 }, new[] { 0, 1 }, new[] { 2, 3, 4 }, Array.Empty<int>(), Enumerable.Range(0, 12).ToArray() };
            var oldStamps = new BucketStamp[targetSets.Length];
            var oldContents = targetSets.Select(_ => new Dictionary<int, int[]>()).ToArray();
            for (int step = 0; step < 20000; step++)
            {
                int changes = random.Next(1, 5);
                for (int change = 0; change < changes; change++)
                {
                    int key = random.Next(20);
                    if (random.Next(3) == 0) { revisions.Remove(key); contents.Remove(key); }
                    else
                    {
                        revisions[key] = ++clock;
                        contents[key] = Enumerable.Range(0, random.Next(1, 6)).Select(_ => random.Next(50)).ToArray();
                    }
                }
                for (int query = 0; query < targetSets.Length; query++)
                {
                    int[] keys = targetSets[query];
                    var current = Stamp(revisions, keys);
                    if (current.Equals(oldStamps[query]))
                    {
                        Check(keys.All(key => contents.ContainsKey(key) == oldContents[query].ContainsKey(key)), "unchanged stamp preserves presence of every dependent bucket");
                        Check(keys.Where(contents.ContainsKey).All(key => contents[key].SequenceEqual(oldContents[query][key])), "unchanged stamp preserves dependent contents and order");
                    }
                    oldStamps[query] = current;
                    oldContents[query] = keys.Where(contents.ContainsKey).ToDictionary(key => key, key => contents[key]);
                }
            }
            return new { checks, mutation_batches = 20000, dependency_sets = targetSets.Length,
                note = "Production BucketStamp checked against full independent bucket-content snapshots, including deletion/recreation and unrelated mutations. Requires globally monotonic revisions; item-list ordering invalidation remains separate. Not an executed Barotrauma nearby query." };
        }
    }
}
