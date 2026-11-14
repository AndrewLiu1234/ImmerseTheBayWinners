// StrumMath: the pure timing rules behind GhostHandsDirector. Which strumming pose the ghost hand shows at a song position,
// and how far the fretting hand has morphed toward the next chord. No engine call and no allocation: every result is a
// struct of pose NAMES (StreamingAssets/poses/strumming-<name>.pose.json) and a weight. Unit-tested in
// Tests/EditMode/GhostHandsDirectorTests.cs.
//
// THE STRUM TIMELINE (positions in pattern slots: slot = Beat * SlotsPerBeat, wrapped to the pattern length = one bar).
// Every slot has an ENTRY pose, where the hand is at the slot's first instant: D -> down-start, U -> up-start,
// "-" -> pick-ready. Inside a slot (phase 0..1):
//   D / U   [0, StrokeFraction)   the stroke: start -> end pose (eased out, so the hand moves off on the slot's onset and
//                                 the pick is past the middle strings within the first ~30% of the stroke)
//           [StrokeFraction, 1)   recover: end pose -> the NEXT slot's entry pose, eased in and out
//   "-"     [0, StrokeFraction)   hover: hold pick-ready (above the strings, no stroke)
//           [StrokeFraction, 1)   prepare: pick-ready -> the next slot's entry pose
// So the hand never jumps: each slot ends exactly on the pose the next one starts with (the authored poses do not meet:
// their wrists: down-end to up-start 7 mm, up-end to down-start 22 mm). Why "-" hovers at pick-ready instead of staying
// on the last end pose: a beginner reads motion = strum and stillness = no strum, and pick-ready is the authored
// "hovering over the strings" pose (5 mm above them, art/blender/hand/pose_hands.py STRUM_POSES), so a missed slot is
// visibly different from a stroke.
// Count-in (Beat < 0): pick-ready, no strokes, except the last (1 - StrokeFraction) slot before beat 0, which moves to the
// first slot's entry pose (so the first stroke starts from its own start pose). Wait mode: pick-ready, no strokes.
// No song (inactive) or no pattern: rest.
//
// THE FRETTING MORPH: it starts exactly one beat before the change (BeatsToChange = 1) and takes one beat, clamped to
// [MinMorphSeconds, MaxMorphSeconds], but never more than the beat itself, so it always arrives by the change:
//   MinMorphSeconds 0.25  GHOST_HANDS.md's shortest ease ("250-300 ms"); below it a whole-hand move reads as a jump. A
//                         beat shorter than this (above 240 bpm, beyond every song and preset) still morphs in one beat.
//   MaxMorphSeconds 0.80  at slow presets (50% of 80 bpm = 1.5 s a beat) a beat-long morph leaves the hand between two
//                         shapes for most of the beat; capped, the new shape is complete 0.7 s before the change, which
//                         is when a beginner needs to see it.
// The progress is locked to the beat cursor (1 - BeatsToChange), not to a timer, so it stays in step at 50/75/100% tempo
// and freezes with the playhead in wait mode.
using System;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    /// <summary>What the strumming ghost hand is doing at one instant.</summary>
    public enum StrumPhase
    {
        /// <summary>No song, or the song has no strumming pattern: the rest pose.</summary>
        Rest = 0,
        /// <summary>Count-in or wait mode: hovering at pick-ready, no strokes.</summary>
        Ready = 1,
        /// <summary>The end of the count-in: pick-ready to the first slot's entry pose.</summary>
        LeadIn = 2,
        /// <summary>The first StrokeFraction of a D or U slot: the pick crosses the strings.</summary>
        Stroke = 3,
        /// <summary>The rest of a D or U slot: off the strings, back to the next slot's entry pose.</summary>
        Recover = 4,
        /// <summary>A "-" slot: hovering at pick-ready, then moving to the next slot's entry pose. No stroke.</summary>
        Hover = 5,
    }

    /// <summary>The strumming hand at one instant: show the blend of two poses, From at Blend 0 to To at Blend 1.</summary>
    public struct StrumSample
    {
        public StrumPhase Phase;
        /// <summary>Strumming pose name (StrumMath.Rest, DownStart, ...).</summary>
        public string From;
        /// <summary>Strumming pose name. Equal to From when the hand holds one pose.</summary>
        public string To;
        /// <summary>Weight of To, already eased, 0..1.</summary>
        public float Blend;
        /// <summary>Slot index inside the bar (0 .. pattern length - 1), or -1 outside the pattern (rest, count-in, waiting).</summary>
        public int Slot;
        /// <summary>'D', 'U' or '-' for the slot; '\0' when Slot is -1.</summary>
        public char Kind;
        /// <summary>Position inside the slot, 0..1; -1 when Slot is -1.</summary>
        public float SlotPhase;

        /// <summary>True while the pick crosses the strings (a D or U stroke).</summary>
        public bool Striking { get { return Phase == StrumPhase.Stroke; } }
    }

    public static class StrumMath
    {
        // ---- pose names: must match GhostPose.StrummingPoseNames / the files in StreamingAssets/poses -------------------
        public const string Rest = "rest";
        public const string DownStart = "down-start";
        public const string DownEnd = "down-end";
        public const string UpStart = "up-start";
        public const string UpEnd = "up-end";
        public const string PickReady = "pick-ready";

        /// <summary>The share of a D / U slot the stroke takes; the rest of the slot brings the hand back.</summary>
        public const float StrokeFraction = 0.5f;

        // ---- fretting morph -------------------------------------------------------------------------------------------
        /// <summary>Shortest morph (seconds) when the beat is longer than this; see the file header.</summary>
        public const float MinMorphSeconds = 0.25f;
        /// <summary>Longest morph (seconds); see the file header.</summary>
        public const float MaxMorphSeconds = 0.8f;
        /// <summary>Morph length without a tempo (BeatsPerSecond 0): PosedGhostHand's default ease.</summary>
        public const float DefaultMorphSeconds = PosedGhostHand.DefaultMorphSeconds;

        /// <summary>'D' for "D" or "d", 'U' for "U" or "u", '-' for anything else ("-", null, unknown).</summary>
        public static char SlotKind(string slot)
        {
            if (slot == null || slot.Length != 1) return '-';
            char c = slot[0];
            if (c == 'D' || c == 'd') return 'D';
            if (c == 'U' || c == 'u') return 'U';
            return '-';
        }

        /// <summary>The pose a slot starts on: D down-start, U up-start, "-" pick-ready.</summary>
        public static string EntryPose(char kind)
        {
            return kind == 'D' ? DownStart : kind == 'U' ? UpStart : PickReady;
        }

        /// <summary>The pose a slot's stroke ends on: D down-end, U up-end, "-" pick-ready.</summary>
        public static string StrokeEndPose(char kind)
        {
            return kind == 'D' ? DownEnd : kind == 'U' ? UpEnd : PickReady;
        }

        /// <summary>Smoothstep on t clamped to 0..1 (GhostPose.Ease's curve, repeated so this class needs nothing else).</summary>
        public static float Ease(float t)
        {
            if (!(t > 0f)) return 0f;
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);
        }

        /// <summary>Ease-out (fast start, soft landing) on t clamped to 0..1: the stroke leaves on the onset.</summary>
        public static float StrokeEase(float t)
        {
            if (!(t > 0f)) return 0f;
            if (t >= 1f) return 1f;
            float r = 1f - t;
            return 1f - r * r;
        }

        /// <summary>A non-negative index modulo count (count must be positive).</summary>
        public static int Wrap(long index, int count)
        {
            if (count <= 0) return 0;
            long m = index % count;
            if (m < 0) m += count;
            return (int)m;
        }

        /// <summary>
        /// Where a song position falls in the pattern: slot = floor(beat * slotsPerBeat) wrapped to slotCount, phase = the
        /// fraction of that slot. slotsPerBeat below 1 counts as 1. False (slot -1, phase -1) for a non-finite beat or an
        /// empty pattern. Negative beats wrap too (callers treat the count-in separately).
        /// </summary>
        public static bool Locate(double beat, int slotsPerBeat, int slotCount, out int slot, out float phase)
        {
            slot = -1;
            phase = -1f;
            if (slotCount <= 0 || double.IsNaN(beat) || double.IsInfinity(beat)) return false;
            double position = beat * Math.Max(1, slotsPerBeat);
            double whole = Math.Floor(position);
            if (whole > long.MaxValue / 2 || whole < long.MinValue / 2) return false;
            slot = Wrap((long)whole, slotCount);
            double fraction = position - whole;
            phase = fraction <= 0.0 ? 0f : fraction >= 1.0 ? 1f : (float)fraction;
            return true;
        }

        /// <summary>
        /// The strumming hand for one practice frame (see the file header for the timeline). active = PracticeFrame.Active,
        /// waiting = Waiting, beat = Beat, slots = StrumSlots (read only), slotsPerBeat = SlotsPerBeat. Allocation-free.
        /// </summary>
        public static StrumSample Sample(bool active, bool waiting, double beat, string[] slots, int slotsPerBeat)
        {
            StrumSample s;
            s.Slot = -1;
            s.Kind = '\0';
            s.SlotPhase = -1f;
            s.Blend = 0f;
            if (!active || slots == null || slots.Length == 0)
            {
                s.Phase = StrumPhase.Rest;
                s.From = s.To = Rest;
                return s;
            }
            if (waiting || double.IsNaN(beat) || double.IsInfinity(beat))
            {
                s.Phase = StrumPhase.Ready;
                s.From = s.To = PickReady;
                return s;
            }

            int n = slots.Length;
            double position = beat * Math.Max(1, slotsPerBeat);
            if (position < 0.0)
            {
                double lead = 1.0 - StrokeFraction;   // the count-in's last part of a slot
                char firstKind = SlotKind(slots[0]);
                if (position >= -lead && firstKind != '-')
                {
                    s.Phase = StrumPhase.LeadIn;
                    s.From = PickReady;
                    s.To = EntryPose(firstKind);
                    s.Blend = Ease((float)((position + lead) / lead));
                    return s;
                }
                s.Phase = StrumPhase.Ready;
                s.From = s.To = PickReady;
                return s;
            }

            int slot;
            float phase;
            if (!Locate(beat, slotsPerBeat, n, out slot, out phase))
            {
                s.Phase = StrumPhase.Ready;
                s.From = s.To = PickReady;
                return s;
            }
            char kind = SlotKind(slots[slot]);
            char next = SlotKind(slots[Wrap(slot + 1L, n)]);
            s.Slot = slot;
            s.Kind = kind;
            s.SlotPhase = phase;
            float back = (phase - StrokeFraction) / (1f - StrokeFraction);
            if (kind == '-')
            {
                s.Phase = StrumPhase.Hover;
                s.From = PickReady;
                s.To = phase < StrokeFraction ? PickReady : EntryPose(next);
                s.Blend = phase < StrokeFraction ? 0f : Ease(back);
                return s;
            }
            if (phase < StrokeFraction)
            {
                s.Phase = StrumPhase.Stroke;
                s.From = EntryPose(kind);
                s.To = StrokeEndPose(kind);
                s.Blend = StrokeEase(phase / StrokeFraction);
                return s;
            }
            s.Phase = StrumPhase.Recover;
            s.From = StrokeEndPose(kind);
            s.To = EntryPose(next);
            s.Blend = Ease(back);
            return s;
        }

        // ---- the fretting hand ------------------------------------------------------------------------------------------

        /// <summary>
        /// How long the morph to the next chord takes: one beat (1 / beatsPerSecond) clamped to [MinMorphSeconds,
        /// MaxMorphSeconds]; DefaultMorphSeconds without a tempo.
        /// </summary>
        public static float MorphSeconds(float beatsPerSecond)
        {
            if (!(beatsPerSecond > 0f) || float.IsInfinity(beatsPerSecond)) return DefaultMorphSeconds;
            float beat = 1f / beatsPerSecond;
            return beat < MinMorphSeconds ? MinMorphSeconds : beat > MaxMorphSeconds ? MaxMorphSeconds : beat;
        }

        /// <summary>The morph's length in beats: MorphSeconds * beatsPerSecond, at most 1 (it must arrive by the change).</summary>
        public static double MorphBeats(float beatsPerSecond)
        {
            if (!(beatsPerSecond > 0f) || float.IsInfinity(beatsPerSecond)) return 1.0;
            double beats = MorphSeconds(beatsPerSecond) * (double)beatsPerSecond;
            return beats > 1.0 ? 1.0 : beats;
        }

        /// <summary>
        /// Linear progress (0..1) of the morph toward the next chord: 0 until BeatsToChange reaches 1 (exactly one beat
        /// early), then (1 - beatsToChange) / MorphBeats, clamped. 0 for an infinite or NaN beatsToChange (no next chord).
        /// </summary>
        public static float MorphProgress(double beatsToChange, float beatsPerSecond)
        {
            if (double.IsNaN(beatsToChange) || double.IsInfinity(beatsToChange) || beatsToChange >= 1.0) return 0f;
            double elapsed = 1.0 - beatsToChange;
            double u = elapsed / MorphBeats(beatsPerSecond);
            return u >= 1.0 ? 1f : u <= 0.0 ? 0f : (float)u;
        }

        /// <summary>
        /// The fretting hand during a song: the chord shown (from), the chord being morphed to (to) and the linear progress
        /// (u, ease it for display). A null chord (count-in) holds the first chord (nextChord) or, without one, rest.
        /// </summary>
        public static void FrettingTarget(string chord, string nextChord, double beatsToChange, float beatsPerSecond,
                                          out string from, out string to, out float u)
        {
            u = 0f;
            if (chord == null)
            {
                from = to = nextChord ?? Rest;
                return;
            }
            from = chord;
            if (nextChord == null || string.Equals(nextChord, chord, StringComparison.Ordinal))
            {
                to = chord;
                return;
            }
            to = nextChord;
            u = MorphProgress(beatsToChange, beatsPerSecond);
        }

        /// <summary>
        /// The fretting ghost's fade: fadeAfterSuccess (clamped 0..1; non-finite = 0.4) while the current chord is confirmed
        /// and the hand is not yet morphing; full (1) otherwise, so it is bright again as soon as it starts forming the next
        /// chord.
        /// </summary>
        public static float FrettingFade(bool chordConfirmed, float morphProgress, float fadeAfterSuccess)
        {
            if (!chordConfirmed || morphProgress > 0f) return 1f;
            if (float.IsNaN(fadeAfterSuccess) || float.IsInfinity(fadeAfterSuccess)) return 0.4f;
            return fadeAfterSuccess < 0f ? 0f : fadeAfterSuccess > 1f ? 1f : fadeAfterSuccess;
        }
    }
}
