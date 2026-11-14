using System.Globalization;
using System.Text;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Runtime.Hud
{
    /// <summary>
    /// The always-on microphone meter (docs/MASTER_PLAN.md section 17, item 4): a tiny segmented level bar plus the sample
    /// rate, so you can tell "deaf" from "wrong" at a glance.
    ///
    /// The bar is made of bold letters "I" coloured with TextMesh rich text (lit segments, a dim scale, a white peak-hold
    /// segment), so it needs no shader, material or mesh: everything is text, bright on black, nothing dark (additive-safe).
    /// All marked-up bar strings are built once at creation, so updating the bar allocates nothing. The text is rebuilt only when
    /// the lit segment count, the peak segment, the clip state or the sample rate actually changes. Nothing flashes: clipping
    /// is held steady for <see cref="ClipHoldSeconds"/>, and it is shown by the word CLIP as well as by colour.
    ///
    /// Feed it with <see cref="SetLevel"/> and <see cref="SetSampleRate"/> from the main thread. If no level arrives for
    /// <see cref="StaleSeconds"/> the label says NO DATA (the capture is not delivering, as opposed to a quiet room).
    /// With the debugOverlay flag on, a third line shows the RMS level in dBFS; the largeText flag enlarges everything.
    /// The object's origin is the top-left corner of the meter. Faces +Z: parent it to the head or eye rig, identity rotation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MicMeter : MonoBehaviour
    {
        /// <summary>Number of bar segments.</summary>
        public const int Segments = 16;

        /// <summary>Level (dBFS) at which the bar is empty.</summary>
        public const float FloorDb = -60f;

        /// <summary>Level (dBFS) at which the bar is full. 0 dBFS is a full-scale sample (amplitude 1.0).</summary>
        public const float CeilDb = 0f;

        /// <summary>Linear amplitude at or above which a peak counts as clipping (about -0.1 dBFS).</summary>
        public const float ClipThreshold = 0.989f;

        /// <summary>Anything quieter than this linear amplitude is treated as digital silence (-120 dBFS).</summary>
        public const float SilenceLinear = 1e-6f;

        public const float AttackSeconds = 0.03f;
        public const float ReleaseSeconds = 0.30f;
        public const float PeakHoldSeconds = 1.0f;
        public const float PeakFallPerSecond = 0.8f;
        public const float ClipHoldSeconds = 1.5f;

        /// <summary>Cap height of the meter's text, degrees (the project minimum), before the largeText multiplier.</summary>
        public const float TextDegrees = HudText.MinTextDegrees;

        const float LineStepInCaps = 2.3f;
        const char SegmentGlyph = 'I';

        // Bright colours for an additive display. Zones are decoration; the bar LENGTH carries the meaning.
        const string LitGreen = "#69F0AE";
        const string LitAmber = "#FFD54F";
        const string LitHot = "#FF7043";
        const string PeakWhite = "#FFFFFF";
        const string DimScale = "#2E4F43";
        static readonly Color LabelColor = new Color(0.702f, 0.898f, 0.988f, 1f);   // #B3E5FC

        static string[] s_BarMarkup;
        static string[] s_DbText;

        HudText _bar;
        HudText _label;
        HudText _debug;

        float _distanceM = 1f;
        bool _large;
        bool _debugShown;

        // input (last values handed to SetLevel / SetSampleRate)
        float _rms;
        float _peakSinceLast;
        int _hz;
        float _sinceLevel;

        // smoothed display state
        float _barRms;
        float _barPeak;
        float _peakHold;
        float _clipHold;

        // what is currently shown, to rebuild only on change
        int _litShown = -1;
        int _peakShown = -1;
        bool _clipShown;
        bool _noDataShown;
        int _hzShown = int.MinValue;
        string _lastDbText;

        /// <summary>No level for this many seconds means "no data". 0 turns the watchdog off.</summary>
        public float StaleSeconds { get; set; } = 1f;

        /// <summary>Creates the meter under <paramref name="parent"/> (may be null) for a viewer <paramref name="viewingDistanceM"/> metres away.</summary>
        public static MicMeter Create(Transform parent, float viewingDistanceM)
        {
            var go = new GameObject("MicMeter");
            if (parent != null) go.transform.SetParent(parent, false);
            MicMeter meter = go.AddComponent<MicMeter>();
            meter.Init(viewingDistanceM);
            return meter;
        }

        void Init(float viewingDistanceM)
        {
            _distanceM = viewingDistanceM > 0f ? viewingDistanceM : 1f;
            Prewarm();

            _bar = HudText.Create(transform, "MicBar");
            _bar.RichText = true;
            _bar.Anchor = TextAnchor.UpperLeft;
            _bar.Alignment = TextAlignment.Left;

            _label = HudText.Create(transform, "MicLabel");
            _label.RichText = true;
            _label.Color = LabelColor;
            _label.Anchor = TextAnchor.UpperLeft;
            _label.Alignment = TextAlignment.Left;

            _debug = HudText.Create(transform, "MicDebug");
            _debug.Color = LabelColor;
            _debug.Anchor = TextAnchor.UpperLeft;
            _debug.Alignment = TextAlignment.Left;
            _debug.Visible = false;

            FlagSet flags = FlagStore.Current;
            _large = flags.largeText;
            ApplySizes();
        }

        /// <summary>
        /// Latest input level. <paramref name="rms"/> and <paramref name="peak"/> are linear amplitudes of float samples
        /// (1.0 = full scale, 0 dBFS). Call it from the main thread as often as you like; the bar keeps the highest
        /// <paramref name="peak"/> seen since the last frame so short transients are not lost.
        /// </summary>
        public void SetLevel(float rms, float peak)
        {
            _rms = Sanitize(rms);
            float p = Sanitize(peak);
            if (p > _peakSinceLast) _peakSinceLast = p;
            _sinceLevel = 0f;
        }

        /// <summary>Capture sample rate in Hz (for example 48000). 0 or negative shows "--" (no capture).</summary>
        public void SetSampleRate(int hz)
        {
            _hz = hz;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            FlagSet flags = FlagStore.Current;

            bool large = flags.largeText;
            if (large != _large)
            {
                _large = large;
                ApplySizes();
            }

            bool debug = flags.debugOverlay;
            if (debug != _debugShown)
            {
                _debugShown = debug;
                _debug.Visible = debug;
                _lastDbText = null;
            }

            _sinceLevel += dt;
            bool noData = StaleSeconds > 0f && _sinceLevel > StaleSeconds;
            float rms = noData ? 0f : _rms;
            float peakNow = noData ? 0f : _peakSinceLast;
            _peakSinceLast = 0f;

            _barRms = Smooth(_barRms, LevelToBar(rms), dt, AttackSeconds, ReleaseSeconds);

            float peakBar = LevelToBar(peakNow);
            if (IsClipping(peakNow)) _clipHold = ClipHoldSeconds;
            else if (_clipHold > 0f) _clipHold -= dt;

            if (peakBar >= _barPeak)
            {
                _barPeak = peakBar;
                _peakHold = PeakHoldSeconds;
            }
            else if (_peakHold > 0f)
            {
                _peakHold -= dt;
            }
            else
            {
                _barPeak = Mathf.Max(_barRms, _barPeak - PeakFallPerSecond * dt);
            }

            int lit = LitSegments(_barRms, Segments);
            int peakSeg = Mathf.Max(lit, LitSegments(_barPeak, Segments));
            if (lit != _litShown || peakSeg != _peakShown)
            {
                _litShown = lit;
                _peakShown = peakSeg;
                _bar.SetText(BarMarkup(lit, peakSeg));
            }

            bool clip = _clipHold > 0f;
            if (clip != _clipShown || noData != _noDataShown || _hz != _hzShown)
            {
                _clipShown = clip;
                _noDataShown = noData;
                _hzShown = _hz;
                _label.SetText(LabelText(_hz, clip, noData));
            }

            if (_debugShown)
            {
                string db = DbText(LinearToDb(rms));
                if (!ReferenceEquals(db, _lastDbText))
                {
                    _lastDbText = db;
                    _debug.SetText(db);
                }
            }
        }

        void ApplySizes()
        {
            float degrees = TextDegrees * HudText.TextScale(_large);
            _bar.SetAngularHeight(degrees, _distanceM);
            _label.SetAngularHeight(degrees, _distanceM);
            _debug.SetAngularHeight(degrees, _distanceM);
            float step = _bar.CapHeightMeters * LineStepInCaps;
            _label.transform.localPosition = new Vector3(0f, -step, 0f);
            _debug.transform.localPosition = new Vector3(0f, -2f * step, 0f);
        }

        static float Sanitize(float v)
        {
            if (float.IsNaN(v) || v < 0f) return 0f;
            return v;
        }

        // ------------------------------------------------------------------ pure helpers (unit-tested)

        /// <summary>
        /// Linear amplitude (1.0 = full scale) to dBFS: 20 * log10(x). Silence (0, NaN or below <see cref="SilenceLinear"/>)
        /// gives -120 rather than minus infinity.
        /// </summary>
        public static float LinearToDb(float linear)
        {
            if (float.IsNaN(linear) || linear <= SilenceLinear) return -120f;
            return 20f * Mathf.Log10(linear);
        }

        /// <summary>
        /// The dB-to-bar mapping: -60 dBFS (or lower) is an empty bar (0), 0 dBFS (or louder: clipping) is a full bar (1),
        /// linear in dB in between, so -30 dBFS is half. NaN gives 0.
        /// </summary>
        public static float DbToBar(float db)
        {
            if (float.IsNaN(db)) return 0f;
            return Mathf.Clamp01((db - FloorDb) / (CeilDb - FloorDb));
        }

        /// <summary>Linear amplitude straight to a 0..1 bar fraction: <see cref="DbToBar"/> of <see cref="LinearToDb"/>.</summary>
        public static float LevelToBar(float linear)
        {
            return DbToBar(LinearToDb(linear));
        }

        /// <summary>How many of <paramref name="segments"/> segments are lit for a bar fraction (rounded to nearest, clamped to 0..segments).</summary>
        public static int LitSegments(float bar01, int segments)
        {
            if (segments <= 0 || float.IsNaN(bar01)) return 0;
            int n = Mathf.FloorToInt(Mathf.Clamp01(bar01) * segments + 0.5f);
            return Mathf.Clamp(n, 0, segments);
        }

        /// <summary>True when a peak (linear amplitude) is at or above <see cref="ClipThreshold"/>.</summary>
        public static bool IsClipping(float peakLinear)
        {
            return !float.IsNaN(peakLinear) && peakLinear >= ClipThreshold;
        }

        /// <summary>"48 kHz", "44.1 kHz", "22.05 kHz"; "-- kHz" for 0 or negative. Culture-invariant (always a dot).</summary>
        public static string FormatSampleRate(int hz)
        {
            if (hz <= 0) return "-- kHz";
            double khz = hz / 1000.0;
            return khz.ToString("0.##", CultureInfo.InvariantCulture) + " kHz";
        }

        /// <summary>The label under the bar: "MIC 48 kHz", plus a CLIP or NO DATA word (rich text) when it applies. NO DATA wins over CLIP.</summary>
        public static string LabelText(int sampleRateHz, bool clipping, bool noData)
        {
            string rate = "MIC " + FormatSampleRate(sampleRateHz);
            if (noData) return rate + "  <color=" + LitAmber + ">NO DATA</color>";
            if (clipping) return rate + "  <color=" + LitHot + ">CLIP</color>";
            return rate;
        }

        /// <summary>
        /// One step of an attack/release smoother: rises with time constant <paramref name="attackSeconds"/>, falls with
        /// <paramref name="releaseSeconds"/>, never overshoots. A non-positive time constant jumps straight to the target.
        /// </summary>
        public static float Smooth(float current, float target, float dt, float attackSeconds, float releaseSeconds)
        {
            if (!(dt > 0f)) return current;
            float tau = target > current ? attackSeconds : releaseSeconds;
            if (tau <= 0f) return target;
            float alpha = 1f - Mathf.Exp(-dt / tau);
            return current + (target - current) * alpha;
        }

        /// <summary>
        /// The rich-text bar for <paramref name="lit"/> lit segments and a peak-hold segment at position
        /// <paramref name="peakSegments"/> (counted like lit; only drawn when it is above the lit ones). Always exactly
        /// <see cref="Segments"/> letters. Cached, so after <see cref="Prewarm"/> this never allocates.
        /// </summary>
        public static string BarMarkup(int lit, int peakSegments)
        {
            lit = Mathf.Clamp(lit, 0, Segments);
            peakSegments = Mathf.Clamp(Mathf.Max(peakSegments, lit), 0, Segments);
            if (s_BarMarkup == null) s_BarMarkup = new string[(Segments + 1) * (Segments + 1)];
            int index = lit * (Segments + 1) + peakSegments;
            string markup = s_BarMarkup[index];
            if (markup == null)
            {
                markup = BuildBarMarkup(lit, peakSegments);
                s_BarMarkup[index] = markup;
            }
            return markup;
        }

        /// <summary>Builds every bar string and every dB string once, so nothing is allocated while the meter runs.</summary>
        public static void Prewarm()
        {
            for (int lit = 0; lit <= Segments; lit++)
            {
                for (int peak = lit; peak <= Segments; peak++) BarMarkup(lit, peak);
            }
            DbText(0f);
        }

        /// <summary>The debug readout for an RMS level in dBFS, rounded to whole dB and clamped to -90..0, e.g. "-23 dBFS". Cached.</summary>
        public static string DbText(float db)
        {
            if (s_DbText == null)
            {
                s_DbText = new string[91];
                for (int i = 0; i < s_DbText.Length; i++)
                    s_DbText[i] = (-i).ToString(CultureInfo.InvariantCulture) + " dBFS";
            }
            int rounded = float.IsNaN(db) ? -90 : Mathf.Clamp(Mathf.RoundToInt(db), -90, 0);
            return s_DbText[-rounded];
        }

        static string BuildBarMarkup(int lit, int peakSegments)
        {
            var sb = new StringBuilder(Segments * 14);
            string open = null;
            for (int i = 0; i < Segments; i++)
            {
                string color;
                if (i < lit) color = ZoneColor(i);
                else if (i == peakSegments - 1) color = PeakWhite;
                else color = DimScale;

                if (!string.Equals(color, open, System.StringComparison.Ordinal))
                {
                    if (open != null) sb.Append("</color>");
                    sb.Append("<color=").Append(color).Append('>');
                    open = color;
                }
                sb.Append(SegmentGlyph);
            }
            if (open != null) sb.Append("</color>");
            return sb.ToString();
        }

        // Segment i covers up to (i+1)/Segments of the bar: green to -15 dBFS, amber to -6 dBFS, red-orange above.
        static string ZoneColor(int segmentIndex)
        {
            float top = (segmentIndex + 1f) / Segments;
            if (top <= 0.75f) return LitGreen;
            if (top <= 0.90f) return LitAmber;
            return LitHot;
        }
    }
}
