// PlayAlongPresenter: pushes one PracticeFrame per frame (plus the song's lines and chord shapes) into the play HUD that
// already exists (Runtime/Hud/Play: NeckHudCard, ApproachRings, ResultsCard) and onto the floating neck's fingertip dots and
// X/O marks. It owns nothing but references and a few preallocated buffers: every visible thing belongs to the HUD and the
// neck, and is driven only through their public API.
//
// What goes where, per frame:
//   card.SetChords(now, next)  - in the count-in "now" is the first chord (the count-in takes the next-chord block's place)
//   card.SetWaiting(Waiting)   - the "strum G" wait cue
//   card.Beats.Set(bar, position in bar, whole beats to the change; to the end of the song for the last chord)
//   card.Pattern               - one bar of the strum pattern, cursor = Beat * SlotsPerBeat (slot 0 on every bar line)
//   card.Lyrics                - the current and next line, progress in beats into the current line
//   card.CountIn               - 4-3-2-1 over the bar before the first chord
//   neck.SetDots/SetStringMarks - the chord being played (the first chord during the count-in), only when it changes
//   rings.Schedule/Tick        - rings closing on the NEXT chord's dots, on the song clock (Beat / BeatsPerSecond), so they
//                                freeze, landed, while wait mode holds
//   results.Show               - once, when the run has ended (labelled when simulated)
//
// SONG DATA. Today it reads the TEMPORARY DevSong (Runtime/Practice/Dev). When Core lands, its adapter either fills a DevSong
// (it is plain data) or this class takes Core's song view instead; the HUD side does not change.
// Chord shapes come from the TEMPORARY DevNeckLayout.ChordShape until Core's ChordLibrary + FretboardGeometry land.
//
// Main thread only. Present allocates nothing (LyricLine objects are built once per song in SetSong).
using System;
using UnityEngine;
using UntitledGuitarApp.Runtime.Hud.Play;
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Neck.Dev;
using UntitledGuitarApp.Runtime.Practice.Dev;

namespace UntitledGuitarApp.Runtime.Practice
{
    /// <summary>
    /// Feeds the play HUD and the neck from a <see cref="PracticeFrame"/>. Create the HUD pieces with <see cref="Create"/> (or
    /// hand existing ones to <see cref="Bind"/>), give it the song with <see cref="SetSong"/>, then call <see cref="Present"/>
    /// (or <see cref="PresentClock"/>) every frame.
    /// </summary>
    public sealed class PlayAlongPresenter
    {
        /// <summary>Distance of the head-locked results card, metres (ResultsCard: "about a metre ahead").</summary>
        public const float ResultsDistanceM = 1f;

        /// <summary>The results card's last line when the score is simulated (7 words or fewer, ResultsCard.Show).</summary>
        public const string SimulatedLine = "Simulated score: no audio check yet";

        NeckHudCard _card;
        ApproachRings _rings;
        ResultsCard _results;
        FloatingNeck _neck;

        DevSong _song;
        LyricLine[] _lines = new LyricLine[0];
        int _shownLine = -1;
        string _shownShape;
        string _ringChord;
        int _ringCount;
        bool _cardShown;
        bool _resultsShown;

        readonly FingertipDot[] _dots = new FingertipDot[FloatingNeck.MaxDots];
        readonly StringMark[] _marks = new StringMark[NeckLayout.StringCount];
        readonly FingertipDot[] _ringDots = new FingertipDot[FloatingNeck.MaxDots];
        readonly StringMark[] _ringMarks = new StringMark[NeckLayout.StringCount];
        readonly Vector3[] _ringTargets = new Vector3[ApproachRings.MaxRings];

        /// <summary>The neck card (chords, beat dots, pattern, lyrics, count-in, wait cue).</summary>
        public NeckHudCard Card { get { return _card; } }

        /// <summary>The approach rings.</summary>
        public ApproachRings Rings { get { return _rings; } }

        /// <summary>The results card.</summary>
        public ResultsCard ResultsCard { get { return _results; } }

        /// <summary>The neck whose dots follow the chord.</summary>
        public FloatingNeck Neck { get { return _neck; } }

        /// <summary>The song being shown.</summary>
        public DevSong Song { get { return _song; } }

        /// <summary>True while the results card is up.</summary>
        public bool ShowingResults { get { return _resultsShown; } }

        /// <summary>The chord whose shape is on the neck now (null before the first frame).</summary>
        public string ShownShape { get { return _shownShape; } }

        /// <summary>
        /// Creates the play HUD: the card and the rings under <paramref name="hudParent"/> (identity scale; not the neck) and
        /// tied to <paramref name="neck"/>'s socket and <paramref name="head"/>, and the results card head-locked
        /// <see cref="ResultsDistanceM"/> ahead of the head. Everything starts hidden.
        /// </summary>
        public static PlayAlongPresenter Create(Transform hudParent, Transform head, FloatingNeck neck)
        {
            Transform socket = neck != null ? neck.Socket : null;
            NeckHudCard card = NeckHudCard.Create(hudParent);
            card.Attach(socket, head);
            ApproachRings rings = ApproachRings.Create(hudParent);
            rings.Attach(socket, head);
            ResultsCard results = ResultsCard.Create(head, ResultsDistanceM);
            results.transform.localPosition = new Vector3(0f, 0f, ResultsDistanceM);
            results.transform.localRotation = Quaternion.identity;
            results.Hide();

            var presenter = new PlayAlongPresenter();
            presenter.Bind(card, rings, results, neck);
            return presenter;
        }

        /// <summary>Uses existing HUD pieces (any may be null and is then skipped).</summary>
        public void Bind(NeckHudCard card, ApproachRings rings, ResultsCard results, FloatingNeck neck)
        {
            _card = card;
            _rings = rings;
            _results = results;
            _neck = neck;
            _cardShown = true;   // force the first HideCard to run
            HideCard();
        }

        /// <summary>The song to show (null clears). Builds its lyric lines once; hides the results of the last run.</summary>
        public void SetSong(DevSong song)
        {
            _song = song;
            _shownLine = -1;
            _shownShape = null;
            _ringChord = null;
            _ringCount = 0;
            if (_rings != null) _rings.Clear();
            HideResults();

            int n = song != null ? song.Lines.Length : 0;
            _lines = new LyricLine[n];
            for (int i = 0; i < n; i++) _lines[i] = ToLyricLine(song.Lines[i]);
            if (_card != null && song != null) _card.Lyrics.BeatsPerBar = Math.Max(1, song.BeatsPerBar);
        }

        /// <summary>The dev clock's frame, plus the results card once the run has ended. Call every frame.</summary>
        public void PresentClock(DevPlayAlongClock clock, float deltaSeconds)
        {
            if (clock == null) return;
            if (!ReferenceEquals(clock.Song, _song) && clock.Song != null) SetSong(clock.Song);
            Present(clock.Frame, deltaSeconds);
            if (clock.State == DevPlayState.Ended && !_resultsShown)
                ShowResults(clock.Results, _song != null ? _song.Title : null);
        }

        /// <summary>
        /// One frame of play-along. An inactive frame hides the card and the rings (the results card stays if it is up).
        /// No allocation.
        /// </summary>
        public void Present(PracticeFrame frame, float deltaSeconds)
        {
            if (!frame.Active || _song == null)
            {
                HideCard();
                return;
            }
            if (_resultsShown) HideResults();
            _cardShown = true;

            DevChordRun[] runs = _song.Runs;
            bool countIn = frame.Chord == null && frame.Beat < _song.StartBeat - DevTimeline.Epsilon;
            string shape = frame.Chord ?? frame.NextChord;
            int bpb = Math.Max(1, frame.BeatsPerBar);

            if (_card != null)
            {
                string next = countIn ? (runs.Length > 1 ? runs[1].Chord : null) : frame.NextChord;
                _card.SetChords(shape, next);
                _card.SetWaiting(frame.Waiting);

                double toChange = double.IsInfinity(frame.BeatsToChange) ? _song.EndBeat - frame.Beat : frame.BeatsToChange;
                BeatDots beats = _card.Beats;
                beats.BeatsPerSecond = frame.BeatsPerSecond;
                beats.Set(bpb, (float)DevTimeline.BeatInBar(frame.Beat, bpb), DevTimeline.WholeBeatsLeft(toChange));

                PatternStrip pattern = _card.Pattern;
                pattern.SlotsPerBeat = Math.Max(1, frame.SlotsPerBeat);
                pattern.SetPattern(frame.StrumSlots, false);
                pattern.SetBassString(DevNeckLayout.BassString(shape));
                pattern.SetCursor((float)DevTimeline.SlotPosition(frame.Beat, frame.SlotsPerBeat));

                PresentLyrics(frame.Beat);

                if (countIn) _card.CountIn.Show(DevTimeline.CountInBeatsLeft(frame.Beat, _song.StartBeat));
                else _card.CountIn.Hide();
            }

            if (!string.Equals(shape, _shownShape, StringComparison.Ordinal)) ApplyShape(shape);
            PresentRings(frame);
        }

        /// <summary>Shows the results card (and hides the card and rings). Simulated scores say so on the card.</summary>
        public void ShowResults(DevPlayResults results, string title)
        {
            HideCard();
            _resultsShown = true;
            if (_results == null) return;
            _results.Title = string.IsNullOrEmpty(title) ? ResultsCard.DefaultTitle : title;
            _results.Show(results.ChordsHit, results.ChordsTotal, results.MeanTimingMs, results.Simulated ? SimulatedLine : null);
        }

        /// <summary>Hides the results card.</summary>
        public void HideResults()
        {
            _resultsShown = false;
            if (_results != null) _results.Hide();
        }

        // ------------------------------------------------------------------ parts

        void PresentLyrics(double beat)
        {
            if (_lines.Length == 0) return;
            int line = DevTimeline.LineIndexAt(_song.Lines, beat);
            if (line != _shownLine)
            {
                _shownLine = line;
                _card.Lyrics.SetLines(_lines[line], line + 1 < _lines.Length ? _lines[line + 1] : null);
            }
            _card.Lyrics.SetProgress((float)Math.Max(0.0, beat - _song.Lines[line].StartBeat));
        }

        void ApplyShape(string chord)
        {
            _shownShape = chord;
            if (_neck == null || _neck.Layout == null) return;
            int n = DevNeckLayout.ChordShape(_neck.Layout, chord, _dots, _marks);
            if (n < 0)
            {
                // No dev shape for this chord: clear the dots rather than leave the last chord's.
                n = 0;
                for (int i = 0; i < _marks.Length; i++) _marks[i] = StringMark.None;
            }
            _neck.SetDots(_dots, n);
            _neck.SetStringMarks(_marks);
        }

        void PresentRings(PracticeFrame frame)
        {
            if (_rings == null) return;
            if (!(frame.BeatsPerSecond > 0f))
            {
                _rings.Clear();
                return;
            }
            _rings.ApproachSeconds = ApproachRings.ApproachForBeat(1f / frame.BeatsPerSecond);
            string target = frame.NextChord;
            if (target != null && !double.IsInfinity(frame.BeatsToChange))
            {
                if (!string.Equals(target, _ringChord, StringComparison.Ordinal)) FillRingTargets(target);
                double hit = (frame.Beat + frame.BeatsToChange) / frame.BeatsPerSecond;
                if (_ringCount > 0) _rings.Schedule(_ringTargets, _ringCount, hit);
            }
            _rings.Tick(frame.Beat / frame.BeatsPerSecond);
        }

        void FillRingTargets(string chord)
        {
            _ringChord = chord;
            _ringCount = 0;
            if (_neck == null || _neck.Layout == null) return;
            int n = DevNeckLayout.ChordShape(_neck.Layout, chord, _ringDots, _ringMarks);
            for (int i = 0; i < n && _ringCount < _ringTargets.Length; i++)
                _ringTargets[_ringCount++] = new Vector3(_ringDots[i].xMm, 0f, _ringDots[i].zMm);   // on the fretboard plane, mm
        }

        void HideCard()
        {
            if (!_cardShown) return;
            _cardShown = false;
            if (_card != null)
            {
                _card.SetChords(null, null);
                _card.SetWaiting(false);
                _card.Beats.Visible = false;
                _card.Pattern.SetPattern(null, false);
                _card.Lyrics.SetLines(null, null);
                _card.CountIn.Hide();
                _shownLine = -1;
            }
            if (_rings != null) _rings.Clear();
            _ringChord = null;
        }

        /// <summary>A song line as the lyric strip wants it (built once per song; "at" only on lines with words).</summary>
        public static LyricLine ToLyricLine(DevSongLine line)
        {
            if (line == null) return new LyricLine();
            var chords = new LyricChord[line.Chords.Length];
            bool words = line.HasLyric;
            for (int i = 0; i < chords.Length; i++)
            {
                DevLineChord c = line.Chords[i];
                chords[i] = words && c.At >= 0 ? new LyricChord(c.Chord, (float)c.Beats, c.At) : new LyricChord(c.Chord, (float)c.Beats);
            }
            return new LyricLine(words ? line.Lyric : string.Empty, chords);
        }
    }
}
