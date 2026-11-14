// LyricStrip: the Ultimate-Guitar style part of the neck card. Optional in the product (docs/PRD.md requirement 6: "Lyrics
// themselves are optional: playing matters more than singing"), so everything needed to play is elsewhere on the card.
//
//  * A line WITH words: the words on one row, each chord name drawn above the character where the chord changes (the song
//    file's "at" index), the chord being played marked with a short bar and brighter, a small caret gliding under the words,
//    and the next line smaller and dimmer underneath.
//  * A line WITHOUT words (a chords-only song: every lyric is empty): the chord sequence spaced by its beat counts, with one
//    tick per beat under the names, so "Am for two beats, G for two, C for four" can be read and counted; the next line's
//    chords smaller underneath. The first beat of each bar is a larger tick.
//
// The font is proportional, so "above character 18" needs real glyph widths: HudTextMetrics reads the advances from the
// built-in font (the same Font.GetCharacterInfo call HudText itself uses). The mapping from widths to positions is a pure
// function (ChordOffsets), tested with made-up widths. Words are never invented or stored here: they come from the song
// file through SetLines, and only the public-domain song has any (docs/SONGS.md).
//
// SOURCES (offline documentation of Unity 2021.3.3f1, Unity.app/Contents/Documentation/en/ScriptReference):
//  * Font.GetCharacterInfo.html: "Get rendering info for a specific character" (declaration with size and style).
//  * CharacterInfo-advance.html: "The horizontal distance, rounded to the nearest integer, from the origin of this character
//    to the origin of the next character."
//  * Font.RequestCharactersInTexture.html: "Request characters to be added to the font texture (dynamic fonts only)."
//
// UNVERIFIED (nobody has run this in Unity yet): that TextMesh lays glyphs out by exactly these advances (no kerning). If
// it does not, chord names sit a little left or right of their syllable; nothing else depends on it.
using System;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Runtime.Hud.Play
{
    /// <summary>
    /// Widths of bold HUD text in ems, read from the built-in font. Falls back to a typical width per character when the
    /// font is not available (EditMode tests, a failed font load), so layout never divides by zero.
    /// </summary>
    public static class HudTextMetrics
    {
        /// <summary>Raster size the advances are read at (pixels per em).</summary>
        public const int ReferenceSize = 64;

        /// <summary>Typical advance of a bold Arial character, ems: used when the font cannot be asked.</summary>
        public const float FallbackAdvanceEm = 0.58f;

        /// <summary>Arial's cap height in ems (1467/2048): used when the font cannot be asked.</summary>
        public const float FallbackCapPerEm = 0.716f;

        static float s_CapPerEm;

        /// <summary>The font's cap height in ems, measured from the letter H; <see cref="FallbackCapPerEm"/> if that fails.</summary>
        public static float CapPerEm
        {
            get
            {
                if (s_CapPerEm > 0f) return s_CapPerEm;
                Font font = HudText.BuiltinFont;
                if (font != null)
                {
                    font.RequestCharactersInTexture("H", ReferenceSize, FontStyle.Bold);
                    if (font.GetCharacterInfo('H', out CharacterInfo info, ReferenceSize, FontStyle.Bold))
                    {
                        float ratio = Mathf.Abs(info.maxY - info.minY) / (float)ReferenceSize;
                        if (ratio > 0.5f && ratio < 0.9f)
                        {
                            s_CapPerEm = ratio;
                            return ratio;
                        }
                    }
                }
                return FallbackCapPerEm;
            }
        }

        /// <summary>
        /// Writes the running width of <paramref name="text"/> into <paramref name="prefixEm"/>: entry i is the width in
        /// ems of the first i characters (entry 0 is 0). Text longer than the array is cut. Returns the number of
        /// characters measured. No allocation.
        /// </summary>
        public static int Prefix(string text, float[] prefixEm)
        {
            if (prefixEm == null || prefixEm.Length == 0) return 0;
            prefixEm[0] = 0f;
            int length = text != null ? Mathf.Min(text.Length, prefixEm.Length - 1) : 0;
            if (length == 0) return 0;

            Font font = HudText.BuiltinFont;
            if (font != null) font.RequestCharactersInTexture(text, ReferenceSize, FontStyle.Bold);
            float x = 0f;
            for (int i = 0; i < length; i++)
            {
                x += AdvanceEm(font, text[i]);
                prefixEm[i + 1] = x;
            }
            return length;
        }

        /// <summary>Width of <paramref name="text"/> in ems. No allocation.</summary>
        public static float WidthEm(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            Font font = HudText.BuiltinFont;
            if (font != null) font.RequestCharactersInTexture(text, ReferenceSize, FontStyle.Bold);
            float x = 0f;
            for (int i = 0; i < text.Length; i++) x += AdvanceEm(font, text[i]);
            return x;
        }

        static float AdvanceEm(Font font, char c)
        {
            if (font != null && font.GetCharacterInfo(c, out CharacterInfo info, ReferenceSize, FontStyle.Bold) && info.advance > 0)
                return info.advance / (float)ReferenceSize;
            return FallbackAdvanceEm;
        }
    }

    /// <summary>
    /// The lyric strip. Give it the current and the next line with <see cref="SetLines"/> whenever they change (or every
    /// frame: unchanged lines cost a few comparisons) and the position inside the current line with
    /// <see cref="SetProgress"/> every frame. The object's origin is the left end of the current line, at the middle of
    /// its letters; chords sit above it and the next line below it. Main thread only. Faces +Z.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LyricStrip : MonoBehaviour
    {
        /// <summary>Most chords shown per line; extra chords are ignored.</summary>
        public const int MaxChords = 8;

        /// <summary>Most beat ticks shown for a chords-only line.</summary>
        public const int MaxTicks = 16;

        /// <summary>Characters of a lyric that are measured; a longer line is still shown, its tail just gets no chord positions.</summary>
        public const int MaxLyricChars = 96;

        /// <summary>Smallest gap between two chord names, as a fraction of their cap height.</summary>
        public const float ChordGapOfCap = 0.5f;

        const float TickOfDot = 0.8f;

        static readonly Color ActiveColor = Color.white;
        static readonly Color InfoColor = new Color(0.702f, 0.898f, 0.988f, 1f);          // #B3E5FC
        static readonly Color PlayedColor = new Color(0.702f, 0.898f, 0.988f, 0.5f);
        static readonly Color ComingColor = new Color(0.702f, 0.898f, 0.988f, 0.9f);
        static readonly Color LyricColor = new Color(0.702f, 0.898f, 0.988f, 0.92f);
        static readonly Color NextColor = new Color(0.702f, 0.898f, 0.988f, 0.55f);

        sealed class LineState
        {
            public string Lyric = string.Empty;
            public int Count;
            public bool HasLyric;
            public float TotalBeats;
            public readonly string[] Names = new string[MaxChords];
            public readonly float[] Beats = new float[MaxChords];
            public readonly int[] At = new int[MaxChords];

            public bool IsEmpty { get { return Count == 0 && !HasLyric; } }

            public bool Matches(LyricLine line)
            {
                string lyric = line != null && line.Lyric != null ? line.Lyric : string.Empty;
                int count = line != null ? Mathf.Min(line.ChordCount, MaxChords) : 0;
                if (count != Count || !string.Equals(lyric, Lyric, StringComparison.Ordinal)) return false;
                for (int i = 0; i < count; i++)
                {
                    LyricChord c = line.Chords[i];
                    if (c.Beats != Beats[i] || c.At != At[i]) return false;
                    if (!string.Equals(c.Chord ?? string.Empty, Names[i], StringComparison.Ordinal)) return false;
                }
                return true;
            }

            public void CopyFrom(LyricLine line)
            {
                Lyric = line != null && line.Lyric != null ? line.Lyric : string.Empty;
                HasLyric = line != null && line.HasLyric;
                Count = line != null ? Mathf.Min(line.ChordCount, MaxChords) : 0;
                TotalBeats = 0f;
                for (int i = 0; i < MaxChords; i++)
                {
                    if (i < Count)
                    {
                        LyricChord c = line.Chords[i];
                        Names[i] = c.Chord ?? string.Empty;
                        Beats[i] = c.Beats > 0f ? c.Beats : 0f;
                        At[i] = c.At;
                        TotalBeats += Beats[i];
                    }
                    else
                    {
                        Names[i] = string.Empty;
                        Beats[i] = 0f;
                        At[i] = -1;
                    }
                }
            }
        }

        readonly LineState _cur = new LineState();
        readonly LineState _next = new LineState();

        readonly HudText[] _chords = new HudText[MaxChords];
        readonly HudText[] _nextChords = new HudText[MaxChords];
        readonly HudShape[] _ticks = new HudShape[MaxTicks];
        readonly float[] _tickSize = new float[MaxTicks];
        readonly float[] _tickLevel = new float[MaxTicks];
        HudText _lyric;
        HudText _nextLyric;
        HudShape _marker;
        HudShape _caret;

        readonly float[] _prefixEm = new float[MaxLyricChars + 1];
        readonly float[] _prefixM = new float[MaxLyricChars + 1];
        readonly float[] _chordX = new float[MaxChords];
        readonly float[] _chordW = new float[MaxChords];
        readonly float[] _nextX = new float[MaxChords];
        readonly float[] _nextW = new float[MaxChords];

        float _distanceM = PlayHudLayout.DesignDistanceM;
        bool _large;
        bool _sequence;
        int _measured;
        int _tickCount;
        int _beatsPerBar = 4;
        float _pitchM;
        float _unitM;
        float _beat;
        int _shownChord = -2;
        float _markerY;
        float _caretY;
        float _tickInsetM;
        bool _showNext = true;

        /// <summary>Beats in a bar (3 or 4): the first tick of each bar of a chords-only line is drawn larger. Default 4.</summary>
        public int BeatsPerBar
        {
            get { return _beatsPerBar; }
            set
            {
                int v = Mathf.Max(1, value);
                if (v == _beatsPerBar) return;
                _beatsPerBar = v;
                for (int i = 0; i < MaxTicks; i++) _tickSize[i] = -1f;
                ApplyProgress();
            }
        }

        /// <summary>
        /// Whether the next line is shown under the current one (default true). The neck card turns it off: on the floating
        /// neck only one line fits above the chord name (zone map, Sat 12:25).
        /// </summary>
        public bool ShowNextLine
        {
            get { return _showNext; }
            set
            {
                if (value == _showNext) return;
                _showNext = value;
                Relayout();
            }
        }

        /// <summary>True when the current line is shown as a chord sequence (it has no words).</summary>
        public bool ShowingSequence { get { return _sequence; } }

        /// <summary>Index of the chord of the current line that is being played, from the last <see cref="SetProgress"/>.</summary>
        public int CurrentChordIndex { get { return _shownChord; } }

        /// <summary>Creates the strip under <paramref name="parent"/> (may be null) for a viewer <paramref name="viewingDistanceM"/> metres away.</summary>
        public static LyricStrip Create(Transform parent, float viewingDistanceM)
        {
            var go = new GameObject("LyricStrip");
            if (parent != null) go.transform.SetParent(parent, false);
            LyricStrip strip = go.AddComponent<LyricStrip>();
            strip.Init(viewingDistanceM);
            return strip;
        }

        void Init(float viewingDistanceM)
        {
            _distanceM = viewingDistanceM > 0f ? viewingDistanceM : PlayHudLayout.DesignDistanceM;
            _large = FlagStore.Current.largeText;

            _lyric = MakeText(transform, "Lyric", LyricColor);
            _nextLyric = MakeText(transform, "NextLyric", NextColor);
            for (int i = 0; i < MaxChords; i++) _chords[i] = MakeText(transform, "Chord" + i, ComingColor);
            _caret = HudShape.Create(transform, "Caret", HudShape.Caret);
            _caret.Color = ActiveColor;
            _caret.Visible = false;
            Relayout();
        }

        static HudText MakeText(Transform parent, string objectName, Color color)
        {
            HudText text = HudText.Create(parent, objectName);
            text.Color = color;
            text.Anchor = TextAnchor.MiddleLeft;
            text.Alignment = TextAlignment.Left;
            text.Visible = false;
            return text;
        }

        // ------------------------------------------------------------------ content

        /// <summary>
        /// The line being played and the one after it (either may be null). A line whose lyric is empty is shown as a chord
        /// sequence with beat ticks. The strip copies what it shows, so the same objects may be refilled and passed again;
        /// nothing is rebuilt unless the content changed.
        /// </summary>
        public void SetLines(LyricLine current, LyricLine next)
        {
            if (_cur.Matches(current) && _next.Matches(next)) return;
            _cur.CopyFrom(current);
            _next.CopyFrom(next);
            Relayout();
        }

        /// <summary>
        /// Where the playhead is in the current line, in beats from the start of the line (0 = its first chord begins).
        /// Marks the chord being played and moves the caret (or the beat ticks). Call it every frame; while wait mode holds,
        /// keep passing the held value. Nothing is allocated; a text is only touched when the chord changes.
        /// </summary>
        public void SetProgress(float beatInLine)
        {
            if (float.IsNaN(beatInLine) || float.IsInfinity(beatInLine)) beatInLine = 0f;
            _beat = beatInLine;

            bool large = FlagStore.Current.largeText;
            if (large != _large)
            {
                _large = large;
                Relayout();
                return;
            }
            ApplyProgress();
        }

        // ------------------------------------------------------------------ layout (only when the lines or the text size change)

        void Relayout()
        {
            float d = _distanceM;
            float world = WorldDistance();
            float scale = HudText.TextScale(_large);
            float capPerEm = HudTextMetrics.CapPerEm;
            _unitM = PlayHudLayout.Size(scale, d);
            _sequence = !_cur.HasLyric;
            _shownChord = -2;

            _lyric.Visible = false;
            _caret.Visible = false;
            if (_marker != null) _marker.Visible = false;
            for (int i = 0; i < MaxChords; i++) _chords[i].Visible = false;
            for (int i = 0; i < MaxTicks; i++)
            {
                if (_ticks[i] != null) _ticks[i].Visible = false;
                _tickSize[i] = -1f;
                _tickLevel[i] = -1f;
            }
            _tickCount = 0;

            float nextDrop;
            if (_cur.IsEmpty)
            {
                nextDrop = PlayHudLayout.LyricNextDropDeg;
            }
            else if (!_sequence)
            {
                nextDrop = PlayHudLayout.LyricNextDropDeg;
                LayoutLyricLine(d, world, scale, capPerEm);
            }
            else
            {
                nextDrop = PlayHudLayout.SequenceNextDropDeg;
                LayoutSequenceLine(d, world, scale, capPerEm);
            }

            if (_showNext)
            {
                LayoutNextLine(d, world, scale, capPerEm, -PlayHudLayout.Offset(nextDrop * scale, d));
            }
            else
            {
                _nextLyric.Visible = false;
                for (int i = 0; i < MaxChords; i++)
                {
                    if (_nextChords[i] != null) _nextChords[i].Visible = false;
                }
            }
            ApplyProgress();
        }

        void LayoutLyricLine(float d, float world, float scale, float capPerEm)
        {
            _measured = HudTextMetrics.Prefix(_cur.Lyric, _prefixEm);
            float widthEm = _prefixEm[_measured];
            float lineDeg = PlayHudLayout.FitCapDegrees(widthEm, PlayHudLayout.LyricLineCapDeg * scale,
                PlayHudLayout.ColumnMaxWidthDeg, HudText.MinTextDegrees, capPerEm);
            float emM = PlayHudLayout.Size(lineDeg, d) / capPerEm;
            for (int i = 0; i <= _measured; i++) _prefixM[i] = _prefixEm[i] * emM;

            _lyric.SetText(_cur.Lyric);
            _lyric.transform.localPosition = Vector3.zero;
            _lyric.SetAngularHeight(lineDeg, world);
            _lyric.Visible = true;

            float chordDeg = PlayHudLayout.TextDeg(PlayHudLayout.LyricChordCapDeg, _large);
            float chordCapM = PlayHudLayout.Size(chordDeg, d);
            float chordEmM = chordCapM / capPerEm;
            for (int i = 0; i < _cur.Count; i++) _chordW[i] = HudTextMetrics.WidthEm(_cur.Names[i]) * chordEmM;
            ChordOffsets(_cur.At, _chordW, _cur.Count, _prefixM, _measured, chordCapM * ChordGapOfCap, _chordX);

            float chordY = PlayHudLayout.Offset(PlayHudLayout.LyricChordRiseDeg * scale, d);
            for (int i = 0; i < _cur.Count; i++)
            {
                HudText chord = _chords[i];
                chord.SetText(_cur.Names[i]);
                chord.transform.localPosition = new Vector3(_chordX[i], chordY, 0f);
                chord.SetAngularHeight(chordDeg, world);
                chord.Visible = true;
            }

            _markerY = PlayHudLayout.Offset(PlayHudLayout.LyricMarkerRiseDeg * scale, d);
            if (_marker == null)
            {
                _marker = HudShape.CreateBar(transform, "Marker",
                    PlayHudLayout.Size(PlayHudLayout.LyricMarkerWidthDeg * scale, d), PlayHudLayout.Size(PlayHudLayout.LyricMarkerHeightDeg * scale, d));
                _marker.Color = ActiveColor;
            }
            _marker.Visible = _cur.Count > 0;

            _caretY = -PlayHudLayout.Offset(PlayHudLayout.LyricCaretDropDeg * scale, d);
            float caretM = PlayHudLayout.Size(PlayHudLayout.LyricCaretDeg * scale, d);
            _caret.SetSize(caretM * 1.1f, caretM);
            _caret.Visible = _cur.Count > 0;
        }

        void LayoutSequenceLine(float d, float world, float scale, float capPerEm)
        {
            float pitchDeg = SequencePitch(_cur.TotalBeats, PlayHudLayout.ColumnMaxWidthDeg,
                PlayHudLayout.SequenceMaxBeatPitchDeg * scale, PlayHudLayout.SequenceMinBeatPitchDeg);
            _pitchM = PlayHudLayout.Size(pitchDeg, d);

            float nameDeg = PlayHudLayout.TextDeg(PlayHudLayout.SequenceNameCapDeg, _large);
            float nameCapM = PlayHudLayout.Size(nameDeg, d);
            float nameEmM = nameCapM / capPerEm;
            for (int i = 0; i < _cur.Count; i++) _chordW[i] = HudTextMetrics.WidthEm(_cur.Names[i]) * nameEmM;
            SequenceOffsets(_cur.Beats, _chordW, _cur.Count, _pitchM, nameCapM * ChordGapOfCap, _chordX);

            float nameY = PlayHudLayout.Offset(PlayHudLayout.SequenceNameRiseDeg * scale, d);
            for (int i = 0; i < _cur.Count; i++)
            {
                HudText chord = _chords[i];
                chord.SetText(_cur.Names[i]);
                chord.transform.localPosition = new Vector3(_chordX[i], nameY, 0f);
                chord.SetAngularHeight(nameDeg, world);
                chord.Visible = true;
            }

            // One tick per beat, under the first letter of the chord that starts on it.
            _tickCount = Mathf.Min(MaxTicks, Mathf.CeilToInt(_cur.TotalBeats - 1e-4f));
            _tickInsetM = nameCapM * 0.36f;
            float tickY = -PlayHudLayout.Offset(PlayHudLayout.SequenceTickDropDeg * scale, d);
            for (int i = 0; i < _tickCount; i++)
            {
                if (_ticks[i] == null) _ticks[i] = HudShape.Create(transform, "Beat" + i, HudShape.Disc);
                _ticks[i].SetPosition(i * _pitchM + _tickInsetM, tickY);
                _ticks[i].Visible = true;
            }
        }

        void LayoutNextLine(float d, float world, float scale, float capPerEm, float y)
        {
            bool lyric = _next.HasLyric;
            _nextLyric.Visible = lyric;
            if (lyric)
            {
                float widthEm = HudTextMetrics.WidthEm(_next.Lyric);
                float nextDeg = PlayHudLayout.FitCapDegrees(widthEm, PlayHudLayout.LyricNextCapDeg * scale,
                    PlayHudLayout.ColumnMaxWidthDeg, HudText.MinTextDegrees, capPerEm);
                _nextLyric.SetText(_next.Lyric);
                _nextLyric.transform.localPosition = new Vector3(0f, y, 0f);
                _nextLyric.SetAngularHeight(nextDeg, world);
            }

            bool names = !lyric && _next.Count > 0;
            if (names)
            {
                float pitchDeg = SequencePitch(_next.TotalBeats, PlayHudLayout.ColumnMaxWidthDeg,
                    PlayHudLayout.SequenceMaxBeatPitchDeg * scale, PlayHudLayout.SequenceMinBeatPitchDeg);
                float pitchM = PlayHudLayout.Size(pitchDeg, d);
                float nameDeg = PlayHudLayout.TextDeg(PlayHudLayout.SequenceNextCapDeg, _large);
                float nameCapM = PlayHudLayout.Size(nameDeg, d);
                float nameEmM = nameCapM / capPerEm;
                for (int i = 0; i < _next.Count; i++) _nextW[i] = HudTextMetrics.WidthEm(_next.Names[i]) * nameEmM;
                SequenceOffsets(_next.Beats, _nextW, _next.Count, pitchM, nameCapM * ChordGapOfCap, _nextX);
                for (int i = 0; i < _next.Count; i++)
                {
                    if (_nextChords[i] == null) _nextChords[i] = MakeText(transform, "NextChord" + i, NextColor);
                    HudText chord = _nextChords[i];
                    chord.SetText(_next.Names[i]);
                    chord.transform.localPosition = new Vector3(_nextX[i], y, 0f);
                    chord.SetAngularHeight(nameDeg, world);
                    chord.Visible = true;
                }
            }
            for (int i = names ? _next.Count : 0; i < MaxChords; i++)
            {
                if (_nextChords[i] != null) _nextChords[i].Visible = false;
            }
        }

        // ------------------------------------------------------------------ progress (every frame; no text is touched unless the chord changes)

        void ApplyProgress()
        {
            if (_cur.Count == 0) return;

            int index = ChordIndexAtBeat(_cur.Beats, _cur.Count, _beat);
            if (index != _shownChord)
            {
                _shownChord = index;
                for (int i = 0; i < _cur.Count; i++)
                    _chords[i].Color = i == index ? ActiveColor : (i < index ? PlayedColor : ComingColor);
                if (!_sequence && _marker != null)
                {
                    float half = PlayHudLayout.Size(PlayHudLayout.LyricMarkerWidthDeg * HudText.TextScale(_large), _distanceM) * 0.5f;
                    _marker.SetPosition(_chordX[index] + half, _markerY);
                }
            }

            if (!_sequence)
            {
                _caret.SetPosition(CaretX(_beat, _cur.Beats, _cur.At, _cur.Count, _prefixM, _measured), _caretY);
                return;
            }

            float clamped = Mathf.Clamp(_beat, 0f, Mathf.Max(0f, _cur.TotalBeats));
            int current = Mathf.Min((int)clamped, Mathf.Max(0, _tickCount - 1));
            if (_beat >= _cur.TotalBeats && _cur.TotalBeats > 0f) current = _tickCount;   // the line is over: no current tick
            float phase = current < _tickCount ? Mathf.Clamp01(clamped - current) : 0f;
            for (int i = 0; i < _tickCount; i++)
            {
                float size = BeatDots.DotSizeDeg(i, current, phase, false, false) * TickOfDot;
                if (i % _beatsPerBar == 0) size *= BeatDots.DownbeatGain;
                float level = BeatDots.DotLevel(i, current, phase);
                if (Mathf.Abs(size - _tickSize[i]) > 0.002f)
                {
                    _tickSize[i] = size;
                    _ticks[i].SetSize(size * _unitM);
                }
                if (Mathf.Abs(level - _tickLevel[i]) > 0.004f)
                {
                    _tickLevel[i] = level;
                    _ticks[i].SetColor(Color.Lerp(InfoColor, ActiveColor, BeatDots.Currentness(i, current, phase)), level);
                }
            }
        }

        float WorldDistance()
        {
            float s = transform.lossyScale.y;
            return _distanceM * (s > 1e-6f ? s : 1f);
        }

        // ------------------------------------------------------------------ pure helpers (unit-tested)

        /// <summary>The chords-only fallback: true when a line has no words, so its chord sequence with beat ticks is shown instead. A null line counts as chords-only.</summary>
        public static bool IsChordsOnly(LyricLine line)
        {
            return line == null || !line.HasLyric;
        }

        /// <summary>True when every line of a song is chords-only (docs/SONGS.md: license "chords-only", every lyric empty).</summary>
        public static bool IsChordsOnly(LyricLine[] lines, int count)
        {
            if (lines == null) return true;
            int n = Mathf.Min(count, lines.Length);
            for (int i = 0; i < n; i++)
            {
                if (!IsChordsOnly(lines[i])) return false;
            }
            return true;
        }

        /// <summary>The character column a chord is drawn above: its "at" index, kept inside the lyric (a missing or negative index is the start).</summary>
        public static int ChordColumn(int at, int lyricLength)
        {
            if (at < 0 || lyricLength <= 0) return 0;
            return at > lyricLength ? lyricLength : at;
        }

        /// <summary>
        /// Chord-above-lyric mapping. For each chord, the x where its name starts: the left edge of the character at its
        /// "at" index (<paramref name="prefix"/>[i] is the width of the first i characters), moved right when needed so
        /// names never overlap (at least <paramref name="minGap"/> between them) and never run backwards. Writes
        /// <paramref name="count"/> values into <paramref name="outX"/>; no allocation.
        /// </summary>
        public static void ChordOffsets(int[] ats, float[] widths, int count, float[] prefix, int lyricLength, float minGap, float[] outX)
        {
            if (outX == null || ats == null) return;
            int usable = prefix != null ? Mathf.Min(lyricLength, prefix.Length - 1) : 0;
            float earliest = 0f;
            for (int i = 0; i < count && i < outX.Length && i < ats.Length; i++)
            {
                float x = prefix != null && usable >= 0 ? prefix[ChordColumn(ats[i], usable)] : 0f;
                if (i > 0 && x < earliest) x = earliest;
                outX[i] = x;
                float w = widths != null && i < widths.Length ? Mathf.Max(0f, widths[i]) : 0f;
                earliest = x + w + Mathf.Max(0f, minGap);
            }
        }

        /// <summary>Index of the chord sounding at <paramref name="beat"/> beats into the line: 0 before the line starts, the last chord once it is over, -1 for a line with no chords.</summary>
        public static int ChordIndexAtBeat(float[] beats, int count, float beat)
        {
            if (beats == null || count <= 0) return -1;
            count = Mathf.Min(count, beats.Length);
            float end = 0f;
            for (int i = 0; i < count; i++)
            {
                end += Mathf.Max(0f, beats[i]);
                if (beat < end) return i;
            }
            return count - 1;
        }

        /// <summary>Beat (from the start of the line) on which chord <paramref name="index"/> begins.</summary>
        public static float ChordStartBeat(float[] beats, int index)
        {
            if (beats == null) return 0f;
            float start = 0f;
            for (int i = 0; i < index && i < beats.Length; i++) start += Mathf.Max(0f, beats[i]);
            return start;
        }

        /// <summary>
        /// X of the caret under the words: it is exactly under a chord's character when that chord begins and moves evenly
        /// to the next chord's character (or to the end of the line) while the chord lasts. The song file times the chords,
        /// not the syllables, so between chords this is an even glide, not a karaoke-accurate one.
        /// </summary>
        public static float CaretX(float beat, float[] beats, int[] ats, int count, float[] prefix, int lyricLength)
        {
            if (beats == null || ats == null || prefix == null || count <= 0) return 0f;
            int usable = Mathf.Min(lyricLength, prefix.Length - 1);
            if (usable < 0) return 0f;
            count = Mathf.Min(count, Mathf.Min(beats.Length, ats.Length));
            float start = 0f;
            for (int i = 0; i < count; i++)
            {
                float length = Mathf.Max(0f, beats[i]);
                float end = start + length;
                if (beat < end || i == count - 1)
                {
                    float x0 = prefix[ChordColumn(ats[i], usable)];
                    float x1 = i + 1 < count ? prefix[ChordColumn(ats[i + 1], usable)] : prefix[usable];
                    float t = length > 0f ? Mathf.Clamp01((beat - start) / length) : 1f;
                    return Mathf.Lerp(x0, x1, t);
                }
                start = end;
            }
            return 0f;
        }

        /// <summary>Width of one beat of a chords-only line: the whole line fits in <paramref name="maxWidth"/>, between the two limits.</summary>
        public static float SequencePitch(float totalBeats, float maxWidth, float maxPitch, float minPitch)
        {
            if (!(totalBeats > 0f)) return maxPitch;
            return Mathf.Max(minPitch, Mathf.Min(maxPitch, maxWidth / totalBeats));
        }

        /// <summary>
        /// Chords-only layout. For each chord, the x where its name starts: its starting beat times
        /// <paramref name="pitch"/> (so the spacing shows the beat counts), moved right when needed so names never overlap.
        /// Writes <paramref name="count"/> values into <paramref name="outX"/>; no allocation.
        /// </summary>
        public static void SequenceOffsets(float[] beats, float[] widths, int count, float pitch, float minGap, float[] outX)
        {
            if (outX == null || beats == null) return;
            float start = 0f;
            float earliest = 0f;
            for (int i = 0; i < count && i < outX.Length && i < beats.Length; i++)
            {
                float x = start * pitch;
                if (i > 0 && x < earliest) x = earliest;
                outX[i] = x;
                float w = widths != null && i < widths.Length ? Mathf.Max(0f, widths[i]) : 0f;
                earliest = x + w + Mathf.Max(0f, minGap);
                start += Mathf.Max(0f, beats[i]);
            }
        }
    }
}
