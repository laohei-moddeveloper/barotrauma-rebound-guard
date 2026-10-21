using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using HarmonyLib;
using ReboundGuard;
using Config = ReboundGuard.Settings;

namespace ReboundGuardTests
{
    public static class LiveStatusTagTests
    {
        private static int checks;
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        private static void Call(string name) => AccessTools.Method(typeof(LiveStatusTagIndex), name).Invoke(null, null);
        private static readonly FieldInfo TagField = AccessTools.Field(typeof(StatusEffect), "statusEffectTags");
        private static PropertyConditional Conditional(bool notEquals, params string[] tags)
        {
            var conditional = (PropertyConditional)RuntimeHelpers.GetUninitializedObject(typeof(PropertyConditional));
            AccessTools.Field(typeof(PropertyConditional), "Type").SetValue(conditional, PropertyConditional.ConditionType.HasStatusTag);
            AccessTools.Field(typeof(PropertyConditional), "ComparisonOperator").SetValue(conditional,
                notEquals ? PropertyConditional.ComparisonOperatorType.NotEquals : PropertyConditional.ComparisonOperatorType.Equals);
            AccessTools.Field(typeof(PropertyConditional), "AttributeValueAsTags").SetValue(conditional, tags.Select(tag => tag.ToIdentifier()).ToImmutableArray());
            return conditional;
        }
        private static StatusEffect Effect(bool delayed, params string[] tags)
        {
            var effect = (StatusEffect)RuntimeHelpers.GetUninitializedObject(delayed ? typeof(DelayedEffect) : typeof(StatusEffect));
            TagField.SetValue(effect, new HashSet<Identifier>(tags.Select(tag => tag.ToIdentifier())));
            return effect;
        }
        private static Item Target() => (Item)RuntimeHelpers.GetUninitializedObject(typeof(Item));
        private static void Compare(PropertyConditional conditional, ISerializableEntity target)
        {
            Config.UseLiveStatusTagIndex = false;
            bool expected = conditional.Matches(target);
            Config.UseLiveStatusTagIndex = true;
            bool actual = conditional.Matches(target);
            Check(expected == actual, "actual native Matches and indexed result agree after live mutation");
        }
        private static void ForeignHasTag(ref bool __result) { __result = !__result; }
        private static void FalsePrefix(ref bool __result) { __result = false; }
        public static object Run()
        {
            checks = 0;
            var originalDurations = StatusEffect.DurationList.ToArray();
            var originalDelays = DelayedEffect.DelayList.ToArray();
            bool enabled = Config.UseLiveStatusTagIndex;
            int minimum = Config.MinimumEffectsForStatusTagIndex;
            var harmony = new Harmony("barotrauma.reboundguard.experimental.v040");
            var foreign = new Harmony("reboundguard.status-tags.foreign");
            try
            {
                StatusEffect.DurationList.Clear(); DelayedEffect.DelayList.Clear();
                Config.UseLiveStatusTagIndex = true; Config.MinimumEffectsForStatusTagIndex = 0;
                Check((bool)AccessTools.Method(typeof(LiveStatusTagIndex), "SupportedCodeMatches").Invoke(null, null), "audited actual status-effect assembly");
                AccessTools.Method(typeof(LiveStatusTagIndex), "Install").Invoke(null, new object[] { harmony });
                Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(PropertyConditional), "MatchesDirect"))?.Owners.Contains(harmony.Id) == true,
                    "index transpiler installed on inner method, preserving contained-target routing");
                Call("BeginSimulation");
                Item[] targets = Enumerable.Range(0, 8).Select(_ => Target()).ToArray();
                var conditions = new[] { Conditional(false, "a"), Conditional(true, "a"), Conditional(false, "a", "b"),
                    Conditional(true, "a", "b"), Conditional(false), Conditional(true), Conditional(false, ""), Conditional(false, "a", "a"),
                    Conditional(false, "A"), Conditional(false, "absent") };
                foreach (var c in conditions) { Compare(c, targets[0]); Compare(c, null); }
                var first = new DurationListElement(Effect(false, "a"), null, new[] { targets[0], targets[0] }, 4, null);
                StatusEffect.DurationList.Add(first);
                var delayed = new DelayedListElement((DelayedEffect)Effect(true, "b"), null, new[] { targets[0], targets[1] }, 8, null, null);
                DelayedEffect.DelayList.Add(delayed);
                foreach (var c in conditions) foreach (var t in targets) Compare(c, t);
                // Each mutation is immediately queried in the SAME simulation step.
                first.Targets[0] = targets[2]; Compare(conditions[0], targets[0]);
                first.Targets.RemoveAt(1); Compare(conditions[0], targets[0]);
                first.Targets.Add(targets[0]); Compare(conditions[0], targets[0]);
                var firstTags = (HashSet<Identifier>)TagField.GetValue(first.Parent);
                firstTags.Clear(); Compare(conditions[0], targets[0]); // Clear need not change HashSet._version.
                firstTags.Add("a".ToIdentifier()); Compare(conditions[0], targets[0]);
                firstTags.Remove("a".ToIdentifier()); firstTags.Add("b".ToIdentifier()); Compare(conditions[0], targets[0]);
                StatusEffect.DurationList[0] = new DurationListElement(Effect(false, "a"), null, new[] { targets[4] }, 4, null);
                Compare(conditions[0], targets[0]); Compare(conditions[0], targets[4]);
                delayed.Targets.Clear(); Compare(conditions[2], targets[0]);
                DelayedEffect.DelayList.Clear(); Compare(conditions[6], targets[1]);

                var random = new Random(672193);
                for (int i = 0; i < 4000; i++)
                {
                    switch (random.Next(8))
                    {
                        case 0: if (StatusEffect.DurationList.Count < 40) StatusEffect.DurationList.Add(new DurationListElement(Effect(false, random.Next(2) == 0 ? "a" : "b"), null, new[] { targets[random.Next(targets.Length)] }, 3, null)); break;
                        case 1: if (StatusEffect.DurationList.Count > 0) StatusEffect.DurationList.RemoveAt(random.Next(StatusEffect.DurationList.Count)); break;
                        case 2: if (StatusEffect.DurationList.Count > 0) { var e = StatusEffect.DurationList[random.Next(StatusEffect.DurationList.Count)]; e.Targets.Clear(); e.Targets.Add(targets[random.Next(targets.Length)]); } break;
                        case 3: if (StatusEffect.DurationList.Count > 0) { var e = StatusEffect.DurationList[random.Next(StatusEffect.DurationList.Count)]; var set = (HashSet<Identifier>)TagField.GetValue(e.Parent); if (!set.Remove("a".ToIdentifier())) set.Add("a".ToIdentifier()); } break;
                        case 4: DelayedEffect.DelayList.Clear(); break;
                        case 5: if (DelayedEffect.DelayList.Count < 20) DelayedEffect.DelayList.Add(new DelayedListElement((DelayedEffect)Effect(true, "a", "b"), null, new[] { targets[random.Next(targets.Length)] }, 7, null, null)); break;
                        case 6: if (StatusEffect.DurationList.Count > 1) StatusEffect.DurationList.Reverse(); break;
                        case 7: if (StatusEffect.DurationList.Count > 0) StatusEffect.DurationList[0] = new DurationListElement(Effect(false, "b"), null, new[] { targets[random.Next(targets.Length)] }, 2, null); break;
                    }
                    foreach (var c in conditions) Compare(c, targets[random.Next(targets.Length)]);
                }
                // A late foreign hook must be observed at the next step, including
                // hooks whose behavior differs from the private tag-set contents.
                foreign.Patch(AccessTools.Method(typeof(StatusEffect), "HasTag"), postfix: new HarmonyMethod(typeof(LiveStatusTagTests), nameof(ForeignHasTag)));
                Call("EndSimulation"); Call("BeginSimulation");
                foreach (var c in conditions) Compare(c, targets[0]);
                foreign.UnpatchSelf(); Call("EndSimulation"); Call("BeginSimulation");

                object[] benchmarks = Benchmark(targets);
                Call("ResetCache"); Call("BeginSimulation");
                foreach (var c in conditions) Compare(c, targets[0]);
                Call("EndSimulation");
                foreach (var c in conditions) Compare(c, targets[0]);
                return new { checks, benchmarks, limits = "Actual native conditional methods and synthetic effects; no loaded submarine or multiplayer session." };
            }
            finally
            {
                foreign.UnpatchSelf(); harmony.UnpatchSelf(); Call("Reset");
                StatusEffect.DurationList.Clear(); StatusEffect.DurationList.AddRange(originalDurations);
                DelayedEffect.DelayList.Clear(); DelayedEffect.DelayList.AddRange(originalDelays);
                Config.UseLiveStatusTagIndex = enabled; Config.MinimumEffectsForStatusTagIndex = minimum;
            }
        }
        private static object[] Benchmark(Item[] targets)
        {
            var results = new List<object>();
            foreach (int effects in new[] { 32, 128, 512 })
            {
                StatusEffect.DurationList.Clear(); DelayedEffect.DelayList.Clear();
                // Missing tags force the native duration/delay scans. Other
                // scenarios below cover early success and frequently changing lists.
                for (int i = 0; i < effects; i++)
                    StatusEffect.DurationList.Add(new DurationListElement(Effect(false, "a", "b"), null, targets, 20, null));
                foreach (var query in new[] { Conditional(false, "absent"), Conditional(false, "a"), Conditional(false, "a", "b", "absent") })
                {
                    Action run = () => { for (int i = 0; i < 2048; i++) query.Matches(targets[i % targets.Length]); };
                    Config.UseLiveStatusTagIndex = false; run();
                    var before = Measure(run);
                    Config.UseLiveStatusTagIndex = true; run();
                    var indexed = Measure(run);
                    Config.UseLiveStatusTagIndex = false;
                    var after = Measure(run);
                    Config.UseLiveStatusTagIndex = true;
                    results.Add(new { effects, tags = query.AttributeValueAsTags.Select(t => t.Value).ToArray(),
                        native_before_ms = before.ms, indexed_ms = indexed.ms, native_after_ms = after.ms,
                        native_bytes = before.bytes, indexed_bytes = indexed.bytes });
                }
            }
            return results.ToArray();
        }
        private static (double ms, long bytes) Measure(Action run)
        {
            var samples = new List<double>(); long bytes = 0;
            for (int i = 0; i < 7; i++)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp(); run();
                samples.Add((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
                bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            }
            samples.Sort(); return (samples[samples.Count / 2], bytes);
        }
    }
}
