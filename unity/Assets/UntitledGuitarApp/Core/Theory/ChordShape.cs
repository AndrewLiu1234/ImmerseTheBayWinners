using System;
using System.Collections.Generic;

namespace UntitledGuitarApp.Core.Theory
{
    /// <summary>One fretted note of a chord shape: which finger presses which string at which fret.</summary>
    public readonly struct FrettedNote
    {
        public readonly int String;  // 1 = high E … 6 = low E
        public readonly int Fret;    // ≥ 1
        public readonly int Finger;  // 1 = index … 4 = pinky

        public FrettedNote(int stringNumber, int fret, int finger) { String = stringNumber; Fret = fret; Finger = finger; }
    }

    /// <summary>
    /// Immutable open-position chord shape in standard tuning. The *LowToHigh lists are ordered string 6 → 1, like
    /// MASTER_PLAN Appendix B2. Fret −1 = muted (don't play), 0 = open. Finger 0 = no finger.
    /// Use <see cref="FretOn"/>/<see cref="FingerOn"/>/<see cref="MidiOn"/> with string numbers (1 = high E).
    /// </summary>
    public sealed class ChordShape
    {
        public const int Muted = -1;
        public const int NoFinger = 0;

        readonly int[] _frets;   // index 0 = string 6
        readonly int[] _fingers;
        readonly int[] _midi;

        public string Name { get; }
        public IReadOnlyList<int> FretsLowToHigh => _frets;
        public IReadOnlyList<int> FingersLowToHigh => _fingers;
        /// <summary>Expected MIDI per string, string 6 → 1; −1 where the string is muted.</summary>
        public IReadOnlyList<int> ExpectedMidiLowToHigh => _midi;
        /// <summary>Lowest played string (the "B" in picking patterns, Appendix B5): Em/G → 6, C/Am → 5, D → 4.</summary>
        public int BassString { get; }

        public ChordShape(string name, int[] fretsLowToHigh, int[] fingersLowToHigh)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("chord needs a name", nameof(name));
            if (fretsLowToHigh == null || fretsLowToHigh.Length != Tuning.StringCount)
                throw new ArgumentException("need 6 frets (string 6 → 1)", nameof(fretsLowToHigh));
            if (fingersLowToHigh == null || fingersLowToHigh.Length != Tuning.StringCount)
                throw new ArgumentException("need 6 fingers (string 6 → 1)", nameof(fingersLowToHigh));

            Name = name;
            _frets = (int[])fretsLowToHigh.Clone();
            _fingers = (int[])fingersLowToHigh.Clone();
            _midi = new int[Tuning.StringCount];
            BassString = 0;
            for (int i = 0; i < Tuning.StringCount; i++)
            {
                int s = Tuning.StringCount - i;
                int fret = _frets[i], finger = _fingers[i];
                if (fret < Muted) throw new ArgumentOutOfRangeException(nameof(fretsLowToHigh), fret, "fret must be −1, 0 or positive");
                if (finger < NoFinger || finger > 4) throw new ArgumentOutOfRangeException(nameof(fingersLowToHigh), finger, "finger must be 0 … 4");
                if (fret <= 0 && finger != NoFinger) throw new ArgumentException($"string {s}: open/muted string can't have a finger");
                if (fret > 0 && finger == NoFinger) throw new ArgumentException($"string {s}: fretted note needs a finger");
                _midi[i] = fret == Muted ? Muted : Tuning.StandardMidi(s) + fret;
                if (fret != Muted && BassString == 0) BassString = s;
            }
            if (BassString == 0) throw new ArgumentException("chord must play at least one string");
        }

        public int FretOn(int stringNumber) { Tuning.CheckString(stringNumber); return _frets[Tuning.StringCount - stringNumber]; }
        public int FingerOn(int stringNumber) { Tuning.CheckString(stringNumber); return _fingers[Tuning.StringCount - stringNumber]; }
        public int MidiOn(int stringNumber) { Tuning.CheckString(stringNumber); return _midi[Tuning.StringCount - stringNumber]; }
        public bool IsPlayed(int stringNumber) => FretOn(stringNumber) != Muted;

        /// <summary>Fretted notes, low string first. Allocates; call at setup time, not per frame.</summary>
        public IEnumerable<FrettedNote> FrettedNotes()
        {
            for (int s = Tuning.StringCount; s >= 1; s--)
            {
                int fret = FretOn(s);
                if (fret > 0) yield return new FrettedNote(s, fret, FingerOn(s));
            }
        }

        public override string ToString() => Name;
    }
}
