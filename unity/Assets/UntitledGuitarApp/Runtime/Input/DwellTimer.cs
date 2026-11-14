// DwellTimer: the pure state machine behind gaze-dwell activation (slices SL0 / SL8, hands-free input).
//
// No UnityEngine, no clock: on every Tick the caller passes the angle between the gaze and the target and the time that
// has passed. That makes it usable for a button, for the four neck-alignment dwells and for a "hold your head still"
// dwell (feed it the angle between the gaze now and the gaze when the dwell began).
//
//   Idle --(angle <= EnterAngleDeg)--> Dwelling --(held for DwellSeconds)--> Fired
//   Fired --(next Tick)--> Cooldown --(CooldownSeconds elapsed)--> Idle
//   Dwelling --(angle > ExitAngleDeg)--> Idle        (progress is lost)
//
// Hysteresis: a dwell starts only when the gaze is inside EnterAngleDeg, but it survives until the gaze leaves
// ExitAngleDeg (>= EnterAngleDeg). Head tremor at the rim of a target therefore cannot restart or break a dwell.
//
// Namespace note: this is UntitledGuitarApp.Runtime.Gaze, not ...Runtime.Input. A namespace called Input would make the
// simple name `Input` in every other UntitledGuitarApp.Runtime.* file mean that namespace instead of UnityEngine.Input
// (verified with cs-check: error CS0234 on Input.GetKeyDown).
namespace UntitledGuitarApp.Runtime.Gaze
{
    public enum DwellState
    {
        /// <summary>Waiting for the gaze to come inside EnterAngleDeg.</summary>
        Idle = 0,
        /// <summary>The gaze is on the target; Progress runs from 0 to 1.</summary>
        Dwelling = 1,
        /// <summary>The dwell just completed. Lasts exactly until the next Tick.</summary>
        Fired = 2,
        /// <summary>Locked out after firing; a new dwell cannot start until CooldownSeconds have passed.</summary>
        Cooldown = 3,
    }

    public sealed class DwellTimer
    {
        public const float DefaultDwellSeconds = 1.2f;
        public const float DefaultCooldownSeconds = 1.0f;
        public const float DefaultEnterAngleDeg = 3.0f;
        public const float DefaultExitAngleDeg = 3.75f;

        // A dwell completes when the held time is within this much of DwellSeconds. It absorbs float rounding: 72 frames
        // of 1/60 s add up to 1.1999996 in float32, which would make the default 1.2 s dwell fire one frame late.
        // 0.1 ms is far below one frame, so it never fires a frame early.
        const float FireToleranceSeconds = 1e-4f;

        DwellState state;
        float heldSeconds;      // time spent Dwelling
        float cooldownLeft;     // time left in Cooldown

        public DwellTimer()
        {
            DwellSeconds = DefaultDwellSeconds;
            CooldownSeconds = DefaultCooldownSeconds;
            EnterAngleDeg = DefaultEnterAngleDeg;
            ExitAngleDeg = DefaultExitAngleDeg;
        }

        public DwellTimer(float enterAngleDeg, float exitAngleDeg, float dwellSeconds, float cooldownSeconds)
        {
            EnterAngleDeg = enterAngleDeg;
            ExitAngleDeg = exitAngleDeg;
            DwellSeconds = dwellSeconds;
            CooldownSeconds = cooldownSeconds;
        }

        /// <summary>How long the gaze must stay on the target. Values &lt;= 0 fire on the first tick after entering.</summary>
        public float DwellSeconds { get; set; }

        /// <summary>Lockout after a fire. Values &lt;= 0 mean no lockout.</summary>
        public float CooldownSeconds { get; set; }

        /// <summary>A dwell may start when the angle is at or below this.</summary>
        public float EnterAngleDeg { get; set; }

        /// <summary>A running dwell survives while the angle is at or below this. Values below EnterAngleDeg count as EnterAngleDeg.</summary>
        public float ExitAngleDeg { get; set; }

        public DwellState State { get { return state; } }

        /// <summary>True in Idle and Dwelling, i.e. the timer accepts gaze. False in Fired and Cooldown.</summary>
        public bool IsArmed { get { return state == DwellState.Idle || state == DwellState.Dwelling; } }

        /// <summary>0 outside a dwell, 0..1 while Dwelling, 1 in Fired.</summary>
        public float Progress
        {
            get
            {
                if (state == DwellState.Fired) return 1f;
                if (state != DwellState.Dwelling || DwellSeconds <= 0f) return 0f;
                float p = heldSeconds / DwellSeconds;
                return p < 0f ? 0f : (p > 1f ? 1f : p);
            }
        }

        /// <summary>Seconds of lockout left; 0 outside Cooldown.</summary>
        public float CooldownRemaining { get { return state == DwellState.Cooldown ? cooldownLeft : 0f; } }

        float EffectiveExitAngleDeg { get { return ExitAngleDeg < EnterAngleDeg ? EnterAngleDeg : ExitAngleDeg; } }

        /// <summary>
        /// Advances the machine. <paramref name="angleDeg"/> is the angle between the gaze and the target (pass
        /// float.PositiveInfinity when the gaze is not on this target). <paramref name="dtSeconds"/> is the time since the
        /// previous Tick; negative and NaN values count as 0. Returns true on the one tick where the dwell completes.
        /// </summary>
        public bool Tick(float angleDeg, float dtSeconds)
        {
            if (!(dtSeconds > 0f)) dtSeconds = 0f;   // also catches NaN

            if (state == DwellState.Fired)
            {
                // Fired lasts exactly one tick, so observers could see it; now the lockout starts and uses this tick's time.
                heldSeconds = 0f;
                if (CooldownSeconds > 0f)
                {
                    state = DwellState.Cooldown;
                    cooldownLeft = CooldownSeconds;
                }
                else
                {
                    state = DwellState.Idle;
                    cooldownLeft = 0f;
                }
            }

            if (state == DwellState.Cooldown)
            {
                cooldownLeft -= dtSeconds;
                if (cooldownLeft > 0f) return false;
                cooldownLeft = 0f;
                state = DwellState.Idle;   // a new dwell may begin on this very tick
            }

            if (state == DwellState.Idle)
            {
                if (angleDeg <= EnterAngleDeg)   // false for NaN
                {
                    state = DwellState.Dwelling;
                    heldSeconds = 0f;            // the tick that enters does not count as held time
                }
                return false;
            }

            // state == Dwelling
            if (!(angleDeg <= EffectiveExitAngleDeg))   // also true for NaN
            {
                state = DwellState.Idle;
                heldSeconds = 0f;
                return false;
            }

            heldSeconds += dtSeconds;
            if (heldSeconds + FireToleranceSeconds >= DwellSeconds)
            {
                heldSeconds = DwellSeconds > 0f ? DwellSeconds : 0f;
                state = DwellState.Fired;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Fires at once (the Space bar). Works from Idle and Dwelling and returns true; does nothing and returns false
        /// in Fired and Cooldown, so a button can never fire twice for one press.
        /// </summary>
        public bool ForceFire()
        {
            if (!IsArmed) return false;
            heldSeconds = DwellSeconds > 0f ? DwellSeconds : 0f;
            state = DwellState.Fired;
            return true;
        }

        /// <summary>Back to Idle with nothing held and no lockout.</summary>
        public void Reset()
        {
            state = DwellState.Idle;
            heldSeconds = 0f;
            cooldownLeft = 0f;
        }
    }
}
