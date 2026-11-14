using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// The motion rules of the Learn HUD in one place. Pure maths, no engine state, unit-tested.
    ///
    /// Rules (docs/MASTER_PLAN.md section 17): things ease, nothing flashes faster than 3 Hz, a verdict is on screen
    /// within 350 ms. So: an element appears already half bright on its first frame and is fully bright
    /// <see cref="EaseInSeconds"/> later; it fades out over <see cref="FadeOutSeconds"/>; and one element changes what it
    /// shows (appear, swap, disappear) at most once per <see cref="MinChangeSeconds"/>, which is fewer than 3 changes a
    /// second however often the caller asks.
    /// </summary>
    public static class LearnTiming
    {
        /// <summary>Nothing may flash faster than this (comfort and photosensitivity rule of the project).</summary>
        public const float MaxFlashHz = 3f;

        /// <summary>Minimum time between two visible changes of one element. 0.34 s is just under 3 changes per second.</summary>
        public const float MinChangeSeconds = 0.34f;

        /// <summary>Time from first appearing to full brightness and full size.</summary>
        public const float EaseInSeconds = 0.12f;

        /// <summary>Time to fade from full brightness to nothing.</summary>
        public const float FadeOutSeconds = 0.35f;

        /// <summary>An element is already this far along its ease-in on the first frame, so it is readable at once.</summary>
        public const float StartProgress = 0.5f;

        /// <summary>Elements grow from this fraction of their size to full size while easing in.</summary>
        public const float PopStartScale = 0.85f;

        /// <summary>A repeated event (a second clean strum) is acknowledged by one gentle swell this long...</summary>
        public const float PulseSeconds = 0.25f;

        /// <summary>...and this big (fraction of the size). It is a size change, not a brightness flash.</summary>
        public const float PulseAmount = 0.12f;

        /// <summary>A strum verdict must be on screen this soon after Show() (docs/PRD.md success metrics).</summary>
        public const float VerdictBudgetSeconds = 0.35f;

        /// <summary>Reading time: a glance to find the text...</summary>
        public const float ReadBaseSeconds = 1.0f;

        /// <summary>...plus this long per word.</summary>
        public const float ReadPerWordSeconds = 0.3f;

        public const float MaxReadSeconds = 4f;

        /// <summary>
        /// How long a message of <paramref name="words"/> words must stay to be read at a glance:
        /// <see cref="ReadBaseSeconds"/> + <see cref="ReadPerWordSeconds"/> per word, capped at <see cref="MaxReadSeconds"/>.
        /// A 7-word tip gets 3.1 s; "Clean!" gets 1.3 s.
        /// </summary>
        public static float ReadSeconds(int words)
        {
            if (words < 0) words = 0;
            return Mathf.Min(MaxReadSeconds, ReadBaseSeconds + ReadPerWordSeconds * words);
        }

        public static float Clamp01(float t)
        {
            if (float.IsNaN(t)) return 0f;
            return t < 0f ? 0f : (t > 1f ? 1f : t);
        }

        /// <summary>Smooth start and smooth stop: 3t^2 - 2t^3 on 0..1 (clamped).</summary>
        public static float SmoothStep01(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>Fast start, gentle landing, no overshoot: 1 - (1 - t)^3 on 0..1 (clamped).</summary>
        public static float EaseOutCubic(float t)
        {
            float u = 1f - Clamp01(t);
            return 1f - u * u * u;
        }

        /// <summary>
        /// One step of frame-rate independent exponential smoothing: moves <paramref name="current"/> towards
        /// <paramref name="target"/> with time constant <paramref name="tauSeconds"/>. Never overshoots; a non-positive time
        /// constant jumps to the target; no time passed changes nothing.
        /// </summary>
        public static float ExpSmooth(float current, float target, float dt, float tauSeconds)
        {
            if (!(dt > 0f)) return current;
            if (!(tauSeconds > 0f)) return target;
            return current + (target - current) * (1f - Mathf.Exp(-dt / tauSeconds));
        }

        /// <summary>Size multiplier while easing in, <paramref name="age"/> seconds after appearing.</summary>
        public static float PopScale(float age)
        {
            return Mathf.Lerp(PopStartScale, 1f, EaseOutCubic(age / EaseInSeconds));
        }

        /// <summary>Size multiplier of the acknowledgement swell, <paramref name="age"/> seconds after it started (1 outside it).</summary>
        public static float PulseScale(float age)
        {
            if (!(age >= 0f) || age >= PulseSeconds) return 1f;
            return 1f + PulseAmount * Mathf.Sin(Mathf.PI * age / PulseSeconds);
        }
    }

    /// <summary>
    /// Rate limiter for visible changes: however often a change is requested, <see cref="Tick"/> lets one through at most
    /// every <see cref="LearnTiming.MinChangeSeconds"/>. The first request after a quiet spell passes on the same frame,
    /// so normal use adds no delay; only a burst is held back (the newest request wins, because the owner applies its
    /// latest wanted state when the gate opens). A default-constructed gate is open. Pure; no engine state.
    /// </summary>
    public struct ChangeGate
    {
        float _cooldown;
        bool _pending;

        /// <summary>True while a requested change is waiting for the gate to open.</summary>
        public bool Pending { get { return _pending; } }

        /// <summary>Seconds until the next change may pass (0 = open).</summary>
        public float Cooldown { get { return _cooldown; } }

        /// <summary>Asks for a change. It passes on the next <see cref="Tick"/> that finds the gate open.</summary>
        public void Request()
        {
            _pending = true;
        }

        /// <summary>Drops a waiting request.</summary>
        public void Cancel()
        {
            _pending = false;
        }

        /// <summary>
        /// Records that the element just changed through another path: drops a waiting request and keeps the gate closed
        /// for <see cref="LearnTiming.MinChangeSeconds"/>, so a change through this gate cannot follow straight after it.
        /// </summary>
        public void MarkChanged()
        {
            _pending = false;
            _cooldown = LearnTiming.MinChangeSeconds;
        }

        /// <summary>
        /// Advances time by <paramref name="dt"/> seconds. Returns true exactly when a pending change passes now; the
        /// gate then closes for <see cref="LearnTiming.MinChangeSeconds"/>.
        /// </summary>
        public bool Tick(float dt)
        {
            if (dt > 0f) _cooldown = _cooldown > dt ? _cooldown - dt : 0f;
            if (!_pending || _cooldown > 0f) return false;
            _pending = false;
            _cooldown = LearnTiming.MinChangeSeconds;
            return true;
        }
    }

    /// <summary>
    /// Brightness and size of one element over time: eases in (readable from the first frame), stays, fades out.
    /// Call <see cref="Show"/> / <see cref="Hide"/> and then <see cref="Tick"/> every frame; read <see cref="Opacity"/> and
    /// <see cref="Scale"/> only when Tick returns true (nothing changes otherwise, so a settled element costs nothing).
    /// A default-constructed fader is hidden. Pure; no engine state.
    /// </summary>
    public struct HudFader
    {
        float _progress;     // 0 = hidden, 1 = fully shown; linear in time, eased on the way out
        float _popAge;       // seconds since Show()
        float _pulseAge;     // seconds since Pulse(); >= PulseSeconds = no pulse running
        bool _target;
        bool _dirty;

        /// <summary>True after <see cref="Show"/>, false after <see cref="Hide"/>.</summary>
        public bool Target { get { return _target; } }

        /// <summary>True while anything is on screen (including while fading out).</summary>
        public bool IsVisible { get { return _progress > 0f; } }

        /// <summary>True when no animation is running.</summary>
        public bool IsSettled
        {
            get
            {
                if (!_target) return _progress <= 0f;
                return _progress >= 1f && _popAge >= LearnTiming.EaseInSeconds && _pulseAge >= LearnTiming.PulseSeconds;
            }
        }

        /// <summary>Brightness, 0..1.</summary>
        public float Opacity { get { return LearnTiming.SmoothStep01(_progress); } }

        /// <summary>Size multiplier (about 0.85..1.12; exactly 1 when settled or fading out).</summary>
        public float Scale
        {
            get
            {
                if (!_target) return 1f;
                return LearnTiming.PopScale(_popAge) * LearnTiming.PulseScale(_pulseAge);
            }
        }

        /// <summary>Starts (or continues) appearing. Already visible on the next frame.</summary>
        public void Show()
        {
            if (_target) return;
            _target = true;
            _popAge = 0f;
            _pulseAge = LearnTiming.PulseSeconds;
            if (_progress < LearnTiming.StartProgress) _progress = LearnTiming.StartProgress;
            _dirty = true;
        }

        /// <summary>One gentle swell in size to acknowledge a repeated event. Does nothing while hidden.</summary>
        public void Pulse()
        {
            if (!_target) return;
            _pulseAge = 0f;
            _dirty = true;
        }

        /// <summary>Starts fading out.</summary>
        public void Hide()
        {
            if (!_target) return;
            _target = false;
            _dirty = true;
        }

        /// <summary>Hides at once, with no fade (used when another message takes the same place).</summary>
        public void HideNow()
        {
            _target = false;
            _progress = 0f;
            _dirty = true;
        }

        /// <summary>Shows at once at full brightness and size (used by previews and when a held-back change finally passes).</summary>
        public void ShowNow()
        {
            _target = true;
            _progress = 1f;
            _popAge = LearnTiming.EaseInSeconds;
            _pulseAge = LearnTiming.PulseSeconds;
            _dirty = true;
        }

        /// <summary>Advances by <paramref name="dt"/> seconds. Returns true when <see cref="Opacity"/> or <see cref="Scale"/> changed.</summary>
        public bool Tick(float dt)
        {
            bool changed = _dirty;
            _dirty = false;
            if (!(dt > 0f)) return changed;

            if (_target)
            {
                if (_progress < 1f)
                {
                    _progress = Mathf.Min(1f, _progress + dt / LearnTiming.EaseInSeconds);
                    changed = true;
                }
                if (_popAge < LearnTiming.EaseInSeconds)
                {
                    _popAge = Mathf.Min(LearnTiming.EaseInSeconds, _popAge + dt);
                    changed = true;
                }
                if (_pulseAge < LearnTiming.PulseSeconds)
                {
                    _pulseAge = Mathf.Min(LearnTiming.PulseSeconds, _pulseAge + dt);
                    changed = true;
                }
            }
            else if (_progress > 0f)
            {
                _progress = Mathf.Max(0f, _progress - dt / LearnTiming.FadeOutSeconds);
                changed = true;
            }
            return changed;
        }
    }
}
