using System;
using System.Diagnostics;
using System.Reflection;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;

[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Barotrauma")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("BarotraumaCore")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("DedicatedServer")]

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public string AssemblyName { get; }
        public IgnoresAccessChecksToAttribute(string assemblyName) { AssemblyName = assemblyName; }
    }
}

namespace ReboundGuard
{
    public partial class Plugin : IAssemblyPlugin
    {
        internal const string HarmonyId = "barotrauma.reboundguard.experimental.v040";
        internal const string ModVersion = "0.4.0";
        private Harmony harmony;
        private static long windowStart;
        private static int steps;
        private static double simulated, workMs, worstMs;
        private static bool activeWindow;
        [ThreadStatic] private static long stepGcStart;
        internal static int LargeCorrections, AdjustedCorrections;
        internal static double WorstOriginalJump, WorstAdjustedJump;
        internal static bool ClientPatchInstalled;

        public void Initialize()
        {
            harmony = new Harmony(HarmonyId);
            try
            {
                DiagnosticLog.Start();
                CrashEvidence.Start();
                NativeModHotPaths.Start();
                ResetWindow();
                SubsystemProfiler.Reset();
                BinaryEvidence.Report();
                if (Settings.Diagnostics)
                {
                    harmony.Patch(AccessTools.Method(typeof(GameScreen), "Update", new[] { typeof(double) }),
                        prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeStep)),
                        postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterStep)),
                        finalizer: new HarmonyMethod(typeof(Plugin), nameof(FinishStep)));
                }
                InstallClientPatch(harmony);
                InstallServerPatch(harmony);
                LuaBridgeOptimizer.Install(harmony);
                LoadingLifecyclePatch.Install(harmony);
                BodyMaintenance.Install(harmony);
                ContainerEffectFastPath.Install(harmony);
                ParallelConstraintPreparation.Install(harmony);
                NearbyItemIndex.Install(harmony);
                ConstraintSolverResearch.Install(harmony);
                if (Settings.DetailedProfiling) { SubsystemProfiler.Install(harmony); }
                Log("loaded; version=" + ModVersion + "; simulation monitor=" + Settings.Diagnostics +
                    "; client correction patch=" + ClientPatchInstalled +
                    "; experimental correction=" + Settings.ApplyExperimentalCorrection +
                    "; diagnostic_file=" + DiagnosticLog.CurrentPath + "; diagnostic_error=" + DiagnosticLog.Failure);
            }
            catch (Exception exception)
            {
                harmony.UnpatchSelf();
                ClientPatchInstalled = false;
                Log("initialization failed; patches removed: " + exception.Message);
                BodyMaintenance.Reset(); ContainerEffectFastPath.Reset(); NearbyItemIndex.Reset(); ParallelConstraintPreparation.Reset();
                SubsystemProfiler.Reset();
                NativeModHotPaths.Stop(true); CrashEvidence.Stop();
                LuaBridgeOptimizer.Stop();
                DiagnosticLog.Stop();
            }
        }

        static partial void InstallClientPatch(Harmony patcher);
        static partial void InstallServerPatch(Harmony patcher);
        public void PreInitPatching() { }
        public void OnLoadCompleted() { BodyMaintenance.RefreshSafety(); }
        public void Dispose()
        {
            NativeModHotPaths.Stop(true);
            LuaBridgeOptimizer.Stop();
            harmony?.UnpatchSelf();
            ClientPatchInstalled = false;
            BodyMaintenance.Reset();
            ContainerEffectFastPath.Reset();
            NearbyItemIndex.Reset();
            ParallelConstraintPreparation.Reset();
            SubsystemProfiler.Reset();
            Log("unloaded; version=" + ModVersion);
            DiagnosticLog.Stop();
            CrashEvidence.Stop();
            ResetWindow();
        }

        private static void BeforeStep(out long __state)
        {
            // Exclude menu/content compilation from the first simulation window.
            if (!activeWindow && GameMain.NetworkMember != null && GameMain.GameSession != null) StepHealth.Reset();
            if (Settings.DetailedProfiling) SubsystemProfiler.BeginStep();
            stepGcStart = StepHealth.CollectionTotal();
            __state = Stopwatch.GetTimestamp();
        }
        private static void FinishStep(Exception __exception)
        {
            SubsystemProfiler.EndStep();
            if (__exception != null) CrashEvidence.Record("GameScreen.Update exception (not suppressed)", __exception);
        }
        private static void AfterStep(double deltaTime, long __state)
        {
            if (GameMain.NetworkMember == null || GameMain.GameSession == null)
            {
                ResetWindow();
                return;
            }
            long now = Stopwatch.GetTimestamp();
            if (!activeWindow) { windowStart = __state; activeWindow = true; }
            double elapsedMs = (now - __state) * 1000.0 / Stopwatch.Frequency;
            StepHealth.Observe(elapsedMs, __state, now, stepGcStart);
            steps++;
            simulated += deltaTime;
            workMs += elapsedMs;
            worstMs = Math.Max(worstMs, elapsedMs);
            double wall = (now - windowStart) / (double)Stopwatch.Frequency;
            if (wall < 10.0) { return; }
            Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "simulation window={0:F1}s updates/s={1:F1} sim/wall={2:F2} avg_update_ms={3:F2} max_update_ms={4:F2}; " +
                "large_corrections={5} adjusted={6} max_original_jump={7:F2} max_adjusted_jump={8:F2} sim_units; correction_enabled={9}",
                wall, steps / wall, simulated / wall, workMs / steps, worstMs,
                LargeCorrections, AdjustedCorrections, WorstOriginalJump, WorstAdjustedJump,
                Settings.ApplyExperimentalCorrection && ClientPatchInstalled));
            Log("body maintenance: " + BodyMaintenance.Report());
            Log(ContainerEffectFastPath.Report());
            Log(ParallelConstraintPreparation.Report());
            Log(NearbyItemIndex.Report());
            Log(StepHealth.Report(now));
            Log(CrashEvidence.MemoryReport());
            Log(CorrectionTelemetry.Report());
            Log(NativeModHotPaths.Report());
            Log(LuaBridgeOptimizer.Report());
            if (Settings.ParallelConstraintSolver) Log(ConstraintSolverResearch.Report());
            if (Settings.DetailedProfiling) { Log(SubsystemProfiler.WorstStepReport()); Log(SubsystemProfiler.Report()); }
            ReportServer();
            if (DiagnosticLog.Failure != null) DebugConsole.NewMessage("[ReboundGuard] report file error: " + DiagnosticLog.Failure);
            ResetWindow(false);
        }

        static partial void ReportServer();

        private static void ResetWindow(bool inactive = true)
        {
            windowStart = Stopwatch.GetTimestamp();
            activeWindow = !inactive;
            StepHealth.Reset(inactive);
            CorrectionTelemetry.Reset();
            steps = LargeCorrections = AdjustedCorrections = 0;
            simulated = workMs = worstMs = WorstOriginalJump = WorstAdjustedJump = 0;
        }

        internal static void Log(string message)
        {
            DebugConsole.NewMessage("[ReboundGuard] " + message);
            DiagnosticLog.Append(message);
        }
    }
}
