using System.Collections.Generic;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Contracts
{
    /// <summary>What the audio analyzer should listen for right now.</summary>
    public enum ListenMode
    {
        /// <summary>Only onsets (timing, mic meter); no verification.</summary>
        None = 0,
        /// <summary>Measure the noise floor: the player stays quiet for ~2 s (MASTER_PLAN §19); emits <see cref="AudioEventKind.NoiseFloor"/>.</summary>
        NoiseFloor = 1,
        /// <summary>Tuner: a <see cref="TunerReading"/> every hop.</summary>
        Tune = 2,
        /// <summary>Pluck check of one string on each onset.</summary>
        Pluck = 3,
        /// <summary>Strum check of one chord on each onset.</summary>
        Strum = 4,
    }

    /// <summary>
    /// Immutable description of the current audio hypothesis. Produced by an <see cref="IPracticeSession"/> (via
    /// <see cref="ListenFor"/>) or by Runtime for the tuner/setup screens; Runtime hands the latest one to the audio worker
    /// by swapping a reference (atomic), so it must never be mutated after construction.
    /// </summary>
    public sealed class AudioExpectation
    {
        /// <summary>Listen for onsets only.</summary>
        public static readonly AudioExpectation Idle = new AudioExpectation(ListenMode.None, 0, 0, null, null);

        public readonly ListenMode Mode;
        /// <summary>Pluck: the string to check. Tune: the string being tuned, or 0 = auto (nearest open string). Otherwise 0.</summary>
        public readonly int StringNumber;
        /// <summary>Pluck: the expected MIDI note on <see cref="StringNumber"/>. Otherwise 0.</summary>
        public readonly int ExpectedMidi;
        /// <summary>Strum: the expected chord (also the chord whose shape the pluck targets come from). May be null otherwise.</summary>
        public readonly ChordShape Chord;
        /// <summary>Strum: the other hypotheses. Never null (empty when unused).</summary>
        public readonly IReadOnlyList<ChordShape> Confusers;

        public AudioExpectation(ListenMode mode, int stringNumber, int expectedMidi, ChordShape chord, IReadOnlyList<ChordShape> confusers)
        {
            Mode = mode; StringNumber = stringNumber; ExpectedMidi = expectedMidi; Chord = chord;
            Confusers = confusers ?? new ChordShape[0];
        }
    }
}
