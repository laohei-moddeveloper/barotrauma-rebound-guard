using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Barotrauma;
using HarmonyLib;

namespace ReboundGuard
{
    // Unlike a frame cache, every query observes actual collection versions.
    // Timers do not affect HasStatusTag: membership lasts until list removal.
    public static class LiveStatusTagIndex
    {
        private struct Source
        {
            internal StatusEffect Effect;
            internal List<ISerializableEntity> Targets;
            internal HashSet<Identifier> Tags;
            internal int TargetVersion, TargetCount, TagVersion, TagCount;
            internal bool Supported;
        }
        private sealed class TargetState
        {
            internal long Generation = -1;
            internal bool HasEffect;
            internal readonly HashSet<Identifier> Tags = new HashSet<Identifier>();
        }
        private static readonly MethodInfo Original = AccessTools.Method(typeof(PropertyConditional), "MatchesDirect");
        private static readonly MethodInfo HasTag = AccessTools.Method(typeof(StatusEffect), "HasTag", new[] { typeof(Identifier) });
        private static readonly AccessTools.FieldRef<StatusEffect, HashSet<Identifier>> Tags = AccessTools.FieldRefAccess<StatusEffect, HashSet<Identifier>>("statusEffectTags");
        private static readonly AccessTools.FieldRef<List<ISerializableEntity>, int> TargetVersion = AccessTools.FieldRefAccess<List<ISerializableEntity>, int>("_version");
        private static readonly AccessTools.FieldRef<List<DurationListElement>, int> DurationVersion = AccessTools.FieldRefAccess<List<DurationListElement>, int>("_version");
        private static readonly AccessTools.FieldRef<List<DelayedListElement>, int> DelayVersion = AccessTools.FieldRefAccess<List<DelayedListElement>, int>("_version");
        private static readonly AccessTools.FieldRef<HashSet<Identifier>, int> TagVersion = AccessTools.FieldRefAccess<HashSet<Identifier>, int>("_version");
        private static readonly ConditionalWeakTable<ISerializableEntity, TargetState> Targets = new ConditionalWeakTable<ISerializableEntity, TargetState>();
        private static readonly Dictionary<Type, bool> EqualitySupport = new Dictionary<Type, bool>();
        private static Source[] sources = Array.Empty<Source>();
        private static int count, durationVersion = -1, delayVersion = -1;
        private static long generation;
        private static bool installed;
        [ThreadStatic] private static bool inStep, safetyChecked, safe;
        private static MethodBase[] protectedMethods;
        private static long queries, audits, rebuiltTargets, reusedTargets, fallbacks;

        internal static bool SupportedCodeMatches()
        {
            if (Original == null || HasTag == null) return false;
            using (var stream = File.OpenRead(typeof(StatusEffect).Assembly.Location))
            {
                string hash = Convert.ToHexString(SHA256.HashData(stream));
                // These complete assemblies contain the audited MatchesDirect,
                // HasTag, duration/delay records and their mutation paths.
                return hash == "70D4F424A0DB55B6A4CE4781BA840EF443D15AC7060CAF16C2543CDAEF5C6E40" ||
                    hash == "CC1CF44F23F82062AFC802E7CB1BFC713EFBC3DB5705F7EF4D4F69DE6B4ED19A" ||
                    hash == "C29090D937CB9E54E38FD3ACAD6DA3B1BC3E74ADCE4EB091A6D4F2E54DFF22BD" ||
                    hash == "E692D2E26A3013B2CE0FAFCF28284A493E9334451E5320194C54370B72E3388E";
            }
        }
        internal static void Install(Harmony harmony)
        {
            if (!Settings.UseLiveStatusTagIndex || !SupportedCodeMatches()) return;
            protectedMethods = new MethodBase[] { Original, HasTag,
                AccessTools.Method(typeof(List<ISerializableEntity>), "Contains"),
                AccessTools.Method(typeof(HashSet<Identifier>), "Contains"),
                AccessTools.Method(typeof(HashSet<Identifier>), "GetEnumerator"),
                AccessTools.Method(typeof(HashSet<Identifier>.Enumerator), "MoveNext"),
                AccessTools.Method(typeof(HashSet<Identifier>), "UnionWith"),
                AccessTools.Method(typeof(Identifier), "GetHashCode", Type.EmptyTypes),
                AccessTools.Method(typeof(Identifier), "Equals", new[] { typeof(Identifier) }),
                AccessTools.Method(typeof(object), "Equals", new[] { typeof(object) }) };
            foreach (MethodBase method in protectedMethods) if (PatchSafety.HasForeignPatch(method)) return;
            harmony.Patch(Original, transpiler: new HarmonyMethod(typeof(LiveStatusTagIndex), nameof(Transpiler)));
            harmony.Patch(AccessTools.Method(typeof(GameScreen), "Update", new[] { typeof(double) }),
                prefix: new HarmonyMethod(typeof(LiveStatusTagIndex), nameof(BeginSimulation)),
                finalizer: new HarmonyMethod(typeof(LiveStatusTagIndex), nameof(EndSimulation)));
            harmony.Patch(AccessTools.Method(typeof(GameSession), "EndRound"),
                finalizer: new HarmonyMethod(typeof(LiveStatusTagIndex), nameof(ResetCache)));
            installed = true;
            Plugin.Log("live status tag index installed; every-query collection audit; contained-target routing retained");
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            System.Reflection.Emit.Label original = generator.DefineLabel();
            LocalBuilder result = generator.DeclareLocal(typeof(bool));
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(PropertyConditional), "Type"));
            yield return new CodeInstruction(OpCodes.Ldc_I4, (int)PropertyConditional.ConditionType.HasStatusTag);
            yield return new CodeInstruction(OpCodes.Bne_Un, original);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldarg_1);
            yield return new CodeInstruction(OpCodes.Ldloca, result);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LiveStatusTagIndex), nameof(TryMatch)));
            yield return new CodeInstruction(OpCodes.Brfalse, original);
            yield return new CodeInstruction(OpCodes.Ldloc, result);
            yield return new CodeInstruction(OpCodes.Ret);
            var entry = new CodeInstruction(OpCodes.Nop); entry.labels.Add(original); yield return entry;
            foreach (CodeInstruction instruction in instructions) yield return instruction;
        }
        internal static void BeginSimulation() { inStep = installed; safetyChecked = safe = false; }
        internal static void EndSimulation() { inStep = false; }
        private static bool EnsureSafety()
        {
            if (safetyChecked) return safe;
            safetyChecked = true;
            foreach (MethodBase method in protectedMethods) if (PatchSafety.HasForeignPatch(method)) return false;
            return safe = true;
        }
        private static bool ReferenceEqualityOnly(ISerializableEntity entity)
        {
            if (entity == null) return true;
            Type type = entity.GetType();
            if (!EqualitySupport.TryGetValue(type, out bool supported))
            {
                MethodInfo equals = type.GetMethod("Equals", new[] { typeof(object) });
                EqualitySupport[type] = supported = equals?.DeclaringType == typeof(object);
            }
            return supported;
        }
        private static bool Audit(StatusEffect effect, List<ISerializableEntity> targets, ref Source previous)
        {
            if (effect == null || targets == null) return false;
            HashSet<Identifier> tags = Tags(effect);
            if (tags == null || !ReferenceEquals(tags.Comparer, EqualityComparer<Identifier>.Default)) return false;
            int tv = TargetVersion(targets), sv = TagVersion(tags);
            bool changed = !ReferenceEquals(previous.Effect, effect) || !ReferenceEquals(previous.Targets, targets) ||
                !ReferenceEquals(previous.Tags, tags) || previous.TargetVersion != tv || previous.TargetCount != targets.Count ||
                previous.TagVersion != sv || previous.TagCount != tags.Count;
            if (!changed) return previous.Supported;
            bool supported = true;
            foreach (ISerializableEntity entity in targets) if (!ReferenceEqualityOnly(entity)) { supported = false; break; }
            previous = new Source { Effect = effect, Targets = targets, Tags = tags, TargetVersion = tv,
                TargetCount = targets.Count, TagVersion = sv, TagCount = tags.Count, Supported = supported };
            generation++;
            return supported;
        }
        private static bool Refresh()
        {
            int durations = StatusEffect.DurationList.Count, delayed = DelayedEffect.DelayList.Count;
            int dv = DurationVersion(StatusEffect.DurationList), lv = DelayVersion(DelayedEffect.DelayList);
            int nextCount = durations + delayed;
            if (sources.Length < nextCount) Array.Resize(ref sources, Math.Max(nextCount, Math.Max(16, sources.Length * 2)));
            bool structureChanged = dv != durationVersion || lv != delayVersion || count != nextCount;
            if (structureChanged) generation++;
            bool supported = true;
            for (int i = 0; i < durations; i++)
            {
                DurationListElement entry = StatusEffect.DurationList[i];
                if (entry == null || !Audit(entry.Parent, entry.Targets, ref sources[i])) supported = false;
            }
            for (int i = 0; i < delayed; i++)
            {
                DelayedListElement entry = DelayedEffect.DelayList[i];
                if (entry == null || !Audit(entry.Parent, entry.Targets, ref sources[durations + i])) supported = false;
            }
            if (count > nextCount) Array.Clear(sources, nextCount, count - nextCount);
            count = nextCount; durationVersion = dv; delayVersion = lv; audits++;
            return supported;
        }
        public static bool TryMatch(PropertyConditional conditional, ISerializableEntity target, out bool result)
        {
            result = false;
            if (!inStep || !Settings.UseLiveStatusTagIndex || conditional.Type != PropertyConditional.ConditionType.HasStatusTag) return false;
            if (StatusEffect.DurationList.Count + DelayedEffect.DelayList.Count < Settings.MinimumEffectsForStatusTagIndex)
            {
                // Do not retain expired effects/targets until the end of a long
                // round merely because subsequent workloads stay below threshold.
                if (count != 0) ResetCache();
                return false;
            }
            if (conditional.AttributeValueAsTags.IsDefault || !ReferenceEqualityOnly(target) || !EnsureSafety()) { fallbacks++; return false; }
            queries++;
            bool notEquals = conditional.ComparisonOperator == PropertyConditional.ComparisonOperatorType.NotEquals;
            if (target == null) { result = notEquals; return true; }
            if (conditional.AttributeValueAsTags.Length == 0) { result = !notEquals; return true; }
            // Preserve the native cheap-success case. A positive witness from
            // the first duration entry needs no audit of unrelated later entries.
            // Audit this entry's live state, including custom Equals fallbacks.
            if (sources.Length != 0 && StatusEffect.DurationList.Count != 0)
            {
                DurationListElement first = StatusEffect.DurationList[0];
                if (first != null && Audit(first.Parent, first.Targets, ref sources[0]) && first.Targets.Contains(target))
                {
                    bool allEarly = true;
                    foreach (Identifier tag in conditional.AttributeValueAsTags)
                        if (!first.Parent.HasTag(tag)) { allEarly = false; break; }
                    if (allEarly) { result = !notEquals; return true; }
                }
            }
            if (!Refresh()) { fallbacks++; return false; }
            TargetState state = Targets.GetValue(target, _ => new TargetState());
            if (state.Generation != generation)
            {
                state.Tags.Clear(); state.HasEffect = false;
                for (int i = 0; i < count; i++)
                {
                    Source source = sources[i];
                    if (!source.Targets.Contains(target)) continue;
                    state.HasEffect = true;
                    state.Tags.UnionWith(source.Tags);
                }
                state.Generation = generation; rebuiltTargets++;
            }
            else reusedTargets++;
            bool all = true;
            foreach (Identifier tag in conditional.AttributeValueAsTags)
                if (tag == null ? !state.HasEffect : !state.Tags.Contains(tag)) { all = false; break; }
            result = notEquals ? !all : all;
            return true;
        }
        internal static string Report()
        {
            string report = "live status tags: installed=" + installed + "; queries=" + queries + "; source_audits=" + audits +
                "; target_rebuilds=" + rebuiltTargets + "; target_reuses=" + reusedTargets + "; fallbacks=" + fallbacks;
            queries = audits = rebuiltTargets = reusedTargets = fallbacks = 0;
            return report;
        }
        internal static void ResetCache()
        {
            Targets.Clear(); EqualitySupport.Clear(); Array.Clear(sources); count = 0;
            durationVersion = delayVersion = -1; generation++;
            inStep = safetyChecked = safe = false;
        }
        internal static void Reset() { installed = false; ResetCache(); queries = audits = rebuiltTargets = reusedTargets = fallbacks = 0; }
    }
}
