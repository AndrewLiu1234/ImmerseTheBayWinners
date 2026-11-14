using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>What the tuner is telling the player. Shown by needle position, by a word and by an icon; never by colour alone.</summary>
    public enum TunerState
    {
        /// <summary>No string chosen: the tuner is hidden.</summary>
        Idle = 0,
        /// <summary>A string is chosen but no clear note has been heard for it yet ("Pluck string 6").</summary>
        Listening = 1,
        /// <summary>Flat: the needle is left of centre ("Too low: tighten").</summary>
        TooLow = 2,
        /// <summary>Sharp: the needle is right of centre ("Too high: loosen").</summary>
        TooHigh = 3,
        /// <summary>Within the in-tune zone, and it stayed there ("In tune!").</summary>
        InTune = 4,
    }

    /// <summary>The tuner's pure maths: cents to needle angle, the confidence gate, the in-tune zone with hysteresis. Unit-tested.</summary>
    public static class TunerMath
    {
        /// <summary>The dial shows this many cents each side of the target.</summary>
        public const float RangeCents = 50f;

        /// <summary>Needle angle at the end of the dial, degrees from straight up.</summary>
        public const float MaxNeedleDegrees = 40f;

        /// <summary>The needle must come this close to count as in tune...</summary>
        public const float InTuneEnterCents = 5f;

        /// <summary>...and must drift this far out before it stops counting (hysteresis, so the verdict cannot chatter at the edge).</summary>
        public const float InTuneExitCents = 8f;

        /// <summary>The needle must stay inside the zone this long before "In tune" is declared (sweeping through does not count).</summary>
        public const float InTuneDwellSeconds = 0.3f;

        /// <summary>Readings less confident than this are ignored (docs/MASTER_PLAN.md section 19 uses confidence above 0.8).</summary>
        public const float DefaultMinConfidence = 0.8f;

        /// <summary>Time constant of the needle's easing, seconds.</summary>
        public const float NeedleSmoothSeconds = 0.12f;

        /// <summary>After the last clear reading the needle stays "live" this long, then dims to show the reading is old.</summary>
        public const float NeedleHoldSeconds = 1.0f;

        /// <summary>Half the width of the in-tune zone on the dial, in degrees of needle angle.</summary>
        public const float ZoneHalfDegrees = InTuneEnterCents / RangeCents * MaxNeedleDegrees;

        /// <summary>Cents limited to what the dial can show (+-<see cref="RangeCents"/>); NaN becomes 0.</summary>
        public static float ClampCents(float cents)
        {
            if (float.IsNaN(cents)) return 0f;
            return Mathf.Clamp(cents, -RangeCents, RangeCents);
        }

        /// <summary>
        /// Needle angle for a pitch error, in degrees clockwise from straight up as the viewer sees it: flat (negative
        /// cents) leans left, sharp leans right, 0 is straight up, +-50 cents and beyond rest on the end stops.
        /// </summary>
        public static float NeedleAngleDegrees(float cents)
        {
            return ClampCents(cents) / RangeCents * MaxNeedleDegrees;
        }

        /// <summary>Where the needle tip is for a pitch error: x to the viewer's right, y up, pivot at the origin.</summary>
        public static Vector2 NeedleTip(float cents, float radius)
        {
            float a = NeedleAngleDegrees(cents) * Mathf.Deg2Rad;
            return new Vector2(radius * Mathf.Sin(a), radius * Mathf.Cos(a));
        }

        /// <summary>
        /// The local rotation that leans a needle drawn straight up by <paramref name="needleDegrees"/> clockwise for a
        /// viewer looking along +Z (the way HudText faces): a rotation about Z by minus that angle.
        /// </summary>
        public static Quaternion NeedleRotation(float needleDegrees)
        {
            float half = -needleDegrees * 0.5f * Mathf.Deg2Rad;
            return new Quaternion(0f, 0f, Mathf.Sin(half), Mathf.Cos(half));
        }

        /// <summary>The confidence gate: a reading counts only if it is a finite number at or above the threshold.</summary>
        public static bool IsConfident(float cents, float confidence, float minConfidence)
        {
            if (float.IsNaN(cents) || float.IsInfinity(cents) || float.IsNaN(confidence)) return false;
            return confidence >= minConfidence;
        }

        /// <summary>
        /// The in-tune zone with hysteresis: once in tune it stays so until the error exceeds
        /// <see cref="InTuneExitCents"/>; when not in tune the error must be within <see cref="InTuneEnterCents"/>.
        /// </summary>
        public static bool InZone(bool wasInTune, float cents)
        {
            float a = Mathf.Abs(cents);
            return wasInTune ? a <= InTuneExitCents : a <= InTuneEnterCents;
        }
    }

    /// <summary>
    /// The tuner's state over time, with no engine objects: feed it readings, tick it, read the needle and the verdict.
    /// The needle eases (exponential smoothing), ignores unclear readings (confidence gate) and holds its place when the
    /// note dies away. The verdict latches, so "Too low: tighten" stays while the player turns the peg in silence, and it
    /// never claims "too low" or "too high" for a note that is inside the in-tune zone and merely still settling.
    /// </summary>
    public struct TunerModel
    {
        bool _hasTarget;
        bool _hasReading;
        float _targetCents;
        float _shownCents;
        float _sinceReading;
        float _inZoneSeconds;
        TunerState _state;

        /// <summary>True between SetTarget and Clear.</summary>
        public bool HasTarget { get { return _hasTarget; } }

        /// <summary>True once a clear reading has arrived for this target.</summary>
        public bool HasReading { get { return _hasReading; } }

        /// <summary>True while the last clear reading is recent (the needle is drawn bright); false = an old reading (dim).</summary>
        public bool NeedleLive { get { return _hasReading && _sinceReading <= TunerMath.NeedleHoldSeconds; } }

        /// <summary>The eased pitch error the needle shows, in cents (within +-50).</summary>
        public float ShownCents { get { return _shownCents; } }

        /// <summary>The needle angle to draw, degrees clockwise from straight up.</summary>
        public float NeedleDegrees { get { return TunerMath.NeedleAngleDegrees(_shownCents); } }

        public bool InTune { get { return _hasTarget && _state == TunerState.InTune; } }

        public TunerState State { get { return _hasTarget ? _state : TunerState.Idle; } }

        /// <summary>Starts listening for a new string: forgets the old needle position and verdict.</summary>
        public void SetTarget()
        {
            _hasTarget = true;
            _hasReading = false;
            _targetCents = 0f;
            _shownCents = 0f;
            _sinceReading = 0f;
            _inZoneSeconds = 0f;
            _state = TunerState.Listening;
        }

        public void Clear()
        {
            this = default(TunerModel);
        }

        /// <summary>
        /// A new pitch reading: <paramref name="cents"/> from the target (negative = flat), <paramref name="confidence"/>
        /// 0..1. Unclear readings change nothing. Returns true when the reading was used.
        /// </summary>
        public bool SetReading(float cents, float confidence, float minConfidence)
        {
            if (!_hasTarget || !TunerMath.IsConfident(cents, confidence, minConfidence)) return false;
            _targetCents = TunerMath.ClampCents(cents);
            if (!NeedleLive)
            {
                // First note, or the first after a silence: start where the note is instead of sweeping across the dial
                // (a sweep through the centre would look like "in tune" for a moment).
                _shownCents = _targetCents;
                _inZoneSeconds = 0f;
                UpdateState(0f);
            }
            _hasReading = true;
            _sinceReading = 0f;
            return true;
        }

        /// <summary>Advances time by <paramref name="dt"/> seconds: eases the needle and updates the verdict.</summary>
        public void Tick(float dt)
        {
            if (!_hasTarget || !_hasReading || !(dt > 0f)) return;

            bool live = NeedleLive;
            _sinceReading += dt;
            if (!live) return;   // an old reading: the needle and the verdict stay as they were

            _shownCents = LearnTiming.ExpSmooth(_shownCents, _targetCents, dt, TunerMath.NeedleSmoothSeconds);
            UpdateState(dt);
        }

        void UpdateState(float dt)
        {
            if (_state == TunerState.InTune)
            {
                if (TunerMath.InZone(true, _shownCents)) return;
                _inZoneSeconds = 0f;
                _state = _shownCents < 0f ? TunerState.TooLow : TunerState.TooHigh;
                return;
            }

            if (TunerMath.InZone(false, _shownCents))
            {
                // Inside the zone but not yet rested there: keep the previous words until the dwell is over.
                _inZoneSeconds += dt;
                if (_inZoneSeconds >= TunerMath.InTuneDwellSeconds) _state = TunerState.InTune;
            }
            else
            {
                _inZoneSeconds = 0f;
                _state = _shownCents < 0f ? TunerState.TooLow : TunerState.TooHigh;
            }
        }
    }
}
