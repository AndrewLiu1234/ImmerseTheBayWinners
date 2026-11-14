// PlayAlongPresenter: pushes one PracticeFrame per frame (plus the song's lines from Core's Song) into the play HUD that already
// exists (Runtime/Hud/Play: NeckHudCard, ApproachRings, ResultsCard) and onto the floating neck's fingertip dots and X/O marks.
// It owns nothing but references and a few preallocated buffers: every visible thing belongs to the HUD and the neck and is
// driven only through their public API. It never reads Core's session directly: the frame is the seam.
//
// What goes where, per frame:
//   card.SetChords(now, next)   - in the count-in "now" is the first chord (the count-in takes the next-chord block's place)
//   card.SetWaiting(Waiting)    - the "strum G" wait cue
//   card.Beats.Set(bar, position in bar, whole beats to the change; to the end of the song for the last chord)
//   card.Pattern                - one bar of the strum pattern, cursor = Beat * SlotsPerBeat (slot 0 on every bar line)
//   card.Lyrics                 - the current and next line, progress in beats into the current line
//   card.CountIn                - 4-3-2-1 over the bar before the first chord
//   neck.SetDots/SetStringMarks - the chord being played (the first chord during the count-in), from CoreNeckLayout.ChordMarks,
//                                 only when it changes
//   rings.Schedule/Tick         - rings closing on the NEXT chord's dots, on the song clock (Beat / BeatsPerSecond), so they
//                                 freeze, landed, while wait mode holds
//   results.Show                - once, when the run has ended (labelled when simulated)
//
// Main thread only. Present allocates nothing (LyricLine objects are built once per song in SetSong).
using System;
using UnityEngine;
using UntitledGuitarApp.Core.Songs;
using UntitledGuitarApp.Core.Theory;
using UntitledGuitarApp.Runtime.Hud.Play;
using UntitledGuitarApp.Runtime.Neck;
using FingertipDot = UntitledGuitarApp.Runtime.Neck.FingertipDot;

namespace UntitledGuitarApp.Runtime.Practice
{
    /// <summary>
    /// Feeds the play HUD and the neck from a <see cref="PracticeFrame"/>. Create the HUD pieces with <see cref="Create"/> (or
    /// hand existing ones to <see cref="Bind"/>), give it the song with <see cref="SetSong"/>, then call <see cref="Present"/>
    /// (or <see cref="PresentPlayAlong"/>) every frame.
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

        Song _song;
        LyricLine[] _lines = new LyricLine[0];
        double[] _lineStarts = new double[0];   // frame beats
        double _firstBeat;                      // frame beat of the first chord (-pickup)
        double _endBeat;                        // frame beat at which the last chord ends
        int _shownLine = -1;
        string _shownShape;
        bool _shapeApplied;
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
        public Song Song { get { return _song; } }

        /// <summary>True while the results card is up.</summary>
        public bool ShowingResults { get { return _resultsShown; } }

        /// <summary>The chord whose shape is on the neck now (null before the first frame).</summary>
        public string ShownShape { get { return _shownShape; } }

        /// <summary>Index of the line shown on the lyric strip (-1 when none).</summary>
        public int ShownLine { get { return _shownLine; } }

        /// <summary>Frame beat at which line <paramref name="index"/> starts (NaN when out of range).</summary>
        public double LineStartBeat(int index)
        {
            return index >= 0 && index < _lineStarts.Length ? _lineStarts[index] : double.NaN;
        }

        /// <summary>
        /// Creates the play HUD: the card and the rings under <paramref name="hudParent"/> (identity scale; not the neck), tied
        /// to <paramref name="neck"/>'s socket and <paramref name="head"/>, and the results card head-locked
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
        public void SetSong(Song song)
        {
            _song = song;
            _shownLine = -1;
            _shownShape = null;
            _shapeApplied = false;
            _ringChord = null;
            _ringCount = 0;
            if (_rings != null) _rings.Clear();
            HideResults();

            BuildLines(song, out _lines, out _lineStarts, out _firstBeat, out _endBeat);
            if (_card != null && song != null) _card.Lyrics.BeatsPerBar = Math.Max(1, song.BeatsPerBar);
        }

        /// <summary>The adapter's frame, plus the results card once the run has ended. Call every frame.</summary>
        public void PresentPlayAlong(CorePlayAlong play, float deltaSeconds)
        {
            if (play == null) return;
            if (play.Song != null && !ReferenceEquals(play.Song, _song)) SetSong(play.Song);
            Present(play.Frame, deltaSeconds);
            if (play.Phase == PlayAlongPhase.Ended && !_resultsShown)
                ShowResults(play.Results, _song != null ? _song.Title : null);
        }

        /// <summary>One frame of play-along. An inactive frame hides the card and the rings (the results card stays). No allocation.</summary>
        public void Present(PracticeFrame frame, float deltaSeconds)
        {
            if (!frame.Active || _song == null)
            {
                HideCard();
                return;
            }
            if (_resultsShown) HideResults();
            _cardShown = true;

            bool countIn = frame.Chord == null && frame.Beat < _firstBeat - PlayAlongMath.Epsilon;
            string shape = frame.Chord ?? frame.NextChord;
            int bpb = Math.Max(1, frame.BeatsPerBar);

            if (_card != null)
            {
                _card.SetChords(shape, countIn ? null : frame.NextChord);
                _card.SetWaiting(frame.Waiting);

                double toChange = double.IsInfinity(frame.BeatsToChange) ? _endBeat - frame.Beat : frame.BeatsToChange;
                BeatDots beats = _card.Beats;
                beats.BeatsPerSecond = frame.BeatsPerSecond;
                beats.Set(bpb, (float)PlayAlongMath.BeatInBar(frame.Beat, bpb), PlayAlongMath.WholeBeatsLeft(toChange));

                PatternStrip pattern = _card.Pattern;
                pattern.SlotsPerBeat = Math.Max(1, frame.SlotsPerBeat);
                pattern.SetPattern(frame.StrumSlots, false);
                pattern.SetCursor((float)PlayAlongMath.SlotPosition(frame.Beat, frame.SlotsPerBeat));

                PresentLyrics(frame.Beat);

                if (countIn) _card.CountIn.Show(PlayAlongMath.CountInBeatsLeft(frame.Beat, _firstBeat));
                else _card.CountIn.Hide();
            }

            if (!_shapeApplied || !string.Equals(shape, _shownShape, StringComparison.Ordinal)) ApplyShape(shape);
            PresentRings(frame);
        }

        /// <summary>Shows the results card (and hides the card and rings). Simulated scores say so on the card.</summary>
        public void ShowResults(PlayAlongResults results, string title)
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
            int line = LineIndexAt(_lineStarts, beat);
            if (line != _shownLine)
            {
                _shownLine = line;
                _card.Lyrics.SetLines(_lines[line], line + 1 < _lines.Length ? _lines[line + 1] : null);
            }
            _card.Lyrics.SetProgress((float)Math.Max(0.0, beat - _lineStarts[line]));
        }

        void ApplyShape(string chord)
        {
            _shownShape = chord;
            _shapeApplied = true;
            if (_neck == null) return;
            int n = chord != null ? CoreNeckLayout.ChordMarks(CoreNeckLayout.DefaultGeometry, chord, _dots, _marks) : -1;
            if (n < 0)
            {
                // No shape for this chord: clear the dots rather than leave the last chord's.
                n = 0;
                for (int i = 0; i < _marks.Length; i++) _marks[i] = StringMark.None;
            }
            _neck.SetDots(_dots, n);
            _neck.SetStringMarks(_marks);
            if (_card != null) _card.Pattern.SetBassString(BassString(chord));
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
            int n = CoreNeckLayout.ChordMarks(CoreNeckLayout.DefaultGeometry, chord, _ringDots, _ringMarks);
            for (int i = 0; i < n && _ringCount < _ringTargets.Length; i++)
                _ringTargets[_ringCount++] = new Vector3(_ringDots[i].xMm, 0f, _ringDots[i].zMm);   // fretboard plane, neck mm
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

        // ------------------------------------------------------------------ pure helpers (tested offline)

        /// <summary>The chord's bass string (Core's ChordLibrary: G 6, C 5, D 4, Em 6, Am 5); 0 when unknown.</summary>
        public static int BassString(string chord)
        {
            ChordShape shape;
            return !string.IsNullOrEmpty(chord) && ChordLibrary.TryGet(chord, out shape) ? shape.BassString : 0;
        }

        /// <summary>Index of the line at <paramref name="frameBeat"/>, clamped to the first and last line (0 when there are none).</summary>
        public static int LineIndexAt(double[] lineStarts, double frameBeat)
        {
            if (lineStarts == null || lineStarts.Length == 0) return 0;
            for (int i = lineStarts.Length - 1; i > 0; i--)
            {
                if (frameBeat >= lineStarts[i] - PlayAlongMath.Epsilon) return i;
            }
            return 0;
        }

        /// <summary>
        /// The song's lines as the lyric strip wants them (words only on lines that have some; "at" only there), each line's
        /// start as a frame beat, the first chord's frame beat (-pickup) and the frame beat where the last chord ends.
        /// Allocates; call once per song.
        /// </summary>
        public static void BuildLines(Song song, out LyricLine[] lines, out double[] lineStarts, out double firstBeat, out double endBeat)
        {
            var list = new System.Collections.Generic.List<LyricLine>();
            var starts = new System.Collections.Generic.List<double>();
            double pickup = song != null && song.PickupBeats > 0 ? song.PickupBeats : 0;
            double coreBeat = 0;
            if (song != null)
            {
                for (int s = 0; s < song.Sections.Count; s++)
                {
                    SongSection section = song.Sections[s];
                    for (int l = 0; l < section.Lines.Count; l++)
                    {
                        SongLine line = section.Lines[l];
                        bool words = !string.IsNullOrWhiteSpace(line.Lyric);
                        var chords = new LyricChord[line.Chords.Count];
                        starts.Add(PlayAlongMath.FrameBeat(coreBeat, pickup));
                        for (int c = 0; c < chords.Length; c++)
                        {
                            SongChord chord = line.Chords[c];
                            chords[c] = words && chord.At >= 0 ? new LyricChord(chord.Chord, (float)chord.Beats, chord.At) : new LyricChord(chord.Chord, (float)chord.Beats);
                            coreBeat += chord.Beats;   // the same accumulation as Core's SongTimeline
                        }
                        list.Add(new LyricLine(words ? line.Lyric : string.Empty, chords));
                    }
                }
            }
            lines = list.ToArray();
            lineStarts = starts.ToArray();
            firstBeat = -pickup;
            endBeat = PlayAlongMath.FrameBeat(coreBeat, pickup);
        }
    }
}
