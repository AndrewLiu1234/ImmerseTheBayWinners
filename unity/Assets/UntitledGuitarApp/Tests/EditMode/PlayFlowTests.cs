// PlayFlowTests: the TEMPORARY play-along dev driver (Runtime/Practice/Dev), the dev chord shapes (Runtime/Neck/Dev) and
// the presenter's pure helper. Everything except the last fixture is pure logic and runs offline (mono-tests.sh) as well as
// in Unity; PlayFlowEngineTests builds the real HUD and neck, so only Unity can run it.
//
// The song and pose files are read from this checkout's StreamingAssets, found from this file's own path (CallerFilePath),
// so no engine call (Application.streamingAssetsPath) is needed; in Unity the compiler's relative path resolves against
// the project folder, which is the editor's working directory.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.Hud.Play;
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Neck.Dev;
using UntitledGuitarApp.Runtime.Practice;
using UntitledGuitarApp.Runtime.Practice.Dev;

namespace UntitledGuitarApp.Tests.EditMode
{
    static class PlayFlowFiles
    {
        public static string AssetsFolder([CallerFilePath] string here = "")
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(here));
            return Path.GetFullPath(Path.Combine(dir, "..", "..", ".."));
        }

        public static string StreamingAssets
        {
            get { return Path.Combine(AssetsFolder(), "StreamingAssets"); }
        }

        public static DevSong Song(string id)
        {
            string folder = Path.Combine(StreamingAssets, DevSongReader.FolderName);
            if (!Directory.Exists(folder)) Assert.Inconclusive("songs folder not found: " + folder);
            DevSong song;
            string error;
            Assert.IsTrue(DevSongReader.LoadFile(DevSongReader.PathFor(folder, id), out song, out error), error);
            return song;
        }

        public static string PoseText(string chord)
        {
            string path = Path.Combine(StreamingAssets, "poses", "fretting-" + chord + ".pose.json");
            if (!File.Exists(path)) Assert.Inconclusive("pose file not found: " + path);
            return File.ReadAllText(path);
        }

        /// <summary>Ticks in fixed steps until <paramref name="until"/> is true; fails after <paramref name="maxSeconds"/>.</summary>
        public static void RunUntil(DevPlayAlongClock clock, Func<DevPlayAlongClock, bool> until, float step, float maxSeconds)
        {
            float t = 0f;
            while (!until(clock))
            {
                Assert.Less(t, maxSeconds, "condition not reached in " + maxSeconds + " s (beat " + clock.Beat + ", state " + clock.State + ")");
                clock.Tick(step);
                t += step;
            }
        }
    }

    public class PlayFlowTests
    {
        const double Tol = 1e-6;

        // ------------------------------------------------------------------ timeline building

        [Test]
        public void Riptide_BuildsFlatTimeline()
        {
            DevSong s = PlayFlowFiles.Song("riptide-loop");
            Assert.AreEqual("riptide-loop", s.Id);
            Assert.AreEqual(100.0, s.TempoBpm, Tol);
            Assert.AreEqual(4, s.BeatsPerBar);
            Assert.AreEqual(0.0, s.StartBeat, Tol, "no pickup: the first chord is on beat 0");
            Assert.AreEqual(10, s.Lines.Length, "2 intro + 8 loop lines");
            Assert.AreEqual(30, s.Runs.Length, "Am G C per line; C to Am at every line break, so nothing merges");
            Assert.AreEqual(80.0, s.EndBeat, Tol);
            Assert.AreEqual("Am", s.Runs[0].Chord); Assert.AreEqual(0.0, s.Runs[0].StartBeat, Tol); Assert.AreEqual(2.0, s.Runs[0].Beats, Tol);
            Assert.AreEqual("G", s.Runs[1].Chord); Assert.AreEqual(2.0, s.Runs[1].StartBeat, Tol);
            Assert.AreEqual("C", s.Runs[2].Chord); Assert.AreEqual(4.0, s.Runs[2].StartBeat, Tol); Assert.AreEqual(4.0, s.Runs[2].Beats, Tol);
            Assert.AreEqual("Am", s.Runs[3].Chord); Assert.AreEqual(8.0, s.Runs[3].StartBeat, Tol); Assert.AreEqual(1, s.Runs[3].Line);
            Assert.AreEqual(8, s.StrumSlots.Length);
            Assert.AreEqual(8, s.StrumSubdivision);
            Assert.AreEqual(2, s.SlotsPerBeat, "8 slots in a 4/4 bar");
            Assert.AreEqual(8, s.PickSlots.Length);
            Assert.IsFalse(s.HasLyrics, "chords-only song");
            Assert.AreEqual("Intro", s.Lines[0].Section);
            Assert.AreEqual("Loop", s.Lines[2].Section);
            Assert.AreEqual(-1, s.Lines[0].Chords[0].At, "no words, no at: -1");
        }

        [Test]
        public void HappyBirthday_PickupStartsBeforeBeatZero_AndMergesAcrossLineBreaks()
        {
            DevSong s = PlayFlowFiles.Song("happy-birthday");
            Assert.AreEqual(3, s.BeatsPerBar);
            Assert.AreEqual(1.0, s.PickupBeats, Tol);
            Assert.AreEqual(-1.0, s.StartBeat, Tol, "the pickup is beat 3 of the bar before beat 0");
            Assert.AreEqual(24.0, s.EndBeat, Tol, "1 pickup beat + 8 bars of 3/4");
            string[] chords = { "G", "D", "G", "C", "G", "D", "G" };
            double[] starts = { -1, 3, 9, 15, 17, 20, 21 };
            double[] lengths = { 4, 6, 6, 2, 3, 1, 3 };
            Assert.AreEqual(chords.Length, s.Runs.Length);
            for (int i = 0; i < chords.Length; i++)
            {
                Assert.AreEqual(chords[i], s.Runs[i].Chord, "run " + i);
                Assert.AreEqual(starts[i], s.Runs[i].StartBeat, Tol, "run " + i + " start");
                Assert.AreEqual(lengths[i], s.Runs[i].Beats, Tol, "run " + i + " length");
            }
            Assert.IsTrue(s.HasLyrics);
            Assert.AreEqual("Happy birthday to you", s.Lines[0].Lyric);
            Assert.AreEqual(18, s.Lines[0].Chords[1].At);
            Assert.AreEqual(5.0, s.Lines[1].StartBeat, Tol);
            Assert.AreEqual(2, s.SlotsPerBeat, "6 slots in a 3/4 bar (subdivision / 4 would give 1)");
        }

        [Test]
        public void StandByMe_MergesTheTwoBarsOfG()
        {
            DevSong s = PlayFlowFiles.Song("stand-by-me-loop");
            Assert.AreEqual(9, s.Runs.Length);
            Assert.AreEqual("G", s.Runs[4].Chord);
            Assert.AreEqual(24.0, s.Runs[4].StartBeat, Tol);
            Assert.AreEqual(16.0, s.Runs[4].Beats, Tol, "G 8 at the end of line 2 + G 8 at the start of line 3");
        }

        [Test]
        public void Parse_RefusesBadFiles()
        {
            DevSong song;
            string error;
            Assert.IsFalse(DevSongReader.Parse("{ not json", out song, out error)); Assert.IsNull(song); StringAssert.Contains("JSON", error);
            Assert.IsFalse(DevSongReader.Parse("{\"schema\":2,\"tempoBpm\":90,\"timeSignature\":[4,4]}", out song, out error));
            Assert.IsFalse(DevSongReader.Parse("{\"schema\":1,\"timeSignature\":[4,4]}", out song, out error)); StringAssert.Contains("tempoBpm", error);
            Assert.IsFalse(DevSongReader.Parse("{\"schema\":1,\"tempoBpm\":90,\"timeSignature\":[4]}", out song, out error));
            Assert.IsFalse(DevSongReader.Parse("{\"schema\":1,\"tempoBpm\":90,\"timeSignature\":[4,4],\"sections\":[]}", out song, out error));
            StringAssert.Contains("no chord", error);
            Assert.IsTrue(DevSongReader.Parse("{\"schema\":1,\"tempoBpm\":90,\"timeSignature\":[4,4],\"sections\":[{\"lines\":[{\"lyric\":\"\",\"chords\":[{\"chord\":\"G\",\"beats\":4}]}]}]}", out song, out error), error);
            Assert.IsNull(song.StrumSlots, "no strum block: no pattern");
            Assert.AreEqual(1, song.SlotsPerBeat);
        }

        [Test]
        public void DevJson_ReadsEscapesNumbersAndTellsMissingFromZero()
        {
            object root;
            string error;
            Assert.IsTrue(DevJson.TryParse("{\"a\":\"x\\\"y\\u00e9\\n\",\"n\":-1.5e2,\"z\":0,\"t\":true,\"nul\":null,\"l\":[1,[2],{}]}", out root, out error), error);
            string a;
            Assert.IsTrue(DevJson.TryGetString(root, "a", out a)); Assert.AreEqual("x\"yé\n", a);
            double n;
            Assert.IsTrue(DevJson.TryGetNumber(root, "n", out n)); Assert.AreEqual(-150.0, n, Tol);
            Assert.IsTrue(DevJson.TryGetNumber(root, "z", out n)); Assert.AreEqual(0.0, n, Tol);
            Assert.IsFalse(DevJson.TryGetNumber(root, "missing", out n), "a missing key is not 0");
            Assert.IsTrue(DevJson.Has(root, "nul")); Assert.IsFalse(DevJson.Has(root, "missing"));
            List<object> l;
            Assert.IsTrue(DevJson.TryGetArray(root, "l", out l)); Assert.AreEqual(3, l.Count);
            Assert.IsFalse(DevJson.TryParse("[1,]", out root, out error));
            Assert.IsFalse(DevJson.TryParse("{\"a\":1} x", out root, out error));
            Assert.IsFalse(DevJson.TryParse("\"open", out root, out error));
        }

        // ------------------------------------------------------------------ count-in

        [Test]
        public void CountIn_IsOneBarOnNegativeBeats_Riptide()
        {
            DevSong s = PlayFlowFiles.Song("riptide-loop");
            var clock = new DevPlayAlongClock();
            Assert.IsTrue(clock.Start(s, 1f, false));
            PracticeFrame f = clock.Frame;
            Assert.AreEqual(DevPlayState.CountIn, clock.State);
            Assert.IsTrue(f.Active);
            Assert.AreEqual(-4.0, f.Beat, Tol);
            Assert.IsNull(f.Chord, "no chord sounds in the count-in");
            Assert.AreEqual("Am", f.NextChord, "the first chord is next");
            Assert.AreEqual(4.0, f.BeatsToChange, Tol);
            Assert.AreEqual(4, DevTimeline.CountInBeatsLeft(-4.0, 0.0));
            Assert.AreEqual(4, DevTimeline.CountInBeatsLeft(-3.5, 0.0));
            Assert.AreEqual(3, DevTimeline.CountInBeatsLeft(-3.0, 0.0), "changes exactly on the beat");
            Assert.AreEqual(1, DevTimeline.CountInBeatsLeft(-0.01, 0.0));
            Assert.AreEqual(0, DevTimeline.CountInBeatsLeft(0.0, 0.0));

            // 100 bpm at 100% = 0.6 s a beat: after 2.4 s the count-in is over and Am sounds.
            for (int i = 0; i < 239; i++) clock.Tick(0.01f);
            Assert.AreEqual(DevPlayState.CountIn, clock.State, "still counting at 2.39 s");
            clock.Tick(0.02f);
            Assert.AreEqual(DevPlayState.Playing, clock.State);
            Assert.AreEqual("Am", clock.Frame.Chord);
            Assert.AreEqual("G", clock.Frame.NextChord);
        }

        [Test]
        public void CountIn_EndsAtThePickup_HappyBirthday()
        {
            DevSong s = PlayFlowFiles.Song("happy-birthday");
            var clock = new DevPlayAlongClock();
            Assert.IsTrue(clock.Start(s, 1f, false));
            Assert.AreEqual(-4.0, clock.Beat, Tol, "one 3/4 bar before the pickup at beat -1");
            Assert.AreEqual(3.0, clock.Frame.BeatsToChange, Tol);
            Assert.AreEqual("G", clock.Frame.NextChord);
            Assert.AreEqual(3, DevTimeline.CountInBeatsLeft(clock.Beat, s.StartBeat), "3, 2, 1 in 3/4");
        }

        // ------------------------------------------------------------------ next chord and beats to change

        [Test]
        public void NextChord_IsTheNextDifferentChord_AcrossALineBreak()
        {
            DevSong s = PlayFlowFiles.Song("happy-birthday");
            var clock = new DevPlayAlongClock();
            clock.SimulateConfirmation = false;
            clock.Start(s, 1f, false);
            PlayFlowFiles.RunUntil(clock, c => c.Beat >= 4.0, 0.01f, 10f);
            PracticeFrame f = clock.Frame;
            Assert.AreEqual("D", f.Chord);
            Assert.AreEqual("G", f.NextChord, "line 2 starts with D again; the next DIFFERENT chord is G at beat 9");
            Assert.AreEqual(9.0 - f.Beat, f.BeatsToChange, 1e-4);
        }

        [Test]
        public void BeatsToChange_RunsAcrossBarLines()
        {
            DevSong s = PlayFlowFiles.Song("happy-birthday");
            // G from 9 to 15 crosses the bar line at 12; C is next at 15.
            int run = DevTimeline.RunIndexAt(s.Runs, 9.5);
            Assert.AreEqual(2, run);
            Assert.AreEqual(5.5, DevTimeline.BeatsToNextChange(s.Runs, run, 9.5), Tol);
            Assert.AreEqual(6, DevTimeline.WholeBeatsLeft(5.5));
            Assert.AreEqual(6, DevTimeline.WholeBeatsLeft(6.0));
            Assert.AreEqual(1, DevTimeline.WholeBeatsLeft(0.5));
            Assert.AreEqual(0, DevTimeline.WholeBeatsLeft(0.0), "due now (wait mode holds)");
            Assert.AreEqual(0, DevTimeline.WholeBeatsLeft(double.PositiveInfinity));
            Assert.AreEqual(-1, DevTimeline.RunIndexAt(s.Runs, -1.5), "count-in");
            Assert.AreEqual(0, DevTimeline.RunIndexAt(s.Runs, -1.0), "the pickup is the first chord");
            Assert.AreEqual(s.Runs.Length, DevTimeline.RunIndexAt(s.Runs, 24.0), "after the end");
            Assert.AreEqual(double.PositiveInfinity, DevTimeline.BeatsToNextChange(s.Runs, s.Runs.Length - 1, 22.0));
            // Position in the bar: bar lines at multiples of 3 from beat 0, so the pickup is the bar's 3rd beat.
            Assert.AreEqual(2.0, DevTimeline.BeatInBar(-1.0, 3), Tol);
            Assert.AreEqual(0.5, DevTimeline.BeatInBar(12.5, 3), Tol);
        }

        [Test]
        public void PatternCursor_LandsSlotZeroOnEveryBarLine()
        {
            Assert.AreEqual(8.0, DevTimeline.SlotPosition(4.0, 2), Tol);
            Assert.AreEqual(0f, PatternStrip.WrapSlot((float)DevTimeline.SlotPosition(4.0, 2), 8), 1e-5f, "riptide bar 2 starts on slot 0");
            Assert.AreEqual(4f, PatternStrip.WrapSlot((float)DevTimeline.SlotPosition(-1.0, 2), 6), 1e-5f, "HB pickup = the 3rd D");
            Assert.AreEqual(2, DevTimeline.SlotsPerBeat(6, 6, 3));
            Assert.AreEqual(2, DevTimeline.SlotsPerBeat(8, 8, 4));
            Assert.AreEqual(2, DevTimeline.SlotsPerBeat(7, 8, 4), "uneven: falls back to subdivision / 4");
            Assert.AreEqual(1, DevTimeline.SlotsPerBeat(0, 0, 4));
        }

        // ------------------------------------------------------------------ wait mode

        [Test]
        public void WaitMode_HoldsOnEveryChange_UntilTheSimulatedConfirmation()
        {
            DevSong s = PlayFlowFiles.Song("riptide-loop");
            var clock = new DevPlayAlongClock();
            clock.SimulatedConfirmSeconds = 0.5f;
            clock.Start(s, 1f, true);
            PlayFlowFiles.RunUntil(clock, c => c.Waiting, 0.01f, 5f);
            Assert.AreEqual(0.0, clock.Beat, Tol, "holds ON the first chord");
            Assert.AreEqual("Am", clock.Frame.Chord, "the chord to play is the new one");
            Assert.IsTrue(clock.Frame.Waiting);
            Assert.IsFalse(clock.Frame.ChordConfirmed);

            for (int i = 0; i < 30; i++) clock.Tick(0.01f);
            Assert.IsTrue(clock.Frame.Waiting, "0.3 s: still holding");
            Assert.AreEqual(0.0, clock.Frame.Beat, Tol, "Beat does not advance while waiting");
            Assert.AreEqual(0.0, clock.SongSeconds, Tol);

            PlayFlowFiles.RunUntil(clock, c => !c.Waiting, 0.01f, 1f);
            Assert.IsTrue(clock.Frame.ChordConfirmed, "simulated confirmation sets ChordConfirmed");
            Assert.LessOrEqual(clock.Results.ChordsHit, 1);
            clock.Tick(0.1f);
            Assert.Greater(clock.Beat, 0.0, "moves on after the confirmation");

            PlayFlowFiles.RunUntil(clock, c => c.Waiting, 0.01f, 5f);
            Assert.AreEqual(2.0, clock.Beat, Tol, "holds again on G");
            Assert.AreEqual("G", clock.Frame.Chord);
            Assert.IsFalse(clock.Frame.ChordConfirmed, "a new chord starts unconfirmed");
        }

        [Test]
        public void WaitMode_WithoutSimulation_HoldsUntilARealVerdict()
        {
            DevSong s = PlayFlowFiles.Song("riptide-loop");
            var clock = new DevPlayAlongClock();
            clock.SimulateConfirmation = false;
            clock.Start(s, 1f, true);
            PlayFlowFiles.RunUntil(clock, c => c.Waiting, 0.01f, 5f);
            for (int i = 0; i < 100; i++) clock.Tick(0.1f);
            Assert.IsTrue(clock.Waiting, "10 s later: still holding");
            clock.ConfirmChord(12f);
            Assert.IsFalse(clock.Waiting);
            Assert.IsTrue(clock.Frame.ChordConfirmed);
            Assert.AreEqual(1, clock.Results.ChordsHit);
            Assert.AreEqual(12f, clock.Results.MeanTimingMs, 1e-4f);
            Assert.IsFalse(clock.Results.Simulated);
        }

        [Test]
        public void ConfirmChord_IsIgnoredInTheCountIn()
        {
            var clock = new DevPlayAlongClock();
            clock.Start(PlayFlowFiles.Song("riptide-loop"), 1f, true);
            clock.ConfirmChord(0f);
            Assert.AreEqual(0, clock.Results.ChordsHit);
            Assert.IsFalse(clock.Frame.ChordConfirmed);
        }

        // ------------------------------------------------------------------ tempo presets

        [Test]
        public void TempoPreset_ScalesTheBeatRate()
        {
            DevSong s = PlayFlowFiles.Song("riptide-loop");
            float[] presets = { 0.5f, 0.6f, 0.75f, 0.9f, 1.0f };
            for (int i = 0; i < presets.Length; i++)
            {
                var clock = new DevPlayAlongClock();
                clock.Start(s, presets[i], false);
                Assert.AreEqual(100.0 / 60.0 * presets[i], clock.BeatsPerSecond, 1e-5, "preset " + presets[i]);
                Assert.AreEqual(clock.BeatsPerSecond, clock.Frame.BeatsPerSecond, 1e-6);
                for (int k = 0; k < 10; k++) clock.Tick(0.1f);
                Assert.AreEqual(-4.0 + 100.0 / 60.0 * presets[i], clock.Beat, 1e-4, "one second at preset " + presets[i]);
            }
        }

        [Test]
        public void NearestPreset_UsesOnlyTheFlagsList()
        {
            Assert.AreEqual(0.75f, DevTimeline.NearestPreset(0.75f, new[] { 0.5f, 0.6f, 0.75f, 0.9f, 1.0f }), 1e-6f);
            Assert.AreEqual(0.75f, DevTimeline.NearestPreset(0.75f, new[] { 0.5f, 0.75f }), 1e-6f, "judge.json");
            Assert.AreEqual(0.5f, DevTimeline.NearestPreset(0.75f, new[] { 1.0f, 0.5f }), 1e-6f, "a tie goes to the slower");
            Assert.AreEqual(0.9f, DevTimeline.NearestPreset(0.8f, new[] { 0.5f, 0.9f }), 1e-6f);
            Assert.AreEqual(0.75f, DevTimeline.NearestPreset(0.75f, null), 1e-6f);
            Assert.AreEqual(0.6f, DevTimeline.NearestPreset(0.75f, new[] { -1f, 0f, 0.6f }), 1e-6f, "non-positive presets are ignored");
        }

        // ------------------------------------------------------------------ the end and the results

        [Test]
        public void EndsAfterTheLastChord_WithSimulatedResults()
        {
            DevSong s = PlayFlowFiles.Song("riptide-loop");
            var clock = new DevPlayAlongClock();
            clock.Start(s, 1f, false);
            PlayFlowFiles.RunUntil(clock, c => c.State == DevPlayState.Ended, 1f / 60f, 60f);
            Assert.IsFalse(clock.Frame.Active, "inactive after the end: hands rest, HUD hidden");
            Assert.IsNull(clock.Frame.Chord);
            DevPlayResults r = clock.Results;
            Assert.AreEqual(30, r.ChordsTotal);
            Assert.AreEqual(30, r.ChordsHit, "every chord lasts >= 1.2 s, longer than the 0.6 s simulated delay");
            Assert.AreEqual(DevPlayAlongClock.DefaultSimulatedTimingMs, r.MeanTimingMs, 1e-3f);
            Assert.IsTrue(r.Simulated);
            clock.Tick(1f);
            Assert.AreEqual(DevPlayState.Ended, clock.State, "stays ended");
        }

        [Test]
        public void ResultsCountAChordThatEndsUnconfirmedAsMissed()
        {
            DevSong s = PlayFlowFiles.Song("happy-birthday");
            var clock = new DevPlayAlongClock();
            clock.SimulatedConfirmSeconds = 1.0f;   // the one-beat D (0.67 s at 90 bpm) ends first
            clock.Start(s, 1f, false);
            PlayFlowFiles.RunUntil(clock, c => c.State == DevPlayState.Ended, 1f / 60f, 60f);
            Assert.AreEqual(7, clock.Results.ChordsTotal);
            Assert.AreEqual(6, clock.Results.ChordsHit);
        }

        [Test]
        public void WaitMode_CompletesHappyBirthdayAt75Percent()
        {
            DevSong s = PlayFlowFiles.Song("happy-birthday");
            var clock = new DevPlayAlongClock();
            clock.Start(s, 0.75f, true);
            int holds = 0;
            bool wasWaiting = false;
            float t = 0f;
            while (clock.State != DevPlayState.Ended)
            {
                clock.Tick(1f / 60f);
                if (clock.Waiting && !wasWaiting) holds++;
                wasWaiting = clock.Waiting;
                t += 1f / 60f;
                Assert.Less(t, 120f);
            }
            Assert.AreEqual(7, holds, "one hold per chord change, the first chord included");
            Assert.AreEqual(7, clock.Results.ChordsHit);
        }

        [Test]
        public void Frame_SharesThePatternAndRejectsBadSteps()
        {
            DevSong s = PlayFlowFiles.Song("happy-birthday");
            var clock = new DevPlayAlongClock();
            clock.Start(s, 1f, false);
            Assert.AreSame(s.StrumSlots, clock.Frame.StrumSlots, "shared, not copied");
            Assert.AreEqual(2, clock.Frame.SlotsPerBeat);
            Assert.AreEqual(3, clock.Frame.BeatsPerBar);
            double before = clock.Beat;
            clock.Tick(float.NaN);
            clock.Tick(-1f);
            clock.Tick(float.PositiveInfinity);
            Assert.AreEqual(before, clock.Beat, Tol);
            var idle = new DevPlayAlongClock();
            Assert.IsFalse(idle.Start(null, 1f, true));
            Assert.IsFalse(idle.Frame.Active);
            Assert.AreEqual(double.PositiveInfinity, idle.Frame.BeatsToChange);
        }

        // ------------------------------------------------------------------ dev chord shapes vs the ghost-hand poses

        static readonly string[] DistalBones = { "thumb_distal", "index_distal", "middle_distal", "ring_distal", "pinky_distal" };

        static Vector3 PoseFingertip(string poseJson, int digit)
        {
            object root;
            string error;
            Assert.IsTrue(DevJson.TryParse(poseJson, out root, out error), error);
            List<object> tips;
            Assert.IsTrue(DevJson.TryGetArray(root, "fingertips", out tips), "fingertips[]");
            for (int i = 0; i < tips.Count; i++)
            {
                string bone;
                if (!DevJson.TryGetString(tips[i], "bone", out bone) || bone != DistalBones[digit]) continue;
                Dictionary<string, object> p;
                Assert.IsTrue(DevJson.TryGetObject(tips[i], "positionMm", out p));
                double x, y, z;
                bool okX = DevJson.TryGetNumber(p, "x", out x);
                bool okY = DevJson.TryGetNumber(p, "y", out y);
                bool okZ = DevJson.TryGetNumber(p, "z", out z);
                Assert.IsTrue(okX && okY && okZ, "positionMm has x, y and z");
                return new Vector3((float)x, (float)y, (float)z);
            }
            Assert.Fail("no fingertip for " + DistalBones[digit]);
            return Vector3.zero;
        }

        // One test per chord (not [Values]: the offline runner calls tests without arguments).
        [Test] public void DevShape_G_MatchesItsPose_Within3mm() { CheckShapeAgainstPose("G"); }
        [Test] public void DevShape_C_MatchesItsPose_Within3mm() { CheckShapeAgainstPose("C"); }
        [Test] public void DevShape_D_MatchesItsPose_Within3mm() { CheckShapeAgainstPose("D"); }
        [Test] public void DevShape_Em_MatchesItsPose_Within3mm() { CheckShapeAgainstPose("Em"); }
        [Test] public void DevShape_Am_MatchesItsPose_Within3mm() { CheckShapeAgainstPose("Am"); }

        static void CheckShapeAgainstPose(string chord)
        {
            NeckLayout layout = DevNeckLayout.Create();
            var dots = new FingertipDot[FloatingNeck.MaxDots];
            var marks = new StringMark[NeckLayout.StringCount];
            int n = DevNeckLayout.ChordShape(layout, chord, dots, marks);
            Assert.Greater(n, 0, chord + " has a dev shape");
            string json = PlayFlowFiles.PoseText(chord);

            var used = new bool[5];
            for (int i = 0; i < n; i++)
            {
                int finger = dots[i].finger;
                Assert.That(finger, Is.InRange(1, 4), chord + " dot " + i + " has a finger");
                used[finger] = true;
                Vector3 tip = PoseFingertip(json, finger);
                var dot = new Vector3(dots[i].xMm, DevNeckLayout.StringHeightMm, dots[i].zMm);
                float apart = Vector3.Distance(dot, tip);
                Assert.LessOrEqual(apart, 3f, chord + ": finger " + finger + " on string " + dots[i].stringNumber + " is " + apart + " mm from the pose fingertip " + tip);
            }
            // A finger the shape does not use must not be pressing in the pose (lifted or curled away from the strings).
            for (int finger = 1; finger <= 4; finger++)
            {
                if (used[finger]) continue;
                Vector3 tip = PoseFingertip(json, finger);
                Assert.Greater(Mathf.Abs(tip.y - DevNeckLayout.StringHeightMm), 3f, chord + ": unused finger " + finger + " presses in the pose at " + tip);
            }
        }

        [Test]
        public void DevShapes_MarksFollowAppendixB2()
        {
            NeckLayout layout = DevNeckLayout.Create();
            var dots = new FingertipDot[FloatingNeck.MaxDots];
            var marks = new StringMark[NeckLayout.StringCount];
            // marks[i] is string i + 1 (high E first).
            Assert.AreEqual(3, DevNeckLayout.ChordShape(layout, "C", dots, marks));
            CollectionAssert.AreEqual(new[] { StringMark.Open, StringMark.None, StringMark.Open, StringMark.None, StringMark.None, StringMark.Muted }, marks, "C x32010");
            Assert.AreEqual(3, DevNeckLayout.ChordShape(layout, "D", dots, marks));
            CollectionAssert.AreEqual(new[] { StringMark.None, StringMark.None, StringMark.None, StringMark.Open, StringMark.Muted, StringMark.Muted }, marks, "D xx0232");
            Assert.AreEqual(2, DevNeckLayout.ChordShape(layout, "Em", dots, marks));
            CollectionAssert.AreEqual(new[] { StringMark.Open, StringMark.Open, StringMark.Open, StringMark.None, StringMark.None, StringMark.Open }, marks, "Em 022000");
            Assert.AreEqual(3, DevNeckLayout.ChordShape(layout, "Am", dots, marks));
            CollectionAssert.AreEqual(new[] { StringMark.Open, StringMark.None, StringMark.None, StringMark.None, StringMark.Open, StringMark.Muted }, marks, "Am x02210");
            Assert.AreEqual(3, DevNeckLayout.ChordShape(layout, "G", dots, marks));
            Assert.AreEqual(-1, DevNeckLayout.ChordShape(layout, "F", dots, marks), "no dev shape");
            Assert.AreEqual(6, DevNeckLayout.BassString("G"));
            Assert.AreEqual(5, DevNeckLayout.BassString("C"));
            Assert.AreEqual(4, DevNeckLayout.BassString("D"));
            Assert.AreEqual(6, DevNeckLayout.BassString("Em"));
            Assert.AreEqual(5, DevNeckLayout.BassString("Am"));
            Assert.AreEqual(0, DevNeckLayout.BassString(null));
        }

        // ------------------------------------------------------------------ the presenter's pure helper

        [Test]
        public void ToLyricLine_KeepsAtOnlyOnLinesWithWords()
        {
            DevSong hb = PlayFlowFiles.Song("happy-birthday");
            LyricLine words = PlayAlongPresenter.ToLyricLine(hb.Lines[0]);
            Assert.AreEqual("Happy birthday to you", words.Lyric);
            Assert.AreEqual(2, words.ChordCount);
            Assert.AreEqual(18, words.Chords[1].At);
            Assert.AreEqual(6f, words.TotalBeats, 1e-5f);

            DevSong rip = PlayFlowFiles.Song("riptide-loop");
            LyricLine chordsOnly = PlayAlongPresenter.ToLyricLine(rip.Lines[0]);
            Assert.IsFalse(chordsOnly.HasLyric);
            Assert.AreEqual(3, chordsOnly.ChordCount);
            Assert.AreEqual(-1, chordsOnly.Chords[0].At);
            Assert.AreEqual(8f, chordsOnly.TotalBeats, 1e-5f);
        }
    }

    /// <summary>Builds the real neck and play HUD: needs the engine (Unity runs it; mono-tests reports ENGINE).</summary>
    public class PlayFlowEngineTests
    {
        [Test]
        public void Presenter_FollowsTheClock_OnTheRealHudAndNeck()
        {
            var root = new GameObject("PlayFlowEngineTests");
            try
            {
                var head = new GameObject("Head");
                head.transform.SetParent(root.transform, false);
                FloatingNeck neck = FloatingNeck.Create(root.transform);
                Assert.IsTrue(neck.Build(DevNeckLayout.Create()));
                neck.PlaceFloating(head.transform);
                PlayAlongPresenter presenter = PlayAlongPresenter.Create(root.transform, head.transform, neck);

                var clock = new DevPlayAlongClock();
                Assert.IsTrue(clock.Start(PlayFlowFiles.Song("riptide-loop"), 0.75f, true));
                presenter.PresentClock(clock, 0f);
                Assert.AreEqual("Am", presenter.Card.CurrentChord, "count-in: the first chord is shown");
                Assert.AreEqual(4, presenter.Card.CountIn.BeatsLeft);
                Assert.AreEqual("Am", presenter.ShownShape);
                Assert.AreEqual(3, neck.DotCount, "Am: three dots");

                while (!clock.Waiting) { clock.Tick(1f / 60f); presenter.PresentClock(clock, 1f / 60f); }
                Assert.IsTrue(presenter.Card.Waiting);
                Assert.AreEqual(0, presenter.Card.CountIn.BeatsLeft);
                while (clock.RunIndex < 1 || clock.Waiting == false) { clock.Tick(1f / 60f); presenter.PresentClock(clock, 1f / 60f); }
                Assert.AreEqual("G", presenter.Card.CurrentChord);
                Assert.AreEqual("G", presenter.ShownShape);
                Assert.AreEqual(3, neck.DotCount, "G: three dots");
                Assert.AreEqual(8, presenter.Card.Pattern.SlotCount);
                Assert.Greater(presenter.Rings.ActiveChanges, 0, "the landed ring stays while waiting");

                while (clock.State != DevPlayState.Ended) { clock.Tick(0.05f); presenter.PresentClock(clock, 0.05f); }
                Assert.IsTrue(presenter.ShowingResults);
                Assert.IsTrue(presenter.ResultsCard.IsShowing);
                Assert.AreEqual(string.Empty, presenter.Card.CurrentChord ?? string.Empty, "card cleared at the end");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
