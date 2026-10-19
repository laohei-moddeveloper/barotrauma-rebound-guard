using System;
using System.IO;
using System.Linq;
using Barotrauma;
using FarseerPhysics.Dynamics.Contacts;
using HarmonyLib;
using ReboundGuard;

namespace ReboundGuardTests
{
    public static class PluginLifecycleTests
    {
        public static object Run()
        {
            var plugin = new Plugin(); string path = null;
            string owner = "barotrauma.reboundguard.experimental.v040";
            Type logger = typeof(Plugin).Assembly.GetType("ReboundGuard.DiagnosticLog");
            try
            {
                plugin.Initialize(); plugin.OnLoadCompleted();
                var loop = Harmony.GetPatchInfo(AccessTools.Method(typeof(GameScreen), "Update", new[] { typeof(double) }));
                if (loop == null || !loop.Owners.Contains(owner)) throw new Exception("Plugin initialization failed or removed all patches");
                foreach (var method in new[] { AccessTools.Method(typeof(StatusEffect), "AddNearbyTargets"), AccessTools.Method(typeof(ContactSolver), "InitializeVelocityConstraints") })
                    if (Harmony.GetPatchInfo(method)?.Owners.Contains(owner) != true) throw new Exception("Default feature missing after full plugin initialization: " + method.Name);
                if (typeof(Item).Assembly.GetName().Name == "Barotrauma")
                {
                    foreach (var method in new[] {
                        AccessTools.DeclaredMethod(typeof(GameMain), "Update", new[] { typeof(Microsoft.Xna.Framework.GameTime) }),
                        AccessTools.DeclaredMethod(typeof(GameMain), "Draw", new[] { typeof(Microsoft.Xna.Framework.GameTime) }),
                        AccessTools.DeclaredMethod(typeof(GameScreen), "Draw") })
                        if (method == null || Harmony.GetPatchInfo(method)?.Owners.Contains(owner) != true)
                            throw new Exception("Client frame measurement hook missing");
                }
                AccessTools.Method(typeof(Plugin), "Log").Invoke(null, new object[] { "isolated lifecycle diagnostic marker" });
                path = (string)AccessTools.PropertyGetter(logger, "CurrentPath").Invoke(null, null);
            }
            finally { plugin.Dispose(); }
            if (PatchProcessor.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method)?.Owners.Contains(owner) == true))
                throw new Exception("Plugin Dispose left active patches");
            if (path == null || !File.Exists(path) || !File.ReadAllText(path).Contains("isolated lifecycle diagnostic marker"))
                throw new Exception("Bounded background diagnostic writer did not flush on Dispose");
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Test diagnostics escaped isolated host");
            try
            {
                plugin.Initialize(); plugin.OnLoadCompleted();
                string profilerReport = (string)AccessTools.Method(typeof(Plugin).Assembly.GetType("ReboundGuard.SubsystemProfiler"), "Report").Invoke(null, null);
                if (!profilerReport.Contains("World.Step") || !profilerReport.Contains("Powered.UpdatePower")) throw new Exception("Reload skipped profiler hooks due to stale metric state");
            }
            finally { plugin.Dispose(); }
            if (PatchProcessor.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method)?.Owners.Contains(owner) == true))
                throw new Exception("Reload Dispose left active patches");
            return new { initialization = "all default features installed together", dispose = "all own patches removed", reload = "same plugin instance initializes/disposes twice; profiler reset", diagnostic_writer = "marker flushed under isolated host", note = "Plugin lifecycle only; no game entry point or live round started." };
        }
    }
}
