using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ReboundGuard;
using Config = ReboundGuard.Settings;

namespace ReboundGuardTests
{
    public static class NearbyItemIndexTests
    {
        private static int checks;
        private static readonly MethodInfo BeginMethod = AccessTools.Method(typeof(NearbyItemIndex), "BeginSimulation");
        private static readonly MethodInfo EndMethod = AccessTools.Method(typeof(NearbyItemIndex), "EndSimulation");
        private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; }
        private static Item MakeItem(int index)
        {
            var prefab = (ItemPrefab)RuntimeHelpers.GetUninitializedObject(typeof(ItemPrefab));
            AccessTools.Field(typeof(MapEntityPrefab), "Identifier").SetValue(prefab, ("prefab" + index % 31).ToIdentifier());
            AccessTools.Field(typeof(ItemPrefab), "tags").SetValue(prefab, ImmutableHashSet.Create("prefabtag".ToIdentifier()));
            var item = (Item)RuntimeHelpers.GetUninitializedObject(typeof(Item));
            AccessTools.Field(typeof(MapEntity), "Prefab").SetValue(item, prefab);
            AccessTools.Field(typeof(Item), "tags").SetValue(item, new HashSet<Identifier> { ("tag" + index % 17).ToIdentifier() });
            AccessTools.Field(typeof(Item), "allPropertyObjects").SetValue(item, new List<ISerializableEntity> { item, item });
            AccessTools.Field(typeof(MapEntity), "rect").SetValue(item, new Rectangle(index % 80 - 40, index % 100 - 50, 1, 1));
            return item;
        }
        private static StatusEffect Effect(params string[] identifiers)
        {
            var effect = (StatusEffect)RuntimeHelpers.GetUninitializedObject(typeof(StatusEffect));
            AccessTools.Field(typeof(StatusEffect), "targetTypes").SetValue(effect, StatusEffect.TargetType.NearbyItems);
            AccessTools.Field(typeof(StatusEffect), "TargetIdentifiers").SetValue(effect, identifiers == null ? null : identifiers.Select(s => s.ToIdentifier()).ToImmutableHashSet());
            AccessTools.PropertySetter(typeof(StatusEffect), "Range").Invoke(effect, new object[] { 35f });
            return effect;
        }
        private static void Compare(StatusEffect effect, Vector2 point)
        {
            var native = new List<ISerializableEntity>(); var indexed = new List<ISerializableEntity>();
            EndMethod.Invoke(null, null); effect.AddNearbyTargets(point, native);
            BeginMethod.Invoke(null, null); effect.AddNearbyTargets(point, indexed);
            Check(native.SequenceEqual(indexed), "native target membership, order and duplicate property objects preserved");
        }
        private static long Counter(string name) => (long)AccessTools.Field(typeof(NearbyItemIndex), name).GetValue(null);
        private static void FrozenBackgroundSnapshot()
        {
            Item item = MakeItem(97531);
            var tags = (HashSet<Identifier>)item.GetTags();
            tags.Add("captured_tag".ToIdentifier());
            Type snapshot = typeof(NearbyItemIndex).GetNestedType("Snapshot", BindingFlags.NonPublic);
            object source = Activator.CreateInstance(snapshot);
            var fields = new Dictionary<string, object> {
                ["Item"] = item, ["Prefab"] = item.Prefab, ["Identifier"] = item.Prefab.Identifier,
                ["PrefabTags"] = item.Prefab.Tags, ["Tags"] = tags,
                ["Version"] = (int)AccessTools.Field(typeof(HashSet<Identifier>), "_version").GetValue(tags), ["Count"] = tags.Count,
                ["DynamicTags"] = tags.ToArray()
            };
            foreach (var pair in fields) AccessTools.Field(snapshot, pair.Key).SetValue(source, pair.Value);
            Array sources = Array.CreateInstance(snapshot, 1); sources.SetValue(source, 0);
            tags.Clear(); tags.Add("later_tag".ToIdentifier());
            object built = System.Threading.Tasks.Task.Run(() => AccessTools.Method(typeof(NearbyItemIndex), "BuildSnapshot")
                .Invoke(null, new object[] { sources, 0 })).GetAwaiter().GetResult();
            var nodes = (System.Collections.IDictionary)AccessTools.Field(built.GetType(), "Nodes").GetValue(built);
            object node = nodes[item];
            var keys = (Identifier[])AccessTools.Field(node.GetType(), "Keys").GetValue(node);
            Check(keys.Contains("captured_tag".ToIdentifier()) && !keys.Contains("later_tag".ToIdentifier()),
                "background builder reads the frozen dynamic-tag array rather than the mutable game HashSet");
            Check((bool)AccessTools.Method(typeof(NearbyItemIndex), "HasChanged").Invoke(null, new[] { node }),
                "main-thread audit detects mutations since the frozen snapshot before indexed results are used");
        }
        private static void TailMaintenanceChecks(int originalCount)
        {
            int count = Item.ItemList.Count;
            var stable = Effect("tag2");
            var unique = Effect("tail_old_key");
            foreach (StatusEffect effect in new[] { stable, unique })
                AccessTools.PropertySetter(typeof(StatusEffect), "Range").Invoke(effect, new object[] { 1000f });
            var tail = MakeItem(90001);
            ((HashSet<Identifier>)tail.GetTags()).Add("tail_old_key".ToIdentifier());
            Compare(stable, Vector2.Zero);
            long appends = Counter("tailAppends");
            Item.ItemList.Add(tail);
            Compare(unique, Vector2.Zero);
            Check(Counter("tailAppends") == appends + 1, "appended tail uses prefix-validated maintenance");
            var table = AccessTools.Field(typeof(NearbyItemIndex), "Candidates").GetValue(null);
            object[] lookup = { unique.TargetIdentifiers, null };
            Check((bool)table.GetType().GetMethod("TryGetValue").Invoke(table, lookup), "former-tag candidate exists");
            var oldCandidates = (List<Item>)AccessTools.Field(lookup[1].GetType(), "Items").GetValue(lookup[1]);
            Check(oldCandidates.Contains(tail), "former-tag candidate initially retains tail");
            ((HashSet<Identifier>)tail.GetTags()).Remove("tail_old_key".ToIdentifier());
            ((HashSet<Identifier>)tail.GetTags()).Add("tail_new_key".ToIdentifier());
            Compare(stable, Vector2.Zero); // Audit changes without querying the old target set.
            Check(oldCandidates.Contains(tail), "stale former-tag candidate retained before structural removal");
            long removals = Counter("tailRemovals");
            Item.ItemList.RemoveAt(Item.ItemList.Count - 1);
            Compare(stable, Vector2.Zero);
            Check(Counter("tailRemovals") == removals + 1 && oldCandidates.Count == 0,
                "tail removal releases references in candidates under former tags");
            Compare(unique, Vector2.Zero);

            // Same version/count delta as two Adds, but actually a removal plus
            // AddRange(3). Version arithmetic cannot establish prefix identity.
            Item removed = Item.ItemList[originalCount + 10];
            appends = Counter("tailAppends");
            Item.ItemList.RemoveAt(originalCount + 10);
            Item.ItemList.AddRange(new[] { MakeItem(91001), MakeItem(91002), MakeItem(91003) });
            Compare(stable, Vector2.Zero);
            Check(Counter("tailAppends") == appends, "misleading version/count deltas fall back after prefix mismatch");
            Item.ItemList.RemoveRange(Item.ItemList.Count - 3, 3);
            Item.ItemList.Insert(originalCount + 10, removed);
            Compare(stable, Vector2.Zero);

            appends = Counter("tailAppends");
            Item.ItemList.AddRange(Enumerable.Range(0, 65).Select(i => MakeItem(92000 + i)));
            Compare(stable, Vector2.Zero);
            Check(Counter("tailAppends") == appends, "large list changes keep general rebuild path");
            Item.ItemList.RemoveRange(count, 65);
            Compare(stable, Vector2.Zero);
            Item.ItemList[originalCount + 1] = Item.ItemList[originalCount + 1];
            long unchanged = Counter("unchangedSequences");
            Compare(stable, Vector2.Zero);
            Check(Counter("unchangedSequences") == unchanged + 1, "identical reference sequence still performs real tag audit");
        }
        public static object Run()
        {
            FrozenBackgroundSnapshot();
            Check((bool)AccessTools.Method(typeof(NearbyItemIndex), "SupportedCodeMatches").Invoke(null, null), "installed nearby loop and audited assembly fingerprints");
            bool enabled = Config.UseNearbyItemIndex; int minimum = Config.MinimumItemsForNearbyIndex;
            bool background = Config.BackgroundNearbyIndexBuild;
            bool incremental = Config.IncrementalNearbyListMaintenance;
            int auditMinimum = Config.MinimumItemsForParallelTagAudit;
            int originalCount = Item.ItemList.Count;
            var harmony = new Harmony("barotrauma.reboundguard.experimental.v040");
            var foreign = new Harmony("reboundguard.nearby.foreign.test");
            try
            {
                Config.UseNearbyItemIndex = true; Config.MinimumItemsForNearbyIndex = 1;
                Config.BackgroundNearbyIndexBuild = false;
                Config.IncrementalNearbyListMaintenance = true;
                Config.MinimumItemsForParallelTagAudit = 1;
                for (int i = 0; i < 2048; i++) Item.ItemList.Add(MakeItem(i));
                var effects = new[] { Effect("tag2"), Effect("tag2", "prefab3"), Effect("prefab3"), Effect("absent"), Effect("prefabtag"), Effect("item"), Effect(Array.Empty<string>()), Effect(null) };
                AccessTools.Method(typeof(NearbyItemIndex), "Install").Invoke(null, new object[] { harmony });
                long beforeSafety = (long)AccessTools.Field(typeof(NearbyItemIndex), "safetyChecks").GetValue(null);
                for (int i = 0; i < 100; i++) BeginMethod.Invoke(null, null);
                Check((long)AccessTools.Field(typeof(NearbyItemIndex), "safetyChecks").GetValue(null) == beforeSafety, "idle simulation steps do not scan Harmony patches");
                string idleReport = (string)AccessTools.Method(typeof(NearbyItemIndex), "Report").Invoke(null, null);
                Check(idleReport.Contains("installed=True") && !idleReport.Contains("not installed"), "installed idle index is reported truthfully");
                foreach (var effect in effects) Compare(effect, Vector2.Zero);
                BeginMethod.Invoke(null, null);
                beforeSafety = (long)AccessTools.Field(typeof(NearbyItemIndex), "safetyChecks").GetValue(null);
                for (int i = 0; i < 8; i++) effects[0].AddNearbyTargets(Vector2.Zero, new List<ISerializableEntity>());
                Check((long)AccessTools.Field(typeof(NearbyItemIndex), "safetyChecks").GetValue(null) == beforeSafety + 1, "eligible queries share one conflict check in a step");
                // Unrelated tag changes must not force every candidate union/sort.
                Item probe = Item.ItemList[originalCount + 40];
                Compare(effects[0], Vector2.Zero);
                long candidateBuilds = (long)AccessTools.Field(typeof(NearbyItemIndex), "candidateRebuilds").GetValue(null);
                ((HashSet<Identifier>)probe.GetTags()).Add("unrelated_probe".ToIdentifier());
                Compare(effects[0], Vector2.Zero);
                Check((long)AccessTools.Field(typeof(NearbyItemIndex), "candidateRebuilds").GetValue(null) == candidateBuilds,
                    "unrelated tag change preserves cached candidates after live audit");
                ((HashSet<Identifier>)probe.GetTags()).Add("tag2".ToIdentifier());
                Compare(effects[0], Vector2.Zero);
                Check((long)AccessTools.Field(typeof(NearbyItemIndex), "candidateRebuilds").GetValue(null) == candidateBuilds + 1,
                    "related tag change rebuilds candidates");
                ((HashSet<Identifier>)probe.GetTags()).Add("probe_only".ToIdentifier());
                var dependent = Effect("tag2", "probe_only");
                Compare(dependent, Vector2.Zero);
                Item nextProbe = Item.ItemList[originalCount + 41];
                ((HashSet<Identifier>)nextProbe.GetTags()).Add("tag2".ToIdentifier());
                Compare(dependent, Vector2.Zero); // tag2 now has a newer revision.
                ((HashSet<Identifier>)probe.GetTags()).Remove("probe_only".ToIdentifier());
                Compare(dependent, Vector2.Zero); // Delete the non-maximum bucket.
                ((HashSet<Identifier>)probe.GetTags()).Add("probe_only".ToIdentifier());
                Compare(dependent, Vector2.Zero); // Recreate the removed bucket.
                Compare(effects[4], Vector2.Zero); // Dense fallback discards its candidate list.
                ((HashSet<Identifier>)probe.GetTags()).Add("another_unrelated_probe".ToIdentifier());
                Compare(effects[4], Vector2.Zero); // Discarded candidates cannot mean empty targets.
                TailMaintenanceChecks(originalCount);
                var random = new Random(506782);
                for (int step = 0; step < 240; step++)
                {
                    Item item = Item.ItemList[originalCount + random.Next(Item.ItemList.Count - originalCount)];
                    switch (step % 14)
                    {
                        case 0: item.AddTag("tag2".ToIdentifier()); break;
                        case 1: item.RemoveTag("tag2".ToIdentifier()); break;
                        case 2: item.ReplaceTag("tag2".ToIdentifier(), "tag3".ToIdentifier()); break;
                        case 3: item.Tags = "tag2,tag3"; break;
                        case 4: Item.ItemList.Remove(item); Item.ItemList.Add(MakeItem(step)); break;
                        case 5: Item.ItemList[originalCount + 1] = MakeItem(step + 10000); break;
                        case 6: Item.ItemList.Reverse(originalCount, Item.ItemList.Count - originalCount); break;
                        case 7: AccessTools.Field(typeof(MapEntity), "rect").SetValue(item, new Rectangle(2000, 2000, 1, 1)); break;
                        case 8: ((HashSet<Identifier>)item.GetTags()).Add("tag2".ToIdentifier()); break;
                        case 9: ((HashSet<Identifier>)item.GetTags()).Remove("tag2".ToIdentifier()); break;
                        case 10: ((HashSet<Identifier>)item.GetTags()).Clear(); break;
                        case 11: ((HashSet<Identifier>)item.GetTags()).Remove("tag2".ToIdentifier()); ((HashSet<Identifier>)item.GetTags()).Add("tag3".ToIdentifier()); break;
                        case 12: AccessTools.Field(typeof(ItemPrefab), "tags").SetValue(item.Prefab, ImmutableHashSet.Create("tag2".ToIdentifier())); break;
                        case 13: AccessTools.Field(typeof(Item), "tags").SetValue(item, new HashSet<Identifier> { "tag3".ToIdentifier() }); break;
                    }
                    foreach (var effect in effects.Take(4)) Compare(effect, new Vector2(step % 17 - 8, step % 13 - 6));
                }
                Item.ItemList.Add(Item.ItemList[originalCount]);
                Compare(effects[0], Vector2.Zero);
                var candidateQuery = (Func<StatusEffect, Vector2, List<Item>>)AccessTools.Method(typeof(NearbyItemIndex), "GetCandidates").CreateDelegate(typeof(Func<StatusEffect, Vector2, List<Item>>));
                Func<StatusEffect, List<Item>> candidates = effect => candidateQuery(effect, Vector2.Zero);
                Check(ReferenceEquals(candidates(effects[0]), Item.ItemList), "duplicate native list entries use original enumeration");
                Item.ItemList.RemoveAt(Item.ItemList.Count - 1);
                Compare(effects[0], Vector2.Zero);
                foreign.Patch(AccessTools.Method(typeof(Item), "GetTags"), prefix: new HarmonyMethod(typeof(NearbyItemIndexTests), nameof(EmptyPrefix)));
                BeginMethod.Invoke(null, null);
                Check(ReferenceEquals(candidates(effects[0]), Item.ItemList), "foreign tag getter hook forces original enumeration");
                foreign.UnpatchSelf();
                Config.UseNearbyItemIndex = false; BeginMethod.Invoke(null, null);
                Check(ReferenceEquals(candidates(effects[0]), Item.ItemList), "disabled path uses original enumeration");
                Config.UseNearbyItemIndex = true;
                Config.BackgroundNearbyIndexBuild = true;
                Config.MinimumItemsForParallelTagAudit = auditMinimum;
                AccessTools.Method(typeof(NearbyItemIndex), "ResetCache").Invoke(null, null);
                BeginMethod.Invoke(null, null);
                Check(ReferenceEquals(candidates(effects[0]), Item.ItemList), "initial asynchronous query uses native list while building");
                Item.ItemList.Add(MakeItem(99999));
                ((HashSet<Identifier>)Item.ItemList[originalCount].GetTags()).Add("tag2".ToIdentifier());
                var settle = Stopwatch.StartNew();
                while (ReferenceEquals(candidates(effects[0]), Item.ItemList) && settle.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(1);
                Check(!ReferenceEquals(candidates(effects[0]), Item.ItemList), "stale background snapshot discarded and rebuilt for changed list");
                Compare(effects[0], Vector2.Zero);
                var benchmarks = new List<object>();
                foreach (int count in new[] { 512, 4096, 16384 })
                {
                    while (Item.ItemList.Count > originalCount) Item.ItemList.RemoveAt(Item.ItemList.Count - 1);
                    for (int i = 0; i < count; i++) Item.ItemList.Add(MakeItem(i));
                    var effect = Effect("tag2");
                    var targets = new List<ISerializableEntity>();
                    Action query = () => { targets.Clear(); effect.AddNearbyTargets(Vector2.Zero, targets); };
                    var originalMethod = AccessTools.Method(typeof(StatusEffect), "AddNearbyTargets");
                    harmony.Unpatch(originalMethod, AccessTools.Method(typeof(NearbyItemIndex), "BeforeQuery"));
                    EndMethod.Invoke(null, null); for (int i = 0; i < 30; i++) query(); var unpatched = Time(query);
                    var unpatchedBatch = Time(() => { for (int i = 0; i < 100; i++) query(); }, 1);
                    harmony.Patch(originalMethod, prefix: new HarmonyMethod(typeof(NearbyItemIndex), "BeforeQuery"));
                    EndMethod.Invoke(null, null); for (int i = 0; i < 30; i++) query(); var native = Time(query);
                    AccessTools.Method(typeof(NearbyItemIndex), "ResetCache").Invoke(null, null);
                    BeginMethod.Invoke(null, null); long start = Stopwatch.GetTimestamp(); query();
                    double cold = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    var ready = Stopwatch.StartNew();
                    while (ReferenceEquals(candidates(effect), Item.ItemList) && ready.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(1);
                    Check(!ReferenceEquals(candidates(effect), Item.ItemList), "background-built index commits after version validation");
                    double readyMs = ready.Elapsed.TotalMilliseconds;
                    Compare(effect, Vector2.Zero);
                    for (int i = 0; i < 30; i++) query(); var indexed = Time(query);
                    Action manyQueries = () => { for (int i = 0; i < 100; i++) query(); };
                    EndMethod.Invoke(null, null); var nativeBatch = Time(manyQueries, 1);
                    BeginMethod.Invoke(null, null); var indexedBatch = Time(manyQueries, 1);
                    bool auditSetting = Config.ParallelNearbyTagAudit;
                    Config.ParallelNearbyTagAudit = false; var serialAudit = Time(query); Config.ParallelNearbyTagAudit = auditSetting;
                    Item.ItemList.Add(MakeItem(count + 100));
                    long rebuildStart = Stopwatch.GetTimestamp(); query();
                    double appendQuery = (Stopwatch.GetTimestamp() - rebuildStart) * 1000.0 / Stopwatch.Frequency;
                    Item.ItemList.RemoveAt(Item.ItemList.Count - 1);
                    rebuildStart = Stopwatch.GetTimestamp(); query();
                    double removeQuery = (Stopwatch.GetTimestamp() - rebuildStart) * 1000.0 / Stopwatch.Frequency;
                    var mutationItem = MakeItem(count + 200);
                    Action mutationQueries = () =>
                    {
                        Item.ItemList.Add(mutationItem); query();
                        Item.ItemList.RemoveAt(Item.ItemList.Count - 1); query();
                    };
                    Config.IncrementalNearbyListMaintenance = false;
                    for (int i = 0; i < 4; i++) mutationQueries();
                    var mutationBefore = Time(mutationQueries, 1);
                    Config.IncrementalNearbyListMaintenance = true;
                    for (int i = 0; i < 4; i++) mutationQueries();
                    var mutationAfter = Time(mutationQueries, 1);
                    Config.IncrementalNearbyListMaintenance = false;
                    var mutationControl = Time(mutationQueries, 1);
                    Config.IncrementalNearbyListMaintenance = true;
                    for (int i = originalCount; i < Item.ItemList.Count; i++) AccessTools.Field(typeof(MapEntity), "rect").SetValue(Item.ItemList[i], new Rectangle(100000 + i, 0, 1, 1));
                    harmony.Unpatch(originalMethod, AccessTools.Method(typeof(NearbyItemIndex), "BeforeQuery"));
                    EndMethod.Invoke(null, null); var farNative = Time(query);
                    harmony.Patch(originalMethod, prefix: new HarmonyMethod(typeof(NearbyItemIndex), "BeforeQuery"));
                    BeginMethod.Invoke(null, null); var farIndexed = Time(query); Compare(effect, Vector2.Zero);
                    for (int i = originalCount; i < Item.ItemList.Count; i++) AccessTools.Field(typeof(MapEntity), "rect").SetValue(Item.ItemList[i], new Rectangle(i % 80 - 40, i % 100 - 50, 1, 1));
                    var denseEffect = Effect("prefabtag");
                    Action denseQuery = () => { targets.Clear(); denseEffect.AddNearbyTargets(Vector2.Zero, targets); };
                    harmony.Unpatch(originalMethod, AccessTools.Method(typeof(NearbyItemIndex), "BeforeQuery"));
                    EndMethod.Invoke(null, null); var denseNative = Time(denseQuery);
                    harmony.Patch(originalMethod, prefix: new HarmonyMethod(typeof(NearbyItemIndex), "BeforeQuery"));
                    BeginMethod.Invoke(null, null); denseQuery(); var denseIndexed = Time(denseQuery); Compare(denseEffect, Vector2.Zero);
                    benchmarks.Add(new { items = count, unpatched_median_ms = unpatched.median, patched_fallback_median_ms = native.median, indexed_median_ms = indexed.median, cold_query_ms = cold,
                        far_native_ms = farNative.median, far_indexed_ms = farIndexed.median, dense_native_ms = denseNative.median, dense_fallback_ms = denseIndexed.median,
                        background_ready_delay_ms = readyMs, indexed_serial_audit_ms = serialAudit.median, append_query_ms = appendQuery, remove_query_ms = removeQuery,
                        append_remove_queries_full_before_ms = mutationBefore.median, append_remove_queries_incremental_ms = mutationAfter.median,
                        append_remove_queries_full_after_ms = mutationControl.median,
                        unpatched_100_queries_ms = unpatchedBatch.median, patched_fallback_100_queries_ms = nativeBatch.median, indexed_100_queries_ms = indexedBatch.median, speedup_cached = unpatched.median / indexed.median });
                }
                return new { checks, benchmarks, note = "Complete installed AddNearbyTargets with live distance/validity checks and original target order. Actual tag set reference/version/count audited each query, including JIT-inlined mutations and direct HashSet Add/Remove/Clear. Synthetic item/prefab objects; list changes, movement, empty/all-target and conflict fallbacks tested." };
            }
            finally
            {
                EndMethod.Invoke(null, null); foreign.UnpatchSelf(); harmony.UnpatchSelf();
                while (Item.ItemList.Count > originalCount) Item.ItemList.RemoveAt(Item.ItemList.Count - 1);
                AccessTools.Method(typeof(NearbyItemIndex), "Reset").Invoke(null, null);
                Config.UseNearbyItemIndex = enabled; Config.MinimumItemsForNearbyIndex = minimum;
                Config.BackgroundNearbyIndexBuild = background;
                Config.IncrementalNearbyListMaintenance = incremental;
                Config.MinimumItemsForParallelTagAudit = auditMinimum;
            }
        }
        private static void EmptyPrefix() { }
        private static (double median, double p95) Time(Action action, int loops = 16)
        {
            var samples = new double[15];
            for (int sample = 0; sample < samples.Length; sample++)
            { long start = Stopwatch.GetTimestamp(); for (int i = 0; i < loops; i++) action(); samples[sample] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency / loops; }
            Array.Sort(samples); return (samples[7], samples[14]);
        }
    }
}
