using System.Collections.Generic;

namespace UntitledGuitarApp.Core.Songs
{
    /// <summary>One chord on a lyric line. At = character index in the lyric where the chord name is drawn (-1 = none).</summary>
    public sealed class SongChord
    {
        public string Chord;
        public double Beats;
        public int At = -1;
    }

    public sealed class SongLine
    {
        public string Lyric = "";
        public List<SongChord> Chords = new List<SongChord>();
    }

    public sealed class SongSection
    {
        public string Name = "";
        public List<SongLine> Lines = new List<SongLine>();
    }

    /// <summary>A strum or pick pattern: Subdivision slots per bar, one entry each (strum: D U -; pick: B 1-6 -).</summary>
    public sealed class SongPattern
    {
        public int Subdivision;
        public List<string> Slots = new List<string>();
    }

    /// <summary>Song JSON v1 (docs/ARCHITECTURE.md). Plain data; SongParser fills it, SongValidator checks it.</summary>
    public sealed class Song
    {
        public int Schema;
        public string Id = "", Title = "", Artist = "", License = "", Source = "", Credit = "", Key = "", Notes = "";
        public double TempoBpm;
        public int BeatsPerBar, BeatUnit;
        public double PickupBeats;
        public SongPattern Strum, Pick;
        public List<SongSection> Sections = new List<SongSection>();
    }
}
