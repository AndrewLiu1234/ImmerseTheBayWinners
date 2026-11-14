// PracticeFrame: one frame of practice-timeline state, in the terms the XR side draws it (ghost hands, play-along HUD).
//
// SEAM, Person 1's lane (Runtime). Today a TEMPORARY dev driver in Runtime/Practice/Dev fills it from
// StreamingAssets/songs/*.song.json, because Core (Person 2: Core/Songs, Core/Practice/PlayAlongSession) has not landed.
// When Core/Contracts and Core's PlayAlongSession land, an adapter fills this same struct and the dev driver is deleted;
// nothing that consumes the struct changes. Plain data, no UnityEngine types, no allocation when copied.
//
// Units: beats and seconds. Beat 0 is the downbeat of bar 1 (after any pickup); the count-in and a pickup run on negative beats.
// Chord names are the pose / chord-library names: "G", "C", "D", "Em", "Am" (StreamingAssets/poses/fretting-<name>.pose.json).
// Strum slots are song.json's strum.slots strings ("D" down, "U" up, "-" no stroke), docs/SONGS.md.
namespace UntitledGuitarApp.Runtime.Practice
{
    public struct PracticeFrame
    {
        /// <summary>True while a song is loaded and running, count-in included. False: hands rest, play HUD hidden.</summary>
        public bool Active;

        /// <summary>Beats per second after the tempo preset (tempoBpm / 60 * preset). 0 when not Active.</summary>
        public float BeatsPerSecond;

        /// <summary>Beats in one bar: the song's time-signature numerator, at least 1.</summary>
        public int BeatsPerBar;

        /// <summary>Song position in beats since the first downbeat (fractional). Negative during the count-in.</summary>
        public double Beat;

        /// <summary>The chord sounding now ("G"); null when there is none (count-in, before the first chord).</summary>
        public string Chord;

        /// <summary>The next different chord; null at the end of the song.</summary>
        public string NextChord;

        /// <summary>Beats from now until <see cref="NextChord"/> starts; double.PositiveInfinity when NextChord is null.</summary>
        public double BeatsToChange;

        /// <summary>Wait mode is holding the playhead at a chord change until the chord is played: Beat does not advance.</summary>
        public bool Waiting;

        /// <summary>Audio confirmed the current chord: the fretting ghost fades to the ghost.fadeAfterSuccess level.</summary>
        public bool ChordConfirmed;

        /// <summary>One bar of the song's strumming pattern (song.json strum.slots). Shared and read-only: never modify. Null = none.</summary>
        public string[] StrumSlots;

        /// <summary>
        /// Pattern slots per beat = slots per bar / beats per bar. song.json's strum.subdivision IS the slot count per bar (Core's
        /// Practice/PatternClock: "Pattern.Subdivision = slots per bar"): Riptide 8 slots / 4 beats = 2, Happy Birthday (3/4)
        /// 6 / 3 = 2. Never subdivision / 4. At least 1 whenever StrumSlots is set. Slot 0 is bar 1's downbeat (Beat 0); a pickup
        /// and the count-in are on negative beats and are not patterned (no strokes), as in PatternClock.
        /// </summary>
        public int SlotsPerBeat;

        /// <summary>Nothing to play: inactive, no chords, no pattern.</summary>
        public static PracticeFrame Idle
        {
            get
            {
                PracticeFrame f = default(PracticeFrame);
                f.BeatsPerBar = 4;
                f.BeatsToChange = double.PositiveInfinity;
                f.SlotsPerBeat = 1;
                return f;
            }
        }
    }
}
