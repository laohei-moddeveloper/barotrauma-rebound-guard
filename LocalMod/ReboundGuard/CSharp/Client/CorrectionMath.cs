using System;
using Microsoft.Xna.Framework;

namespace ReboundGuard
{
    public static class CorrectionMath
    {
        // Preserve movement since the acknowledged input, while applying the entire
        // authoritative error. This is an approximation, not deterministic input replay.
        public static bool TryProject(Vector2 historical, Vector2 current, Vector2 error, out Vector2 target)
        {
            target = historical;
            if (!Finite(historical) || !Finite(current) || !Finite(error)) { return false; }
            float magnitude = error.Length();
            if (magnitude <= 0.5f || magnitude > Settings.MaximumCorrection) { return false; }
            Vector2 candidate = current + error;
            Vector2 preservedTravel = candidate - historical;
            if (!Finite(candidate) || preservedTravel.LengthSquared() > Settings.MaximumPreservedTravel * Settings.MaximumPreservedTravel)
                return false;
            // No benefit if prediction has not advanced, or if projection increases
            // the immediate jump. Leave these cases to the original correction.
            if (preservedTravel.LengthSquared() < 0.000001f ||
                Vector2.DistanceSquared(current, candidate) >= Vector2.DistanceSquared(current, historical))
                return false;
            target = candidate;
            return true;
        }

        private static bool Finite(Vector2 value)
        {
            return !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
                   !float.IsNaN(value.Y) && !float.IsInfinity(value.Y);
        }
    }
}
