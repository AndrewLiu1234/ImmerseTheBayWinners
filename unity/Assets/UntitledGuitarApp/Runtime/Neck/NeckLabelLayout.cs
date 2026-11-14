// NeckLabelLayout: where the finger numbers go. Pure layout on dot positions (no music theory), unit-tested in NeckTests.cs.
//
// Why callouts. Text on the glasses must be at least 1.2 degrees tall (MASTER_PLAN section 17), which at the floating
// neck's distance is about 19 mm: almost three string spacings. A digit that size cannot sit on its dot without covering
// the neighbouring strings and dots. So each number sits just outside the fretboard, level with its dot along the neck, on
// the side its dot is nearer to (high-E side for strings 1-3, low-E side for strings 4-6), joined to the dot by a dotted
// leader. When two dots share a fret on the same side, the one further from that edge takes the other side; only when
// both sides are taken does a number slide along the neck.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Neck
{
    /// <summary>Where one finger number is drawn, in neck-local millimetres.</summary>
    public struct LabelSlot
    {
        /// <summary>Index of the dot (in the array given to Compute) this number belongs to.</summary>
        public int dotIndex;

        /// <summary>Centre of the digit along the neck, mm.</summary>
        public float xMm;

        /// <summary>Centre of the digit across the neck, mm (outside the fretboard).</summary>
        public float zMm;

        /// <summary>+1 = beyond the string 1 (high E) edge, -1 = beyond the string 6 (low E) edge.</summary>
        public int side;
    }

    public static class NeckLabelLayout
    {
        /// <summary>
        /// Places one number for every dot whose finger is 1 to 4. Writes them to <paramref name="slots"/> and returns how many
        /// there are (never more than slots.Length). Allocation-free.
        /// </summary>
        /// <param name="capMm">digit height</param>
        /// <param name="cellMm">room one digit needs along the neck</param>
        /// <param name="gapMm">gap between the fretboard edge and the digit</param>
        public static int Compute(NeckLayout layout, FingertipDot[] dots, int dotCount, float capMm, float cellMm, float gapMm, LabelSlot[] slots)
        {
            if (layout == null || dots == null || slots == null) return 0;
            int n = Mathf.Min(dotCount, dots.Length);
            int placed = 0;
            float minSpacing = cellMm * 1.3f;

            // Dots nearest their edge choose first, so the one that is further in is the one that moves. At most slots.Length
            // passes over at most a handful of dots: no sorting buffer is needed.
            while (placed < slots.Length)
            {
                int best = -1;
                float bestDistance = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (dots[i].finger < 1 || dots[i].finger > 4) continue;
                    if (IsPlaced(slots, placed, i)) continue;
                    float distance = NeckMath.FretboardHalfWidthMm(layout, dots[i].xMm) - Mathf.Abs(dots[i].zMm);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = i;
                    }
                }
                if (best < 0) break;

                FingertipDot dot = dots[best];
                int side = dot.zMm >= 0f ? 1 : -1;
                float x = dot.xMm;
                if (!IsFree(slots, placed, side, x, minSpacing))
                {
                    if (IsFree(slots, placed, -side, x, minSpacing))
                    {
                        side = -side;
                    }
                    else
                    {
                        // Both sides are taken at this fret: slide along the neck on the near side, as little as possible.
                        for (int k = 1; k <= 8; k++)
                        {
                            float toBridge = dot.xMm + k * minSpacing;
                            float toNut = dot.xMm - k * minSpacing;
                            if (IsFree(slots, placed, side, toBridge, minSpacing)) { x = toBridge; break; }
                            if (toNut > cellMm * 0.5f && IsFree(slots, placed, side, toNut, minSpacing)) { x = toNut; break; }
                        }
                    }
                }

                LabelSlot slot;
                slot.dotIndex = best;
                slot.side = side;
                slot.xMm = x;
                slot.zMm = side * (NeckMath.FretboardHalfWidthMm(layout, x) + gapMm + capMm * 0.5f);
                slots[placed++] = slot;
            }
            return placed;
        }

        static bool IsPlaced(LabelSlot[] slots, int count, int dotIndex)
        {
            for (int i = 0; i < count; i++)
                if (slots[i].dotIndex == dotIndex) return true;
            return false;
        }

        static bool IsFree(LabelSlot[] slots, int count, int side, float xMm, float minSpacingMm)
        {
            for (int i = 0; i < count; i++)
                if (slots[i].side == side && Mathf.Abs(slots[i].xMm - xMm) < minSpacingMm) return false;
            return true;
        }
    }
}
