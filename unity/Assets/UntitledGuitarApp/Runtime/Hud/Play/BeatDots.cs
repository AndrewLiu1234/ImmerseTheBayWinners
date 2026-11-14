using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Runtime.Hud.Play
{
    /// <summary>
    /// The countdown to the next chord change: one dot per beat that is left, in fixed places, with the current beat
    /// highlighted. The highlight walks to the right; when it runs off the end, the chord changes. Beats already played
    /// stay as small dim dots, so the row never jumps.
    ///
    /// Meaning is carried by SIZE and position as well as brightness (never by colour alone): the current beat is the big
    /// dot, beats to come are medium, beats played are small; the first beat of each bar is a little larger than its
    /// neighbours. The picture is a pure function of the three numbers given to <see cref="Set"/>, so it cannot drift
    /// from the music and it freezes cleanly when the playhead holds (wait mode).
    ///
    /// The current dot swells on the beat and relaxes (eased, peak exactly on the beat). That pulse is capped at
    /// <see cref="MaxPulseHz"/> (2 Hz): at faster tempos only every second beat, or only the first beat of the bar, pulses
    /// (<see cref="PulseEveryBeats"/>). Give the tempo with <see cref="BeatsPerSecond"/>; if it is left at 0 it is
    /// estimated from how fast the beat position moves.
    ///
    /// The object's origin is the centre of the first dot; the row runs to the right. Main thread only. Faces +Z.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BeatDots : MonoBehaviour
    {
        /// <summary>Most dots shown. A longer wait keeps the first dot current until this many beats are left.</summary>
        public const int MaxDots = 8;

        /// <summary>The beat pulse never repeats faster than this (docs/MASTER_PLAN.md section 17).</summary>
        public const float MaxPulseHz = 2f;

        /// <summary>How much bigger the current dot gets at the peak of its pulse.</summary>
        public const float PulseGain = 0.35f;

        /// <summary>Fraction of the beat over which the pulse relaxes.</summary>
        public const float PulseDecay = 0.5f;

        /// <summary>The last part of each beat, in which the current dot hands over to the next one.</summary>
        public const float HandoverStart = 0.85f;

        /// <summary>The first beat of a bar is drawn this much larger.</summary>
        public const float DownbeatGain = 1.25f;

        const float PassedLevel = 0.30f;
        const float UpcomingLevel = 0.72f;
        const float CurrentLevel = 1f;

        static readonly Color DotColor = new Color(0.702f, 0.898f, 0.988f, 1f);       // #B3E5FC
        static readonly Color CurrentColor = Color.white;

        readonly HudShape[] _dots = new HudShape[MaxDots];
        readonly float[] _shownSize = new float[MaxDots];
        readonly float[] _shownLevel = new float[MaxDots];

        float _distanceM = PlayHudLayout.DesignDistanceM;
        bool _large;
        float _pitchM;
        float _unitM;          // metres per degree of dot size
        int _span;
        int _lastToChange = int.MinValue;
        int _shownCount = -1;
        bool _visible = true;

        // tempo estimate (only used while BeatsPerSecond is 0)
        float _lastBeatPosition = float.NaN;
        float _lastTime;
        float _estimatedBps;
        int _pulseEvery = 1;
        int _barCounter;
        int _lastBeatInBar = -1;

        /// <summary>The tempo in beats per second (bpm * tempo preset / 60). Optional; 0 = estimate it. Only used to cap the pulse rate.</summary>
        public float BeatsPerSecond { get; set; }

        /// <summary>How many beats the current countdown started with (the number of dots, before the <see cref="MaxDots"/> cap).</summary>
        public int Span { get { return _span; } }

        /// <summary>Width of the row for <paramref name="dots"/> dots, metres (local).</summary>
        public float WidthMeters(int dots)
        {
            return Mathf.Max(0, dots - 1) * _pitchM;
        }

        /// <summary>Creates the row under <paramref name="parent"/> (may be null) for a viewer <paramref name="viewingDistanceM"/> metres away.</summary>
        public static BeatDots Create(Transform parent, float viewingDistanceM)
        {
            var go = new GameObject("BeatDots");
            if (parent != null) go.transform.SetParent(parent, false);
            BeatDots dots = go.AddComponent<BeatDots>();
            dots.Init(viewingDistanceM);
            return dots;
        }

        void Init(float viewingDistanceM)
        {
            _distanceM = viewingDistanceM > 0f ? viewingDistanceM : PlayHudLayout.DesignDistanceM;
            for (int i = 0; i < MaxDots; i++)
            {
                _dots[i] = HudShape.Create(transform, "Dot" + i, HudShape.Disc);
                _dots[i].Visible = false;
                _shownSize[i] = -1f;
                _shownLevel[i] = -1f;
            }
            _large = FlagStore.Current.largeText;
            ApplySizes();
        }

        void ApplySizes()
        {
            float scale = HudText.TextScale(_large);
            _pitchM = PlayHudLayout.Size(PlayHudLayout.DotPitchDeg * scale, _distanceM);
            _unitM = PlayHudLayout.Size(scale, _distanceM);
            for (int i = 0; i < MaxDots; i++)
            {
                _dots[i].SetPosition(i * _pitchM, 0f);
                _shownSize[i] = -1f;
            }
        }

        /// <summary>Shows or hides the whole row (the card hides it while the wait cue is up).</summary>
        public bool Visible
        {
            get { return _visible; }
            set
            {
                if (_visible == value) return;
                _visible = value;
                _shownCount = -1;
                if (!value)
                {
                    for (int i = 0; i < MaxDots; i++) _dots[i].Visible = false;
                }
            }
        }

        /// <summary>
        /// Call every frame while playing. <paramref name="beatsPerBar"/>: 3 or 4. <paramref name="beatPosition"/>: where
        /// the playhead is inside the bar, in beats, 0 up to beatsPerBar (1.25 = a quarter of the way through beat 2).
        /// <paramref name="beatsToChange"/>: beats left until the next chord change, counting the current beat (1 = the
        /// change comes on the next beat; 0 or less = it is due now, e.g. while wait mode holds). Nothing is allocated.
        /// </summary>
        public void Set(int beatsPerBar, float beatPosition, int beatsToChange)
        {
            if (beatsPerBar < 1) beatsPerBar = 4;
            if (float.IsNaN(beatPosition) || float.IsInfinity(beatPosition)) beatPosition = 0f;

            bool large = FlagStore.Current.largeText;
            if (large != _large)
            {
                _large = large;
                ApplySizes();
            }

            _span = NextSpan(_span, _lastToChange, beatsToChange);
            _lastToChange = beatsToChange;

            float inBar = PlayHudLayout.PositiveMod(beatPosition, beatsPerBar);
            int beatInBar = Mathf.Min(beatsPerBar - 1, (int)inBar);
            float phase = inBar - beatInBar;

            UpdatePulseRate(beatsPerBar, inBar, beatInBar);

            int shown = Mathf.Min(_span, MaxDots);
            int current = CurrentIndex(shown, beatsToChange);
            bool pulseNow = BeatPulses(beatInBar, beatsPerBar, _pulseEvery, _barCounter);
            int nextInBar = (beatInBar + 1) % beatsPerBar;
            bool pulseNext = BeatPulses(nextInBar, beatsPerBar, _pulseEvery, nextInBar == 0 ? _barCounter + 1 : _barCounter);

            if (!_visible) return;

            if (shown != _shownCount)
            {
                _shownCount = shown;
                for (int i = 0; i < MaxDots; i++) _dots[i].Visible = i < shown;
            }

            for (int i = 0; i < shown; i++)
            {
                float currentness = Currentness(i, current, phase);
                float size = DotSizeDeg(i, current, phase, pulseNow, pulseNext);
                if (IsDownbeat(i, current, shown, beatsToChange, beatInBar, beatsPerBar)) size *= DownbeatGain;
                float level = DotLevel(i, current, phase);

                if (Mathf.Abs(size - _shownSize[i]) > 0.002f)
                {
                    _shownSize[i] = size;
                    _dots[i].SetSize(size * _unitM);
                }
                if (Mathf.Abs(level - _shownLevel[i]) > 0.004f)
                {
                    _shownLevel[i] = level;
                    _dots[i].SetColor(Color.Lerp(DotColor, CurrentColor, currentness), level);
                }
            }
        }

        void UpdatePulseRate(int beatsPerBar, float inBar, int beatInBar)
        {
            float bps = BeatsPerSecond;
            if (!(bps > 0f))
            {
                float now = Time.unscaledTime;
                if (!float.IsNaN(_lastBeatPosition))
                {
                    float dt = now - _lastTime;
                    float db = PlayHudLayout.PositiveMod(inBar - _lastBeatPosition, beatsPerBar);
                    if (dt > 0.001f && dt < 0.25f && db > 0f && db < 1f)
                    {
                        float instant = db / dt;
                        _estimatedBps = _estimatedBps > 0f ? Mathf.Lerp(_estimatedBps, instant, 0.1f) : instant;
                    }
                }
                _lastBeatPosition = inBar;
                _lastTime = now;
                bps = _estimatedBps;
            }

            if (beatInBar != _lastBeatInBar)
            {
                if (beatInBar == 0 && _lastBeatInBar >= 0) _barCounter++;
                // The divider only changes on the first beat of a bar, so the pulse pattern is steady within a bar.
                if (beatInBar == 0 || _lastBeatInBar < 0) _pulseEvery = PulseEveryBeats(bps, beatsPerBar, MaxPulseHz);
                _lastBeatInBar = beatInBar;
            }
        }

        // ------------------------------------------------------------------ pure helpers (unit-tested)

        /// <summary>
        /// The pulse-rate cap. Returns n such that pulsing on every n-th beat is at most <paramref name="maxHz"/> pulses a
        /// second: 1 (every beat) up to 120 bpm; above that the smallest divisor of the bar that is slow enough (so the
        /// pattern repeats every bar: beats 1 and 3 of 4/4, or only beat 1 of 3/4), then every 2nd, 3rd... bar.
        /// An unknown tempo (0 or less) gives 1.
        /// </summary>
        public static int PulseEveryBeats(float beatsPerSecond, int beatsPerBar, float maxHz)
        {
            if (!(beatsPerSecond > 0f) || !(maxHz > 0f) || float.IsInfinity(beatsPerSecond)) return 1;
            if (beatsPerBar < 1) beatsPerBar = 1;
            float limit = maxHz * 1.0001f;
            for (int n = 1; n <= beatsPerBar; n++)
            {
                if (beatsPerBar % n == 0 && beatsPerSecond / n <= limit) return n;
            }
            int bars = 2;
            while (bars < 64 && beatsPerSecond / (beatsPerBar * bars) > limit) bars++;
            return beatsPerBar * bars;
        }

        /// <summary>The pulses per second that <see cref="PulseEveryBeats"/> results in. Never above the cap.</summary>
        public static float PulseHz(float beatsPerSecond, int beatsPerBar, float maxHz)
        {
            if (!(beatsPerSecond > 0f)) return 0f;
            return beatsPerSecond / PulseEveryBeats(beatsPerSecond, beatsPerBar, maxHz);
        }

        /// <summary>Whether the beat with index <paramref name="beatInBar"/> (0 = the first) pulses, for a divider from <see cref="PulseEveryBeats"/>.</summary>
        public static bool BeatPulses(int beatInBar, int beatsPerBar, int pulseEvery, int barCounter)
        {
            if (pulseEvery <= 1) return true;
            if (beatsPerBar < 1) beatsPerBar = 1;
            if (pulseEvery <= beatsPerBar) return beatInBar % pulseEvery == 0;
            int everyBars = Mathf.Max(1, pulseEvery / beatsPerBar);
            return beatInBar == 0 && PlayHudLayout.PositiveMod(barCounter, everyBars) == 0;
        }

        /// <summary>
        /// The pulse shape over one beat: 1 exactly on the beat (phase 0), easing back to 0 by <see cref="PulseDecay"/> of
        /// the beat, 0 for the rest. The swell before the beat is done by the next dot growing (see <see cref="DotSizeDeg"/>).
        /// </summary>
        public static float PulseEnvelope(float phase01)
        {
            if (float.IsNaN(phase01) || phase01 < 0f || phase01 >= PulseDecay) return 0f;
            float t = 1f - phase01 / PulseDecay;
            return t * t;
        }

        /// <summary>How far through the hand-over to the next dot the beat is: 0 until <see cref="HandoverStart"/>, easing to 1 at the end of the beat.</summary>
        public static float Handover(float phase01)
        {
            return PlayHudLayout.Ease01((phase01 - HandoverStart) / (1f - HandoverStart));
        }

        /// <summary>
        /// The number of beats the countdown runs over. It restarts when <paramref name="beatsToChange"/> goes up (a new
        /// chord has begun) and otherwise keeps the largest value seen.
        /// </summary>
        public static int NextSpan(int span, int previousToChange, int beatsToChange)
        {
            int count = Mathf.Max(0, beatsToChange);
            if (previousToChange == int.MinValue || beatsToChange > previousToChange) return count;
            return Mathf.Max(span, count);
        }

        /// <summary>
        /// Index of the dot that is the current beat, for <paramref name="shownDots"/> dots: 0 while more beats are left
        /// than there are dots, the last dot when one beat is left, and <paramref name="shownDots"/> (past the end: no
        /// current dot) when the change is due.
        /// </summary>
        public static int CurrentIndex(int shownDots, int beatsToChange)
        {
            if (shownDots <= 0) return 0;
            return shownDots - Mathf.Clamp(beatsToChange, 0, shownDots);
        }

        /// <summary>1 for the current dot, easing to 0 as it hands over at the end of the beat; the next dot eases 0 to 1 at the same time; 0 for all others.</summary>
        public static float Currentness(int dot, int current, float phase01)
        {
            if (dot == current) return 1f - Handover(phase01);
            if (dot == current + 1) return Handover(phase01);
            return 0f;
        }

        /// <summary>
        /// Diameter of a dot in degrees (before the largeText and downbeat factors). Played dots are small, coming dots
        /// medium, the current dot large; at the end of a beat the current dot shrinks while the next one grows, reaching
        /// the pulse peak exactly on the beat if that beat pulses.
        /// </summary>
        public static float DotSizeDeg(int dot, int current, float phase01, bool currentPulses, bool nextPulses)
        {
            if (dot < current) return PlayHudLayout.DotPassedDeg;
            float h = Handover(phase01);
            if (dot == current)
            {
                float big = PlayHudLayout.DotCurrentDeg * (1f + (currentPulses ? PulseGain * PulseEnvelope(phase01) : 0f));
                return Mathf.Lerp(big, PlayHudLayout.DotPassedDeg, h);
            }
            if (dot == current + 1)
            {
                float peak = PlayHudLayout.DotCurrentDeg * (1f + (nextPulses ? PulseGain : 0f));
                return Mathf.Lerp(PlayHudLayout.DotUpcomingDeg, peak, h);
            }
            return PlayHudLayout.DotUpcomingDeg;
        }

        /// <summary>Brightness of a dot, 0..1: played dim, coming medium, current full; eased across the hand-over.</summary>
        public static float DotLevel(int dot, int current, float phase01)
        {
            if (dot < current) return PassedLevel;
            float h = Handover(phase01);
            if (dot == current) return Mathf.Lerp(CurrentLevel, PassedLevel, h);
            if (dot == current + 1) return Mathf.Lerp(UpcomingLevel, CurrentLevel, h);
            return UpcomingLevel;
        }

        /// <summary>
        /// Whether dot <paramref name="dot"/> stands for the first beat of a bar. The dot that is <paramref name="current"/>
        /// is the beat being played (<paramref name="beatInBar"/>); a later dot is that many beats ahead, counted back from
        /// the change.
        /// </summary>
        public static bool IsDownbeat(int dot, int current, int shownDots, int beatsToChange, int beatInBar, int beatsPerBar)
        {
            if (beatsPerBar < 1) return false;
            int ahead = dot == current ? 0 : dot - shownDots + Mathf.Max(0, beatsToChange);
            return PlayHudLayout.PositiveMod(beatInBar + ahead, beatsPerBar) == 0;
        }
    }
}
