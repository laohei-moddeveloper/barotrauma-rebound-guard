using System;
using System.Diagnostics;
using System.Reflection;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace ReboundGuard
{
    internal static class ClientFrameProfiler
    {
        private static FrameHealth health = new FrameHealth();
        private static bool Active => Settings.Diagnostics && Settings.DetailedProfiling &&
            GameMain.NetworkMember != null && GameMain.GameSession != null && ReferenceEquals(Screen.Selected, GameMain.GameScreen) &&
            GameMain.Instance != null && !GameMain.Instance.LoadingScreenOpen && !GameMain.Instance.Paused && GameMain.WindowActive;
        internal static void Install(Harmony harmony)
        {
            health = new FrameHealth();
            if (!Settings.Diagnostics || !Settings.DetailedProfiling) return;
            Add(harmony, AccessTools.DeclaredMethod(typeof(GameMain), "Update", new[] { typeof(GameTime) }), nameof(BeforeUpdate), nameof(AfterUpdate));
            Add(harmony, AccessTools.DeclaredMethod(typeof(GameMain), "Draw", new[] { typeof(GameTime) }), nameof(BeforeDraw), nameof(AfterDraw));
            Add(harmony, AccessTools.DeclaredMethod(typeof(GameScreen), "Draw"), nameof(BeforeGameDraw), nameof(AfterGameDraw));
        }
        private static void Add(Harmony harmony, MethodInfo method, string before, string after)
        {
            if (method == null) { Plugin.Log("client frame measurement unavailable: " + before); return; }
            // Observe calls only; a void finalizer preserves original exceptions.
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientFrameProfiler), before),
                finalizer: new HarmonyMethod(typeof(ClientFrameProfiler), after));
            Plugin.Log("client frame measurement installed: " + method.DeclaringType.Name + "." + method.Name);
        }
        private static void BeforeUpdate(out long __state)
        {
            __state = 0;
            if (!Active) { health.Pause(); return; }
            health.BeginUpdate(); __state = Stopwatch.GetTimestamp();
        }
        private static void AfterUpdate(long __state)
        {
            if (__state != 0) health.EndUpdate(Milliseconds(Stopwatch.GetTimestamp() - __state));
        }
        private static void BeforeDraw(out long __state)
        {
            __state = 0;
            if (!Active) { health.Pause(); return; }
            __state = Stopwatch.GetTimestamp();
        }
        private static void AfterDraw(long __state)
        {
            if (__state != 0) health.ObserveDraw(Milliseconds(__state), Milliseconds(Stopwatch.GetTimestamp() - __state));
        }
        private static void BeforeGameDraw(out long __state) { __state = Active ? Stopwatch.GetTimestamp() : 0; }
        private static void AfterGameDraw(long __state)
        {
            if (__state != 0) health.ObserveGameDraw(Milliseconds(Stopwatch.GetTimestamp() - __state));
        }
        private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        internal static void ObserveSimulationStep() { health.ObserveSimulationStep(); }
        internal static void Report() { if (Settings.DetailedProfiling) Plugin.Log(health.Report()); }
    }

    public partial class Plugin
    {
        static partial void ObserveClientStep() { ClientFrameProfiler.ObserveSimulationStep(); }
        static partial void ReportClientFrames() { ClientFrameProfiler.Report(); }
    }
}
