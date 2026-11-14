// DemoState: what the demo operator has chosen (mode, song, learn chord, tempo preset), as pure state with no Unity calls.
//
// SL9 demo ops. The operator sits at the Mac while a judge wears the glasses; DemoKeys turns key presses into DemoCommands,
// DemoState applies them, and Bootstrap restarts whatever DemoState says is active whenever Generation changes.
// Counts come from the song folder and the flags (chords, tempoPresets), which hot-reload: SetCounts clamps the indices.
using System;

namespace UntitledGuitarApp.Runtime
{
    /// <summary>The three things the app can be doing.</summary>
    public enum DemoMode
    {
        /// <summary>The walking skeleton: floating neck with one chord, the fretting ghost hand, the head-locked HUD.</summary>
        Idle,
        /// <summary>Learn one chord: tuner, finger by finger, pluck check, strum check.</summary>
        Learn,
        /// <summary>Play along with a song at a tempo preset.</summary>
        Play,
    }

    /// <summary>One operator request.</summary>
    public enum DemoCommand
    {
        None,
        Idle,
        Learn,
        Play,
        /// <summary>Next song (Play) or next chord (Learn).</summary>
        Next,
        TempoDown,
        TempoUp,
        /// <summary>Skip the current learn step.</summary>
        Skip,
        /// <summary>Re-centre the head pose (straight ahead = now).</summary>
        Recenter,
        /// <summary>Put the virtual neck on the real guitar: 4 glances (NeckAligner). Goes to idle first.</summary>
        Align,
        /// <summary>One glance at the nut to re-anchor an aligned neck (any mode).</summary>
        Reanchor,
    }

    /// <summary>What a command changed, so the caller knows whether to restart the active mode.</summary>
    public enum DemoChange
    {
        None,
        /// <summary>The mode, song, chord or (while playing) the tempo changed: restart the active mode.</summary>
        Restart,
        /// <summary>Only the tempo index for the NEXT play changed (not playing now): nothing to restart.</summary>
        TempoForNextPlay,
        Skip,
        Recenter,
        /// <summary>Start the 4-glance alignment (the mode is now Idle; restarted if it was not).</summary>
        Align,
        /// <summary>Start the one-glance re-anchor.</summary>
        Reanchor,
    }

    public sealed class DemoState
    {
        int _songs;
        int _chords;
        int _tempos;

        public DemoMode Mode { get; private set; }
        public int SongIndex { get; private set; }
        public int ChordIndex { get; private set; }
        public int TempoIndex { get; private set; }

        /// <summary>Bumped on every change that requires the active mode to restart. Starts at 0.</summary>
        public int Generation { get; private set; }

        /// <summary>Why the last command did nothing (null when it did something). For the log, never shown to the wearer.</summary>
        public string LastRefusal { get; private set; }

        public DemoState(int songCount, int chordCount, int tempoCount, int startTempoIndex)
        {
            Mode = DemoMode.Idle;
            SetCounts(songCount, chordCount, tempoCount);
            TempoIndex = Clamp(startTempoIndex, _tempos);
        }

        public int SongCount { get { return _songs; } }
        public int ChordCount { get { return _chords; } }
        public int TempoCount { get { return _tempos; } }

        /// <summary>New counts (flags or the song folder changed). Indices are clamped; the mode is kept. Negative counts are 0.</summary>
        public void SetCounts(int songCount, int chordCount, int tempoCount)
        {
            _songs = Math.Max(0, songCount);
            _chords = Math.Max(0, chordCount);
            _tempos = Math.Max(0, tempoCount);
            SongIndex = Clamp(SongIndex, _songs);
            ChordIndex = Clamp(ChordIndex, _chords);
            TempoIndex = Clamp(TempoIndex, _tempos);
            if (Mode == DemoMode.Play && _songs == 0) Mode = DemoMode.Idle;
            if (Mode == DemoMode.Learn && _chords == 0) Mode = DemoMode.Idle;
        }

        /// <summary>
        /// Picks the song, chord and tempo preset directly (smoke-run arguments, a future picker). Each index is clamped; the mode
        /// is not changed and nothing restarts: follow with a mode command, which restarts.
        /// </summary>
        public void SetIndices(int songIndex, int chordIndex, int tempoIndex)
        {
            SongIndex = Clamp(songIndex, _songs);
            ChordIndex = Clamp(chordIndex, _chords);
            TempoIndex = Clamp(tempoIndex, _tempos);
        }

        public DemoChange Apply(DemoCommand command)
        {
            LastRefusal = null;
            switch (command)
            {
                case DemoCommand.Idle:
                    return Enter(DemoMode.Idle);
                case DemoCommand.Learn:
                    if (_chords == 0) return Refuse("no chords to learn");
                    return Enter(DemoMode.Learn);
                case DemoCommand.Play:
                    if (_songs == 0) return Refuse("no songs to play");
                    return Enter(DemoMode.Play);
                case DemoCommand.Next:
                    if (Mode == DemoMode.Play && _songs > 0) { SongIndex = (SongIndex + 1) % _songs; return Restart(); }
                    if (Mode == DemoMode.Learn && _chords > 0) { ChordIndex = (ChordIndex + 1) % _chords; return Restart(); }
                    return Refuse("next does nothing while idle");
                case DemoCommand.TempoDown:
                case DemoCommand.TempoUp:
                {
                    if (_tempos == 0) return Refuse("no tempo presets");
                    int next = Clamp(TempoIndex + (command == DemoCommand.TempoUp ? 1 : -1), _tempos);
                    if (next == TempoIndex) return Refuse("already at the " + (command == DemoCommand.TempoUp ? "fastest" : "slowest") + " preset");
                    TempoIndex = next;
                    return Mode == DemoMode.Play ? Restart() : DemoChange.TempoForNextPlay;
                }
                case DemoCommand.Skip:
                    // learn: the next stage; play: release a wait-mode hold
                    if (Mode == DemoMode.Idle) return Refuse("skip does nothing while idle");
                    return DemoChange.Skip;
                case DemoCommand.Recenter:
                    return DemoChange.Recenter;
                case DemoCommand.Align:
                    if (Mode != DemoMode.Idle)
                    {
                        Mode = DemoMode.Idle;   // align from the walking skeleton: stop a song or a lesson first
                        Generation++;
                    }
                    return DemoChange.Align;
                case DemoCommand.Reanchor:
                    return DemoChange.Reanchor;
                default:
                    return DemoChange.None;
            }
        }

        /// <summary>The index of the preset closest to <paramref name="target"/> (first one on a tie); 0 for an empty or null list.</summary>
        public static int NearestIndex(float[] presets, float target)
        {
            if (presets == null || presets.Length == 0) return 0;
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < presets.Length; i++)
            {
                float d = Math.Abs(presets[i] - target);
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return best;
        }

        DemoChange Enter(DemoMode mode)
        {
            Mode = mode;   // entering the mode that is already active restarts it (e.g. play the song again)
            return Restart();
        }

        DemoChange Restart()
        {
            Generation++;
            return DemoChange.Restart;
        }

        DemoChange Refuse(string why)
        {
            LastRefusal = why;
            return DemoChange.None;
        }

        static int Clamp(int index, int count)
        {
            if (count <= 0) return 0;
            if (index < 0) return 0;
            return index >= count ? count - 1 : index;
        }
    }
}
