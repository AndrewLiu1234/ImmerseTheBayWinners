// DwellEngine: one frame of gaze-dwell, as plain C# so it can be tested without a scene. GazeDwell (the MonoBehaviour)
// owns the registry of DwellTargets, reads the head, and raises events from what this reports.
//
// Step() does, in this order:
//   1. picks the focus: the armed item nearest the gaze ray, within its enter angle (or, if it is already being dwelled
//      on, within its wider exit angle: that is the hysteresis)
//   2. applies the confirm key: fires the focused item at once, or reports a global Confirmed when nothing is focused
//   3. advances every timer: the focused item sees the real angle, every other item "infinitely far", so a dwell that lost
//      focus resets and cooldowns keep counting down
// Nothing is allocated per call: candidate and result arrays are reused and only grow with the number of items.
using System;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Gaze
{
    /// <summary>What the engine needs from a target: where it is, and its timer.</summary>
    public interface IDwellItem
    {
        Vector3 Position { get; }
        DwellTimer Timer { get; }
    }

    public sealed class DwellEngine
    {
        GazeMath.Candidate[] candidates = new GazeMath.Candidate[16];
        IDwellItem[] fired = new IDwellItem[16];

        /// <summary>The item the gaze is on after the last Step, or null.</summary>
        public IDwellItem Focused { get; private set; }

        /// <summary>The focus before the last Step.</summary>
        public IDwellItem Previous { get; private set; }

        /// <summary>True if the last Step moved the focus (including onto or off an item).</summary>
        public bool FocusChanged { get { return !ReferenceEquals(Previous, Focused); } }

        /// <summary>How many items fired in the last Step (a dwell completed, or the confirm key fired the focus).</summary>
        public int FiredCount { get; private set; }

        /// <summary>The item the confirm key fired in the last Step, or null. It is also in the fired list.</summary>
        public IDwellItem Forced { get; private set; }

        /// <summary>True if the confirm key was pressed in the last Step with nothing focused.</summary>
        public bool ConfirmedRaised { get; private set; }

        /// <summary>The index-th item that fired in the last Step.</summary>
        public IDwellItem FiredAt(int index)
        {
            return fired[index];
        }

        /// <summary>
        /// Advances every item by <paramref name="dt"/> seconds. <paramref name="hasGaze"/> false means there is no head:
        /// nothing can be focused (the confirm key still works). Only the first <paramref name="count"/> items are read.
        /// </summary>
        public void Step(Vector3 origin, Vector3 forward, bool hasGaze, IDwellItem[] items, int count, float dt, bool confirm)
        {
            if (items == null || count < 0) count = 0;
            else if (count > items.Length) count = items.Length;

            if (count > candidates.Length)
            {
                int capacity = candidates.Length;
                while (capacity < count) capacity *= 2;   // not Mathf.NextPowerOfTwo: that is a native call, and this class stays plain C#
                Array.Resize(ref candidates, capacity);
                Array.Resize(ref fired, capacity);
            }

            Array.Clear(fired, 0, FiredCount);   // let go of last frame's items
            FiredCount = 0;
            Previous = Focused;
            Forced = null;
            ConfirmedRaised = false;

            // 1. The focus.
            int best = -1;
            float bestAngle = float.PositiveInfinity;
            if (hasGaze && count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    DwellTimer timer = items[i].Timer;
                    candidates[i] = new GazeMath.Candidate(
                        items[i].Position, timer.EnterAngleDeg, timer.ExitAngleDeg,
                        timer.IsArmed, timer.State == DwellState.Dwelling);
                }
                best = GazeMath.SelectNearest(origin, forward, candidates, count, out bestAngle);
            }
            IDwellItem next = best >= 0 ? items[best] : null;

            // 2. The confirm key.
            if (confirm)
            {
                if (next != null)
                {
                    if (next.Timer.ForceFire()) Forced = next;
                }
                else
                {
                    ConfirmedRaised = true;
                }
            }

            // 3. Every timer. A force-fired item is left in Fired for this frame, so it is seen in that state once.
            for (int i = 0; i < count; i++)
            {
                IDwellItem item = items[i];
                if (ReferenceEquals(item, Forced))
                {
                    fired[FiredCount++] = item;
                    continue;
                }
                float angle = i == best ? bestAngle : float.PositiveInfinity;
                if (item.Timer.Tick(angle, dt)) fired[FiredCount++] = item;
            }

            Focused = next;
        }

        /// <summary>Forgets the focus and last results (GazeDwell was disabled).</summary>
        public void Reset()
        {
            Array.Clear(fired, 0, FiredCount);
            FiredCount = 0;
            Focused = null;
            Previous = null;
            Forced = null;
            ConfirmedRaised = false;
        }
    }
}
