using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Barotrauma;
using FarseerPhysics.Dynamics.Contacts;
using HarmonyLib;

namespace ReboundGuard
{
    internal static class BinaryEvidence
    {
        // Startup only. Different LuaCs builds can have identical version numbers.
        internal static void Report()
        {
            Plugin.Log("environment: mod=" + Plugin.ModVersion + "; role=" + (typeof(Item).Assembly.GetName().Name == "DedicatedServer" ? "server" : "client") +
                "; build=" + Plugin.BuildLabel + "; runtime=" + Environment.Version + "; processors=" + Environment.ProcessorCount + "; max_workers=" + Settings.MaximumWorkers);
            foreach (Assembly assembly in new[] { typeof(Item).Assembly, typeof(ContactSolver).Assembly, typeof(Harmony).Assembly })
            {
                try
                {
                    using (var file = File.OpenRead(assembly.Location))
                        Plugin.Log("binary: name=" + assembly.GetName().Name + "; version=" + assembly.GetName().Version + "; sha256=" + Convert.ToHexString(SHA256.HashData(file)) +
                            "; debug_flags=" + (assembly.GetCustomAttribute<DebuggableAttribute>()?.DebuggingFlags.ToString() ?? "none"));
                }
                catch (Exception error) { Plugin.Log("binary evidence unavailable: " + assembly.GetName().Name + "; " + error.GetType().Name); }
            }
            foreach (MethodBase method in new[] { AccessTools.Method(typeof(GameScreen), "Update", new[] { typeof(double) }),
                AccessTools.Method(typeof(PhysicsBody), "Update"), AccessTools.Method(typeof(StatusEffect), "AddNearbyTargets"),
                AccessTools.Method(typeof(ContactSolver), "InitializeVelocityConstraints") })
            {
                byte[] il = method?.GetMethodBody()?.GetILAsByteArray();
                Plugin.Log("method evidence: " + method?.DeclaringType.Name + "." + method?.Name + "; il_sha256=" + (il == null ? "unavailable" : Convert.ToHexString(SHA256.HashData(il))));
            }
        }
    }
}
