// LearnChord: one chord shape as learn mode needs it: per string, the fret (x = do not play, 0 = open) and the finger, the
// finger-by-finger steps (finger 1 first) and the strings that must sound. Plain data, pure, unit-tested.
// Built from a row of the chord table (docs/MASTER_PLAN.md Appendix B2: frets and fingers listed string 6 -> 1), normally
// Core's ChordShape (FromShape; LearnChords looks the chord up in Core's ChordLibrary).
using System;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Runtime.Practice.Learn
{
    /// <summary>One finger of the finger-by-finger build.</summary>
    public struct LearnChordStep
    {
        public int finger;        // 1 index .. 4 little
        public int stringNumber;  // 1 high E .. 6 low E
        public int fret;          // 1..

        public LearnChordStep(int finger, int stringNumber, int fret)
        {
            this.finger = finger;
            this.stringNumber = stringNumber;
            this.fret = fret;
        }
    }

    public sealed class LearnChord
    {
        public const int StringCount = 6;

        /// <summary>A fret value meaning "do not play this string" (x in the chord table).</summary>
        public const int Muted = -1;

        static readonly string[] s_OpenNotes = { "E", "B", "G", "D", "A", "E" };   // string 1 .. 6, standard tuning

        readonly int[] _fret = new int[StringCount];     // index = string number - 1
        readonly int[] _finger = new int[StringCount];
        readonly LearnChordStep[] _steps;

        /// <summary>The chord / pose name ("G", "C", "D", "Em", "Am").</summary>
        public string Name { get; private set; }

        /// <summary>How many fingers the finger-by-finger build has.</summary>
        public int StepCount { get { return _steps.Length; } }

        LearnChord(string name, int[] fretsString6To1, int[] fingersString6To1)
        {
            Name = name;
            int fingered = 0;
            for (int column = 0; column < StringCount; column++)
            {
                int s = StringCount - column;                     // the table runs string 6 -> 1
                _fret[s - 1] = fretsString6To1[column] < 0 ? Muted : fretsString6To1[column];
                _finger[s - 1] = _fret[s - 1] > 0 ? Math.Max(0, fingersString6To1[column]) : 0;
                if (_finger[s - 1] >= 1 && _finger[s - 1] <= 4) fingered++;
            }

            _steps = new LearnChordStep[fingered];
            int n = 0;
            for (int finger = 1; finger <= 4; finger++)
                for (int s = StringCount; s >= 1; s--)
                    if (_finger[s - 1] == finger && n < _steps.Length) _steps[n++] = new LearnChordStep(finger, s, _fret[s - 1]);
        }

        /// <summary>
        /// Builds a chord from a chord-table row: <paramref name="fretsString6To1"/> (-1 = x, 0 = open) and
        /// <paramref name="fingersString6To1"/> (0 = none), both six long, string 6 first. Null on bad input.
        /// </summary>
        public static LearnChord FromRow(string name, int[] fretsString6To1, int[] fingersString6To1)
        {
            if (string.IsNullOrEmpty(name) || fretsString6To1 == null || fingersString6To1 == null) return null;
            if (fretsString6To1.Length != StringCount || fingersString6To1.Length != StringCount) return null;
            return new LearnChord(name, fretsString6To1, fingersString6To1);
        }

        /// <summary>The same chord as Core's <paramref name="shape"/> (its frets and fingers are listed string 6 first too). Null for null.</summary>
        public static LearnChord FromShape(ChordShape shape)
        {
            if (shape == null) return null;
            var frets = new int[StringCount];
            var fingers = new int[StringCount];
            for (int i = 0; i < StringCount; i++)
            {
                frets[i] = shape.FretsLowToHigh[i];
                fingers[i] = shape.FingersLowToHigh[i];
            }
            return FromRow(shape.Name, frets, fingers);
        }

        /// <summary>The fret on a string: <see cref="Muted"/> (do not play), 0 (open) or the fret number. Muted outside 1..6.</summary>
        public int Fret(int stringNumber)
        {
            return stringNumber >= 1 && stringNumber <= StringCount ? _fret[stringNumber - 1] : Muted;
        }

        /// <summary>The finger on a string (0 = none).</summary>
        public int Finger(int stringNumber)
        {
            return stringNumber >= 1 && stringNumber <= StringCount ? _finger[stringNumber - 1] : 0;
        }

        /// <summary>True when the string is part of the chord (open or fretted).</summary>
        public bool IsSounding(int stringNumber)
        {
            return Fret(stringNumber) >= 0;
        }

        /// <summary>True when the string rings open in this chord.</summary>
        public bool IsOpen(int stringNumber)
        {
            return Fret(stringNumber) == 0;
        }

        /// <summary>Step <paramref name="index"/> (0-based) of the finger-by-finger build: finger 1 first.</summary>
        public LearnChordStep Step(int index)
        {
            return index >= 0 && index < _steps.Length ? _steps[index] : default(LearnChordStep);
        }

        /// <summary>The lowest-pitched string that sounds (6 for G and Em, 5 for C and Am, 4 for D): where plucking starts.</summary>
        public int LowestSoundingString
        {
            get
            {
                for (int s = StringCount; s >= 1; s--)
                    if (IsSounding(s)) return s;
                return 1;
            }
        }

        /// <summary>How many strings sound.</summary>
        public int SoundingCount
        {
            get
            {
                int n = 0;
                for (int s = 1; s <= StringCount; s++)
                    if (IsSounding(s)) n++;
                return n;
            }
        }

        /// <summary>The note an open string is tuned to in standard tuning: string 1 "E" ... 6 "E" (empty outside 1..6).</summary>
        public static string TuningNote(int stringNumber)
        {
            return stringNumber >= 1 && stringNumber <= StringCount ? s_OpenNotes[stringNumber - 1] : string.Empty;
        }

        /// <summary>The name a beginner uses for a string in a tip: "high E", "B", "G", "D", "A", "low E".</summary>
        public static string StringName(int stringNumber)
        {
            switch (stringNumber)
            {
                case 1: return "high E";
                case 2: return "B";
                case 3: return "G";
                case 4: return "D";
                case 5: return "A";
                case 6: return "low E";
                default: return string.Empty;
            }
        }
    }
}
