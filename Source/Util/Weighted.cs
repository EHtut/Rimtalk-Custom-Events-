using System.Collections.Generic;

namespace RimTalkCustomEvents.Util
{
    public static class Weighted
    {
        /// <summary>
        /// Picks an index in proportion to its weight. Split out from the caller so it takes
        /// the roll as a parameter and can be checked without a game running.
        /// </summary>
        /// <param name="weights">Relative weights. Negatives are treated as zero.</param>
        /// <param name="roll01">A value in [0, 1).</param>
        /// <returns>The chosen index, or -1 when no weight is positive.</returns>
        public static int PickIndex(IList<float> weights, float roll01)
        {
            if (weights == null || weights.Count == 0) return -1;

            var total = 0f;
            for (var i = 0; i < weights.Count; i++)
            {
                if (weights[i] > 0f) total += weights[i];
            }

            if (total <= 0f) return -1;

            if (roll01 < 0f) roll01 = 0f;
            if (roll01 >= 1f) roll01 = 0.9999999f;

            var target = roll01 * total;
            var running = 0f;

            for (var i = 0; i < weights.Count; i++)
            {
                if (weights[i] <= 0f) continue;

                running += weights[i];
                if (target < running) return i;
            }

            // Only reachable through floating-point drift; fall back to the last positive arm.
            for (var i = weights.Count - 1; i >= 0; i--)
            {
                if (weights[i] > 0f) return i;
            }

            return -1;
        }
    }
}
