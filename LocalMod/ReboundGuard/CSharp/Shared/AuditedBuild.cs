using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Barotrauma;
using FarseerPhysics.Dynamics.Contacts;

namespace ReboundGuard
{
    // Exact runtime binaries from LuaCs release c6e8829143c98162ed376aee33eb9799602365ac.
    // Resolved IL (including locals and exception regions) was compared with the
    // previously audited build. Relevant methods are unchanged; raw tokens moved.
    // Publicized compilation references are deliberately NOT runtime allowlisted.
    internal static class AuditedBuild
    {
        private static readonly ConcurrentDictionary<Assembly, string> Hashes = new ConcurrentDictionary<Assembly, string>();
        internal static string Hash(Assembly assembly) => Hashes.GetOrAdd(assembly, value =>
        {
            try
            {
                using var stream = File.OpenRead(value.Location);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception) { return "unavailable"; }
        });

        internal static bool CurrentGame
        {
            get
            {
                Assembly game = typeof(Item).Assembly;
                if (game.GetName().Version != new Version(1, 13, 5, 0)) return false;
                string expected = game.GetName().Name == "Barotrauma"
                    ? "EF31A7236DDE833382F53DF71B76C102B0F2BB3BB2A035DEDDA85ED5C276822A"
                    : game.GetName().Name == "DedicatedServer"
                        ? "705A4F5511EDB16CE59CC0F5B5A7F14BB3EB1FFDA3AB4F3C5EDCD49E834D4C16" : "";
                return Hash(game) == expected && Hash(Assembly.Load("BarotraumaCore")) ==
                    "3DF40ECB5ED3B7EEF7C4E7959D9A802FDB6CCA20E04FFE302A0208EA167AB0F2";
            }
        }

        internal static bool CurrentPhysics => Hash(typeof(ContactSolver).Assembly) ==
            "227B945B9F57A592864CE4A17D85725961612B600EC45D7DE0E6FF9514900403";
    }
}
