using System;
using System.Globalization;

namespace ReboundGuard
{
    internal static class CorrectionTelemetry
    {
        private static int local, other, overHalf, overTwo;
        private static double maxJump, maxError;
        internal static void Observe(bool controlled, double targetDisplacement, double historicalError)
        {
            if (!controlled) { other++; return; }
            local++;
            if (targetDisplacement > .5) overHalf++;
            if (targetDisplacement > 2) overTwo++;
            maxJump = Math.Max(maxJump, targetDisplacement); maxError = Math.Max(maxError, historicalError);
        }
        internal static string Report() => string.Format(CultureInfo.InvariantCulture,
            "correction branch: controlled_character_events={0}; other_character_events={1}; controlled_target_displacements_over_0_5={2}; controlled_target_displacements_over_2={3}; max_controlled_target_displacement={4:F3}; max_controlled_ack_error={5:F3}; units=sim; rendered_displacement_not_measured=True",
            local, other, overHalf, overTwo, maxJump, maxError);
        internal static void Reset() { local = other = overHalf = overTwo = 0; maxJump = maxError = 0; }
    }
}
