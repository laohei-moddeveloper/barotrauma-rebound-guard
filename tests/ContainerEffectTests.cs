using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ReboundGuard;

namespace ReboundGuardTests
{
    public static class ContainerEffectTests
    {
        private static int checks;
        private static void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); checks++; }
        private static StatusEffect Effect(ActionType action, StatusEffect.TargetType target = StatusEffect.TargetType.NearbyItems)
        {
            var effect = (StatusEffect)RuntimeHelpers.GetUninitializedObject(typeof(StatusEffect));
            AccessTools.Field(typeof(StatusEffect), "type").SetValue(effect, action);
            AccessTools.Field(typeof(StatusEffect), "targetTypes").SetValue(effect, target);
            AccessTools.PropertySetter(typeof(StatusEffect), "Range").Invoke(effect, new object[] { 100f });
            return effect;
        }
        private static T Call<T>(string method, params object[] args) =>
            (T)AccessTools.Method(typeof(ContainerEffectFastPath), method).Invoke(null, args);
        private static void SetInstalled(bool value) => AccessTools.Field(typeof(ContainerEffectFastPath), "installed").SetValue(null, value);
        private static void Begin() => AccessTools.Method(typeof(ContainerEffectFastPath), "BeginSimulation").Invoke(null, null);
        private static void End() => AccessTools.Method(typeof(ContainerEffectFastPath), "EndSimulation").Invoke(null, null);
        private static void EmptyPrefix() { }

        public static object Run()
        {
            Check(Call<bool>("SupportedCodeMatches"), "actual container Update, predicate and base Apply fingerprints");
            foreach (ActionType action in Enum.GetValues<ActionType>())
            {
                var effect = Effect(action);
                bool expected = action != ActionType.OnActive && action != ActionType.OnContaining && action != ActionType.OnWearing;
                Check(Call<bool>("IsIrrelevantNearbyEffect", effect) == expected, "event classification: " + action);
                if (expected)
                {
                    // Uninitialized currentTargets would throw if the native Apply
                    // reached work beyond its event-type early return.
                    foreach (var updateAction in new[] { ActionType.OnActive, ActionType.OnContaining, ActionType.OnWearing })
                        effect.Apply(updateAction, 1f / 60f, null, (IReadOnlyList<ISerializableEntity>)null);
                    Check(true, "all original Update Apply calls immediately return: " + action);
                }
            }
            Check(!Call<bool>("IsIrrelevantNearbyEffect", Effect(ActionType.OnInserted, StatusEffect.TargetType.This)), "cheap non-nearby effects stay original");
            var zeroRange = Effect(ActionType.OnInserted);
            AccessTools.PropertySetter(typeof(StatusEffect), "Range").Invoke(zeroRange, new object[] { 0f });
            Check(!Call<bool>("IsIrrelevantNearbyEffect", zeroRange), "zero range stays original");
            Type delayed = typeof(StatusEffect).Assembly.GetType("Barotrauma.DelayedEffect");
            Check(delayed != null && !Call<bool>("IsIrrelevantNearbyEffect", RuntimeHelpers.GetUninitializedObject(delayed)), "custom/DelayedEffect subclass stays original");

            var container = (ItemContainer)RuntimeHelpers.GetUninitializedObject(typeof(ItemContainer));
            var item = (Item)RuntimeHelpers.GetUninitializedObject(typeof(Item));
            AccessTools.Field(typeof(ItemComponent), "item").SetValue(container, item);
            AccessTools.Field(typeof(ItemContainer), "targets").SetValue(container, new List<ISerializableEntity>());
            var active = new ItemContainer.ActiveContainedItem(item, Effect(ActionType.OnInserted), false, false, false);
            var fast = (Func<ItemContainer, ItemContainer.ActiveContainedItem, bool>)AccessTools.Method(typeof(ContainerEffectFastPath), "ShouldApplyDuringUpdate")
                .CreateDelegate(typeof(Func<ItemContainer, ItemContainer.ActiveContainedItem, bool>));
            var update = AccessTools.Method(typeof(ItemContainer), "Update");
            var harmony = new Harmony("barotrauma.reboundguard.experimental.v040");
            var foreign = new Harmony("reboundguard.foreign.container.test");
            bool enabled = Settings.SkipIrrelevantContainerSearches;
            try
            {
                Settings.SkipIrrelevantContainerSearches = true;
                Check(fast(container, active), "outside simulation uses original target gathering");
                harmony.Patch(update, transpiler: new HarmonyMethod(typeof(ContainerEffectFastPath), "Transpiler"));
                Check(Harmony.GetPatchInfo(update).Owners.Contains(harmony.Id), "actual container transpiler emits under native runtime");
                foreach (string name in new[] { "OnItemContained", "OnItemRemoved" })
                    Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(ItemContainer), name)) == null, name + " remains unpatched");
                SetInstalled(true); Begin();
                long beforeSafety = (long)AccessTools.Field(typeof(ContainerEffectFastPath), "safetyChecks").GetValue(null);
                for (int i = 0; i < 100; i++) Begin();
                Check((long)AccessTools.Field(typeof(ContainerEffectFastPath), "safetyChecks").GetValue(null) == beforeSafety, "idle steps perform no Harmony conflict scan");
                Check(!fast(container, active), "irrelevant nearby gather skipped inside simulation");
                Check(!fast(container, active), "second irrelevant gather still skipped");
                Check((long)AccessTools.Field(typeof(ContainerEffectFastPath), "safetyChecks").GetValue(null) == beforeSafety + 1, "applicable gathers share one conflict check per step");
                End(); Check(fast(container, active), "scope exit restores original");
                var apply = typeof(StatusEffect).GetMethods().Single(m => m.Name == "Apply" && m.GetParameters().Length == 5 &&
                    m.GetParameters()[3].ParameterType == typeof(IReadOnlyList<ISerializableEntity>));
                foreign.Patch(apply, prefix: new HarmonyMethod(typeof(ContainerEffectTests), nameof(EmptyPrefix)));
                Begin(); Check(fast(container, active), "foreign Apply hook forces original target gathering"); End();
                foreign.UnpatchSelf();
                Settings.SkipIrrelevantContainerSearches = false;
                Begin(); Check(fast(container, active), "disabled path original"); End();
                Settings.SkipIrrelevantContainerSearches = true; Begin();
                // Native nearby scan of synthetic distant items; no prefab,
                // graphics or live submarine initialization is required.
                int originalCount = Item.ItemList.Count;
                var benchmarks = new List<object>();
                try
                {
                    foreach (int count in new[] { 0, 2048, 8192 })
                    {
                        while (Item.ItemList.Count > originalCount) Item.ItemList.RemoveAt(Item.ItemList.Count - 1);
                        for (int i = 0; i < count; i++)
                        {
                            var distant = (Item)RuntimeHelpers.GetUninitializedObject(typeof(Item));
                            AccessTools.Field(typeof(MapEntity), "rect").SetValue(distant, new Rectangle(100000 + i, 0, 1, 1));
                            Item.ItemList.Add(distant);
                        }
                        Action original = () => container.ShouldApplyEffects(active);
                        Action optimized = () => fast(container, active);
                        for (int i = 0; i < 50; i++) { original(); optimized(); }
                        benchmarks.Add(new { items = count, original_ns = Time(original), guard_ns = Time(optimized) });
                    }
                    // Actual matching continuous effect still gathers targets.
                    var ongoing = new ItemContainer.ActiveContainedItem(item, Effect(ActionType.OnContaining), false, false, false);
                    Check(fast(container, ongoing), "continuous effect predicate still runs");
                }
                finally { while (Item.ItemList.Count > originalCount) Item.ItemList.RemoveAt(Item.ItemList.Count - 1); }
                return new { checks, benchmarks, note = "Native target predicate on synthetic distant items. Does not measure an entire container Update or a live round. Per-step conflict scan measured separately.", safety_check_ns = Time(Begin) };
            }
            finally { foreign.UnpatchSelf(); harmony.UnpatchSelf(); End(); SetInstalled(false); Settings.SkipIrrelevantContainerSearches = enabled; }
        }
        private static double Time(Action action)
        {
            var samples = new double[11];
            for (int sample = 0; sample < samples.Length; sample++)
            {
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < 200; i++) action();
                samples[sample] = (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / 200;
            }
            Array.Sort(samples); return samples[samples.Length / 2];
        }
    }
}
