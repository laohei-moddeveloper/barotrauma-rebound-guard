using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ReboundGuard
{
    // Identifier/tag broad phase only. Positions, range, hull conditions, removal
    // and property objects are still evaluated by the installed native method.
    public static class NearbyItemIndex
    {
        private sealed class Node
        {
            internal int Order;
            internal Item Item;
            internal Identifier[] Keys;
            internal HashSet<Identifier> Tags;
            internal ItemPrefab Prefab;
            internal ImmutableHashSet<Identifier> PrefabTags;
            internal int TagsVersion, TagsCount;
            internal long Seen;
            internal bool ForceRefresh;
        }
        private struct Snapshot
        {
            internal Item Item; internal ItemPrefab Prefab; internal Identifier Identifier;
            internal ImmutableHashSet<Identifier> PrefabTags; internal HashSet<Identifier> Tags;
            internal int Version, Count;
        }
        private sealed class Bucket
        {
            internal readonly List<Item> Items = new List<Item>();
            internal long Revision;
        }
        private sealed class InitialState
        {
            internal readonly Dictionary<Item, Node> Nodes = new Dictionary<Item, Node>();
            internal readonly Dictionary<Identifier, Bucket> Buckets = new Dictionary<Identifier, Bucket>();
            internal readonly List<Node> Ordered = new List<Node>();
            internal bool Valid = true; internal int Version;
        }
        private sealed class Candidate
        {
            internal long Generation = -1, DenseGeneration = -1;
            internal long ObservedGeneration = -1;
            internal int DenseListVersion = -1;
            internal BucketStamp Dependencies;
            internal readonly List<Item> Items = new List<Item>();
        }
        private static Dictionary<Item, Node> Nodes = new Dictionary<Item, Node>();
        private static Dictionary<Identifier, Bucket> Buckets = new Dictionary<Identifier, Bucket>();
        private static readonly HashSet<Identifier> ScratchKeys = new HashSet<Identifier>();
        private static readonly HashSet<Item> ScratchItems = new HashSet<Item>();
        private static List<Node> OrderedNodes = new List<Node>();
        private static readonly List<Node> NextNodes = new List<Node>();
        private static long rebuildGeneration;
        private static Task<InitialState> initialBuild;
        private static long backgroundBuilds;
        private static List<Node>[] ChangedByWorker = Array.Empty<List<Node>>();
        private static readonly ParallelOptions AuditOptions = new ParallelOptions { TaskScheduler = TaskScheduler.Default };
        private static int auditWorkers;
        private static long parallelAudits;
        private static readonly Action<int> AuditChunk = chunk =>
        {
            List<Node> changed = ChangedByWorker[chunk]; changed.Clear();
            int from = (int)((long)OrderedNodes.Count * chunk / auditWorkers), to = (int)((long)OrderedNodes.Count * (chunk + 1) / auditWorkers);
            for (int i = from; i < to; i++) if (HasChanged(OrderedNodes[i])) changed.Add(OrderedNodes[i]);
        };
        private static readonly ConditionalWeakTable<ImmutableHashSet<Identifier>, Candidate> Candidates = new ConditionalWeakTable<ImmutableHashSet<Identifier>, Candidate>();
        private static readonly AccessTools.FieldRef<List<Item>, int> Version = AccessTools.FieldRefAccess<List<Item>, int>("_version");
        private static readonly AccessTools.FieldRef<Item, HashSet<Identifier>> Tags = AccessTools.FieldRefAccess<Item, HashSet<Identifier>>("tags");
        private static readonly AccessTools.FieldRef<HashSet<Identifier>, int> TagsVersion = AccessTools.FieldRefAccess<HashSet<Identifier>, int>("_version");
        private static readonly MethodInfo Original = AccessTools.Method(typeof(StatusEffect), "AddNearbyTargets");
        private static MethodBase[] protectedMethods;
        private static int listVersion = -1;
        private static long generation, structureGeneration, queries, scannedCandidates, avoidedCandidates, rebuilds, tagUpdates, denseFallbacks, spatialFallbacks;
        private static long candidateRebuilds, candidateReuses;
        private static bool installed, valid;
        [ThreadStatic] private static bool safeInSimulation;
        [ThreadStatic] private static bool safetyChecked, safetyAllowed;
        private static long safetyChecks, calls, mixedFallbacks, poweredFallbacks, identifierFallbacks, smallFallbacks;
        private static int maximumItems;
        private static string reason = "not installed";

        internal static bool SupportedCodeMatches()
        {
            string hash = Convert.ToHexString(SHA256.HashData(Original.GetMethodBody().GetILAsByteArray()));
            bool client = typeof(Item).Assembly.GetName().Name == "Barotrauma";
            if (hash != (client ? "C7F651072A0B00DB2D42B638842A09AEEC9808C6E0D5308A38AC19AEE3D1BD49" : "3069C7E8AB4EE2CC5C13E7C2AD88A4FFB06D224D7E0A001900977EF0746DE18E")) return false;
            // Tag mutation paths were audited in this exact installed build.
            using (var stream = File.OpenRead(typeof(Item).Assembly.Location))
            {
                string assembly = Convert.ToHexString(SHA256.HashData(stream));
                // All Cecil-resolved method bodies in the installed LuaCs
                // assembly match the audited build; the binary hash changed.
                return assembly == (client ? "70D4F424A0DB55B6A4CE4781BA840EF443D15AC7060CAF16C2543CDAEF5C6E40" : "C29090D937CB9E54E38FD3ACAD6DA3B1BC3E74ADCE4EB091A6D4F2E54DFF22BD") ||
                    assembly == (client ? "CC1CF44F23F82062AFC802E7CB1BFC713EFBC3DB5705F7EF4D4F69DE6B4ED19A" : "E692D2E26A3013B2CE0FAFCF28284A493E9334451E5320194C54370B72E3388E");
            }
        }
        internal static void Install(Harmony harmony)
        {
            if (!Settings.UseNearbyItemIndex) { reason = "disabled by setting"; return; }
            if (!SupportedCodeMatches()) { reason = "not installed: unsupported binary"; return; }
            var protect = new List<MethodBase> { Original, AccessTools.Method(typeof(Item), "GetTags"),
                AccessTools.PropertyGetter(typeof(Item), "Prefab"), AccessTools.PropertyGetter(typeof(ItemPrefab), "Tags"),
                AccessTools.Method(typeof(StatusEffect), "HasTargetType") };
            protect.Add(AccessTools.PropertyGetter(typeof(HashSet<Identifier>), "Count"));
            protect.Add(AccessTools.Method(typeof(HashSet<Identifier>), "Contains", new[] { typeof(Identifier) }));
            protect.Add(AccessTools.Method(typeof(ImmutableHashSet<Identifier>), "Contains", new[] { typeof(Identifier) }));
            protect.Add(AccessTools.Method(typeof(HashSet<Identifier>), "GetEnumerator"));
            protect.Add(AccessTools.Method(typeof(HashSet<Identifier>.Enumerator), "MoveNext"));
            foreach (var pair in new[] { Tuple.Create(typeof(StatusEffect), "Range"), Tuple.Create(typeof(StatusEffect), "OnlyInside"),
                Tuple.Create(typeof(StatusEffect), "OnlyOutside"), Tuple.Create(typeof(Item), "Position"), Tuple.Create(typeof(Item), "AllPropertyObjects"),
                Tuple.Create(typeof(Entity), "WorldPosition"), Tuple.Create(typeof(Entity), "Removed"), Tuple.Create(typeof(PhysicsBody), "Position"),
                Tuple.Create(typeof(MapEntity), "Position"), Tuple.Create(typeof(Submarine), "Position"), Tuple.Create(typeof(Item), "CurrentHull") })
            {
                MethodInfo getter = AccessTools.PropertyGetter(pair.Item1, pair.Item2);
                if (getter != null) protect.Add(getter);
            }
            protect.AddRange(typeof(Item).GetMethods().Where(m => m.Name == "HasTag"));
            protect.AddRange(typeof(StatusEffect).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name == "IsValidTarget"));
            protectedMethods = protect.ToArray();
            harmony.Patch(Original, prefix: new HarmonyMethod(typeof(NearbyItemIndex), nameof(BeforeQuery)));
            harmony.Patch(AccessTools.Method(typeof(GameScreen), "Update", new[] { typeof(double) }),
                prefix: new HarmonyMethod(typeof(NearbyItemIndex), nameof(BeginSimulation)),
                finalizer: new HarmonyMethod(typeof(NearbyItemIndex), nameof(EndSimulation)));
            // Release old round entities even when no further nearby query runs
            // in the lobby. A void finalizer preserves any original exception.
            harmony.Patch(AccessTools.Method(typeof(GameSession), "EndRound"),
                finalizer: new HarmonyMethod(typeof(NearbyItemIndex), nameof(ResetCache)));
            installed = true;
            reason = "installed; awaiting applicable query";
            Plugin.Log("nearby item identifier/tag index installed; native distances and target order retained");
        }
        internal static void BeginSimulation()
        {
            safeInSimulation = installed && Settings.UseNearbyItemIndex;
            safetyChecked = safetyAllowed = false;
        }
        private static bool EnsureSafety()
        {
            if (!safeInSimulation) return false;
            if (safetyChecked) return safetyAllowed;
            safetyChecked = true; safetyChecks++;
            foreach (MethodBase method in protectedMethods)
            {
                if (method == null) { reason = "missing protected method"; return false; }
                if (PatchSafety.HasForeignPatch(method)) { reason = "original fallback: other patch on " + method.DeclaringType.Name + "." + method.Name; return false; }
            }
            safetyAllowed = true;
            if (!valid) reason = "installed; awaiting index build";
            return true;
        }
        internal static void EndSimulation() { safeInSimulation = false; }

        private static bool CollectKeys(Item item)
        {
            ScratchKeys.Clear();
            if (item == null || item.GetType() != typeof(Item) || item.Prefab == null || item.Prefab.GetType() != typeof(ItemPrefab) || item.Prefab.Tags == null || !ReferenceEquals(item.Prefab.Tags.KeyComparer, EqualityComparer<Identifier>.Default) || Tags(item) == null || !ReferenceEquals(Tags(item).Comparer, EqualityComparer<Identifier>.Default)) return false;
            ScratchKeys.Add(item.Prefab.Identifier);
            foreach (Identifier key in item.Prefab.Tags) ScratchKeys.Add(key);
            foreach (Identifier key in item.GetTags()) ScratchKeys.Add(key);
            return true;
        }
        private static bool TryInitialBuild(List<Item> items)
        {
            if (initialBuild == null)
            {
                int version = Version(items);
                var snapshot = new Snapshot[items.Count];
                for (int i = 0; i < items.Count; i++)
                {
                    Item item = items[i];
                    if (item == null || item.GetType() != typeof(Item) || item.Prefab == null || item.Prefab.GetType() != typeof(ItemPrefab) || item.Prefab.Tags == null || Tags(item) == null ||
                        !ReferenceEquals(item.Prefab.Tags.KeyComparer, EqualityComparer<Identifier>.Default) || !ReferenceEquals(Tags(item).Comparer, EqualityComparer<Identifier>.Default))
                    { listVersion = version; valid = false; reason = "original fallback: unsupported initial snapshot"; return false; }
                    HashSet<Identifier> tags = Tags(item);
                    snapshot[i] = new Snapshot { Item = item, Prefab = item.Prefab, Identifier = item.Prefab.Identifier, PrefabTags = item.Prefab.Tags,
                        Tags = tags, Version = TagsVersion(tags), Count = tags.Count };
                }
                initialBuild = Task.Run(() => BuildSnapshot(snapshot, version));
                backgroundBuilds++; reason = "building in background; native queries active";
                return false;
            }
            if (!initialBuild.IsCompleted) return false;
            InitialState built;
            try { built = initialBuild.GetAwaiter().GetResult(); }
            catch (Exception exception) { initialBuild = null; listVersion = Version(items); valid = false; reason = "original fallback: background build " + exception.GetType().Name; return false; }
            initialBuild = null;
            if (built.Version != Version(items)) { reason = "discarded stale build; native queries active"; return false; }
            foreach (var entry in Candidates) { entry.Value.Items.Clear(); entry.Value.Generation = -1; }
            Nodes = built.Nodes; Buckets = built.Buckets; OrderedNodes = built.Ordered;
            valid = built.Valid; listVersion = built.Version; generation++; structureGeneration++; rebuilds++;
            reason = valid ? "eligible" : "original fallback: duplicate initial item";
            return valid;
        }
        private static InitialState BuildSnapshot(Snapshot[] snapshot, int version)
        {
            var state = new InitialState { Version = version };
            var keys = new HashSet<Identifier>();
            for (int i = 0; i < snapshot.Length; i++)
            {
                Snapshot source = snapshot[i];
                var node = new Node { Item = source.Item, Prefab = source.Prefab, PrefabTags = source.PrefabTags, Tags = source.Tags,
                    TagsVersion = source.Version, TagsCount = source.Count, Order = i };
                if (!state.Nodes.TryAdd(source.Item, node)) { state.Valid = false; break; }
                keys.Clear();
                try
                {
                    keys.Add(source.Identifier);
                    foreach (Identifier key in source.PrefabTags) keys.Add(key);
                    foreach (Identifier key in source.Tags) keys.Add(key);
                    node.ForceRefresh = TagsVersion(source.Tags) != source.Version || source.Tags.Count != source.Count;
                }
                catch (InvalidOperationException) { node.ForceRefresh = true; }
                catch (IndexOutOfRangeException) { node.ForceRefresh = true; }
                if (node.ForceRefresh) keys.Clear();
                node.Keys = keys.ToArray(); state.Ordered.Add(node);
                foreach (Identifier key in node.Keys)
                {
                    if (!state.Buckets.TryGetValue(key, out var bucket)) state.Buckets[key] = bucket = new Bucket();
                    bucket.Items.Add(source.Item);
                }
            }
            if (!state.Valid) { state.Nodes.Clear(); state.Buckets.Clear(); state.Ordered.Clear(); }
            return state;
        }
        private static void Rebuild(List<Item> items)
        {
            valid = true; NextNodes.Clear();
            long seen = ++rebuildGeneration;
            bool sort = false, sawNew = false;
            int lastOrder = -1;
            // Prefab target sets can live for the whole application. Their cached
            // lists must not retain removed entities after a list rebuild.
            foreach (var entry in Candidates) { entry.Value.Items.Clear(); entry.Value.Generation = -1; }
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item == null) { valid = false; break; }
                if (Nodes.TryGetValue(item, out Node node))
                {
                    if (node.Seen == seen) { valid = false; break; }
                    if (node.Order < lastOrder || sawNew) sort = true;
                    lastOrder = node.Order;
                }
                else
                {
                    if (!CollectKeys(item)) { valid = false; break; }
                    node = new Node { Item = item, Prefab = item.Prefab, PrefabTags = item.Prefab.Tags, Keys = ScratchKeys.ToArray(), Tags = Tags(item), TagsVersion = TagsVersion(Tags(item)), TagsCount = Tags(item).Count };
                    Nodes.Add(item, node); sawNew = true;
                    foreach (Identifier key in node.Keys)
                    {
                        if (!Buckets.TryGetValue(key, out var bucket)) Buckets[key] = bucket = new Bucket();
                        bucket.Items.Add(item);
                    }
                }
                node.Order = i; node.Seen = seen; NextNodes.Add(node);
            }
            if (valid)
            {
                foreach (Node node in OrderedNodes)
                {
                    if (node.Seen == seen) continue;
                    foreach (Identifier key in node.Keys)
                    { var bucket = Buckets[key]; bucket.Items.Remove(node.Item); if (bucket.Items.Count == 0) Buckets.Remove(key); }
                    Nodes.Remove(node.Item);
                }
                if (sort) foreach (var bucket in Buckets.Values) bucket.Items.Sort((left, right) => Nodes[left].Order.CompareTo(Nodes[right].Order));
                OrderedNodes.Clear(); OrderedNodes.AddRange(NextNodes);
            }
            else { Nodes.Clear(); OrderedNodes.Clear(); Buckets.Clear(); }
            NextNodes.Clear();
            listVersion = Version(items); generation++; structureGeneration++; rebuilds++;
            reason = valid ? "eligible" : "original fallback: unsupported item/prefab or duplicate entry";
        }
        private static bool HasChanged(Node node)
        {
            HashSet<Identifier> current = Tags(node.Item);
            return node.ForceRefresh || !ReferenceEquals(node.Tags, current) || current == null || node.TagsVersion != TagsVersion(current) || node.TagsCount != current.Count || !ReferenceEquals(node.PrefabTags, node.Prefab.Tags);
        }
        private static void RefreshTags()
        {
            // API hooks can be bypassed by JIT inlining. Audit actual set state
            // for every query. Count also covers HashSet.Remove/Clear, which do
            // not always bump _version in modern .NET.
            if (Settings.ParallelNearbyTagAudit && OrderedNodes.Count >= Settings.MinimumItemsForParallelTagAudit && Environment.ProcessorCount >= 4 && Settings.MaximumWorkers >= 2)
            {
                auditWorkers = Math.Min(Settings.MaximumWorkers, Environment.ProcessorCount / 2);
                if (ChangedByWorker.Length != auditWorkers)
                {
                    ChangedByWorker = Enumerable.Range(0, auditWorkers).Select(_ => new List<Node>()).ToArray();
                    AuditOptions.MaxDegreeOfParallelism = auditWorkers;
                }
                Parallel.For(0, auditWorkers, AuditOptions, AuditChunk);
                parallelAudits++;
                foreach (var changed in ChangedByWorker)
                {
                    foreach (Node node in changed)
                    {
                        UpdateTags(node);
                        if (!valid) { foreach (var retained in ChangedByWorker) retained.Clear(); return; }
                    }
                    changed.Clear();
                }
            }
            else foreach (Node node in OrderedNodes) if (HasChanged(node)) { UpdateTags(node); if (!valid) return; }
        }
        private static void UpdateTags(Node node)
        {
                Item item = node.Item;
                HashSet<Identifier> current = Tags(item);
                if (!CollectKeys(item)) { valid = false; reason = "original fallback: unsupported changed item"; return; }
                node.Tags = current; node.TagsVersion = TagsVersion(current); node.TagsCount = current.Count;
                node.PrefabTags = node.Prefab.Tags;
                node.ForceRefresh = false;
                if (ScratchKeys.SetEquals(node.Keys)) return;
                long revision = ++generation;
                foreach (Identifier key in node.Keys)
                {
                    if (ScratchKeys.Contains(key)) continue;
                    var bucket = Buckets[key]; bucket.Items.Remove(item); bucket.Revision = revision;
                    if (bucket.Items.Count == 0) Buckets.Remove(key);
                }
                foreach (Identifier key in ScratchKeys)
                {
                    if (Array.IndexOf(node.Keys, key) >= 0) continue;
                    if (!Buckets.TryGetValue(key, out var bucket)) Buckets[key] = bucket = new Bucket();
                    int low = 0, high = bucket.Items.Count;
                    while (low < high) { int middle = low + (high - low) / 2; if (Nodes[bucket.Items[middle]].Order < node.Order) low = middle + 1; else high = middle; }
                    bucket.Items.Insert(low, item); bucket.Revision = revision;
                }
                node.Keys = ScratchKeys.ToArray(); tagUpdates++;
        }
        private static bool DenseEnoughToIndex(List<Item> items, Vector2 position, float range)
        {
            // A conservative performance choice only: returning false uses the
            // complete original scan, never an approximate target list.
            int count = Math.Min(16, items.Count), near = 0;
            float squared = range * range;
            for (int i = 0; i < count; i++)
            {
                Item item = items[(int)((long)i * items.Count / count)];
                if (item == null || item.GetType() != typeof(Item)) return false;
                if (item.Removed) continue;
                Vector2 delta = item.WorldPosition - position;
                if (Math.Abs(delta.X) <= range && Math.Abs(delta.Y) <= range && delta.X * delta.X + delta.Y * delta.Y < squared) near++;
            }
            return near * 4 >= count;
        }
        private static List<Item> GetCandidates(StatusEffect effect, Vector2 worldPosition)
        {
            List<Item> items = Item.ItemList;
            var identifiers = effect.TargetIdentifiers;
            if (!safeInSimulation || !Settings.UseNearbyItemIndex) return items;
            maximumItems = Math.Max(maximumItems, items.Count);
            if (identifiers == null || !ReferenceEquals(identifiers.KeyComparer, EqualityComparer<Identifier>.Default) || identifiers.Contains("item"))
            { identifierFallbacks++; return items; }
            if (items.Count < Settings.MinimumItemsForNearbyIndex) { smallFallbacks++; return items; }
            if (!EnsureSafety()) return items;
            queries++;
            if (!DenseEnoughToIndex(items, worldPosition, effect.Range)) { spatialFallbacks++; return items; }
            Candidate candidate = Candidates.GetValue(identifiers, _ => new Candidate());
            if (candidate.DenseGeneration == generation && candidate.DenseListVersion == Version(items)) { denseFallbacks++; return items; }
            if (listVersion != Version(items))
            {
                if (Nodes.Count == 0 && Settings.BackgroundNearbyIndexBuild)
                { if (!TryInitialBuild(items)) return items; }
                else Rebuild(items);
            }
            if (valid) RefreshTags();
            if (!valid) return items;
            var dependencies = candidate.Dependencies;
            if (candidate.ObservedGeneration != generation)
            {
                dependencies = new BucketStamp();
                foreach (Identifier identifier in identifiers)
                    if (Buckets.TryGetValue(identifier, out var dependency)) dependencies.Include(dependency.Revision);
            }
            if (candidate.Generation != structureGeneration || !candidate.Dependencies.Equals(dependencies))
            {
                candidate.Generation = -1;
                candidate.Items.Clear(); ScratchItems.Clear();
                foreach (Identifier identifier in identifiers)
                    if (Buckets.TryGetValue(identifier, out var bucket))
                        foreach (Item item in bucket.Items) if (ScratchItems.Add(item)) candidate.Items.Add(item);
                candidate.Items.Sort((left, right) => Nodes[left].Order.CompareTo(Nodes[right].Order));
                ScratchItems.Clear(); candidate.Generation = structureGeneration; candidate.Dependencies = dependencies; candidateRebuilds++;
            }
            else candidateReuses++;
            candidate.ObservedGeneration = generation;
            if (candidate.Items.Count * 4L >= items.Count * 3L)
            {
                candidate.DenseGeneration = generation; candidate.DenseListVersion = Version(items);
                candidate.Generation = -1; // Items below is discarded, not a cached empty result.
                candidate.Items.Clear(); candidate.Items.TrimExcess(); denseFallbacks++;
                return items;
            }
            scannedCandidates += candidate.Items.Count;
            avoidedCandidates += items.Count - candidate.Items.Count;
            return candidate.Items;
        }
        private static readonly Identifier PoweredTag = "powered".ToIdentifier(), JunctionTag = "junctionbox".ToIdentifier(), RelayTag = "relaycomponent".ToIdentifier();
        public static bool BeforeQuery(StatusEffect __instance, Vector2 worldPosition, List<ISerializableEntity> targets)
        {
            // Keep the original method body intact for all fallback paths.
            // Mixed character/item and powered-list queries also stay native.
            if (!safeInSimulation || !Settings.UseNearbyItemIndex) return true;
            calls++;
            if (!__instance.HasTargetType(StatusEffect.TargetType.NearbyItems) || __instance.Range <= 0f) return true;
            if (__instance.HasTargetType(StatusEffect.TargetType.NearbyCharacters)) { mixedFallbacks++; return true; }
            var ids = __instance.TargetIdentifiers;
            if (ids != null && ids.Count == 1 && (ids.Contains(PoweredTag) || ids.Contains(JunctionTag) || ids.Contains(RelayTag))) { poweredFallbacks++; return true; }
            List<Item> candidates = GetCandidates(__instance, worldPosition);
            if (ReferenceEquals(candidates, Item.ItemList)) return true;
            foreach (Item item in candidates)
            {
                if (!item.Removed && WithinRange(__instance, item, worldPosition) && __instance.IsValidTarget(item))
                    targets.AddRange(item.AllPropertyObjects);
            }
            return false;
        }
        private static bool WithinRange(StatusEffect effect, Item item, Vector2 position)
        {
            float x = Math.Abs(item.WorldPosition.X - position.X);
            if (x > effect.Range) return false;
            float y = Math.Abs(item.WorldPosition.Y - position.Y);
            if (y > effect.Range) return false;
            return x * x + y * y < effect.Range * effect.Range;
        }
        internal static string Report()
        {
            string text = "nearby item index: " + reason + "; installed=" + installed + "; enabled=" + Settings.UseNearbyItemIndex + "; queries=" + queries + "; candidates=" + scannedCandidates +
                "; avoided_item_visits=" + avoidedCandidates + "; rebuilds=" + rebuilds + "; tag_updates=" + tagUpdates;
            text += "; parallel_tag_audits=" + parallelAudits + "; background_builds=" + backgroundBuilds + "; dense_fallbacks=" + denseFallbacks + "; spatial_fallbacks=" + spatialFallbacks;
            text += "; candidate_rebuilds=" + candidateRebuilds + "; candidate_reuses=" + candidateReuses;
            text += "; observed_calls=" + calls + "; mixed_fallbacks=" + mixedFallbacks + "; powered_fallbacks=" + poweredFallbacks + "; identifier_fallbacks=" + identifierFallbacks + "; small_fallbacks=" + smallFallbacks + "; max_items=" + maximumItems + "; safety_checks=" + safetyChecks;
            calls = mixedFallbacks = poweredFallbacks = identifierFallbacks = smallFallbacks = safetyChecks = 0; maximumItems = 0;
            queries = scannedCandidates = avoidedCandidates = rebuilds = tagUpdates = parallelAudits = backgroundBuilds = denseFallbacks = spatialFallbacks = candidateRebuilds = candidateReuses = 0; return text;
        }
        internal static void Reset()
        {
            installed = false; ResetCache(); reason = "not installed";
        }
        internal static void ResetCache()
        {
            valid = safeInSimulation = false; listVersion = -1; initialBuild = null;
            safetyChecked = safetyAllowed = false;
            reason = installed ? "installed; awaiting applicable query" : "not installed";
            calls = mixedFallbacks = poweredFallbacks = identifierFallbacks = smallFallbacks = safetyChecks = 0; maximumItems = 0;
            Nodes.Clear(); OrderedNodes.Clear(); NextNodes.Clear(); Buckets.Clear(); ScratchKeys.Clear(); ScratchItems.Clear(); Candidates.Clear();
            foreach (var changed in ChangedByWorker) changed.Clear();
            generation++; structureGeneration++; queries = scannedCandidates = avoidedCandidates = rebuilds = tagUpdates = parallelAudits = backgroundBuilds = denseFallbacks = spatialFallbacks = candidateRebuilds = candidateReuses = 0;
        }
    }
}
