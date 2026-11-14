namespace UntitledGuitarApp.Runtime.Hud.Play
{
    /// <summary>
    /// One chord of a line, as the HUD needs it: its name, how many beats it lasts, and (lines with words only) the
    /// character index in the lyric above which the name is drawn. Mirrors the song file's
    /// <c>{ "chord": "G", "beats": 4, "at": 0 }</c> without depending on Core: an adapter fills it from Core's song model.
    /// </summary>
    public struct LyricChord
    {
        /// <summary>Chord name as displayed ("G", "Em").</summary>
        public string Chord;

        /// <summary>How many beats the chord lasts.</summary>
        public float Beats;

        /// <summary>Character index in the lyric where the chord changes; -1 when the line has no words.</summary>
        public int At;

        public LyricChord(string chord, float beats, int at)
        {
            Chord = chord;
            Beats = beats;
            At = at;
        }

        public LyricChord(string chord, float beats)
        {
            Chord = chord;
            Beats = beats;
            At = -1;
        }
    }

    /// <summary>
    /// One line of a song for the <see cref="LyricStrip"/>: the words (empty for a chords-only song) and its chords in
    /// order. A plain data holder with no logic about time; the strip copies what it needs, so the same instance may be
    /// reused and refilled. The HUD never contains lyrics of its own: the words come from the song file, and only the
    /// public-domain song has any (docs/SONGS.md).
    /// </summary>
    public sealed class LyricLine
    {
        /// <summary>The words, or null/empty for a line with chords only.</summary>
        public string Lyric;

        /// <summary>The chords in playing order. May be longer than <see cref="Count"/>.</summary>
        public LyricChord[] Chords;

        /// <summary>How many entries of <see cref="Chords"/> are in use.</summary>
        public int Count;

        public LyricLine()
        {
        }

        public LyricLine(string lyric, params LyricChord[] chords)
        {
            Lyric = lyric;
            Chords = chords;
            Count = chords != null ? chords.Length : 0;
        }

        /// <summary>Number of usable chords: <see cref="Count"/>, limited to the array.</summary>
        public int ChordCount
        {
            get
            {
                if (Chords == null || Count <= 0) return 0;
                return Count < Chords.Length ? Count : Chords.Length;
            }
        }

        /// <summary>True when the line has words to show.</summary>
        public bool HasLyric
        {
            get { return !string.IsNullOrWhiteSpace(Lyric); }
        }

        /// <summary>Length of the line in beats (the sum of its chords).</summary>
        public float TotalBeats
        {
            get
            {
                float total = 0f;
                int n = ChordCount;
                for (int i = 0; i < n; i++)
                {
                    float b = Chords[i].Beats;
                    if (b > 0f) total += b;
                }
                return total;
            }
        }
    }
}
