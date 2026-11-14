// PlayFlowTests: the adapter from Core's play-along (Runtime/Practice/CorePlayAlong) to PracticeFrame, its pure helpers
// (PlayAlongMath, PlayAlongPresenter's static helpers), and Core's chord shapes against the ghost-hand poses. Core's own
// behaviour (timeline maths, wait mode, escape hatch, scoring) is Person 2's and tested in core-tests; these tests only pin
// what the adapter adds: the count-in, the beat convention, the next DIFFERENT chord, confirmation, simulated strums,
// tempo presets and the results mapping.
//
// Everything except PlayFlowEngineTests is pure and runs offline (mono-tests.sh) as well as in Unity. Song and pose files
// are read from this checkout's StreamingAssets, found from this file's own path (CallerFilePath), so no engine call is
// needed; in Unity the compiler's relative path resolves against the project folder (the editor's working directory).
// UGALog is diverted to a list in every test, so nothing reaches Debug.Log.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Core.Songs;
using UntitledGuitarApp.Runtime.Hud.Play;
using UntitledGuitarApp.Runtime.Logging;
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Practice;
using FingertipDot = UntitledGuitarApp.Runtime.Neck.FingertipDot;

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

        public static Song Song(string id)
        {
            string folder = Path.Combine(StreamingAssets, CorePlayAlong.SongsFolderName);
            if (!Directory.Exists(folder)) Assert.Inconclusive("songs folder not found: " + folder);
            Song song;
            string error;
            Assert.IsTrue(CorePlayAlong.TryLoadSongFile(CorePlayAlong.SongPath(folder, id), out song, out error), error);
            return song;
        }

        public static string PoseText(string chord)
        {
            string path = Path.Combine(StreamingAssets, "poses", "fretting-" + chord + ".pose.json");
            if (!File.Exists(path)) Assert.Inconclusive("pose file not found: " + path);
            return File.ReadAllText(path);
        }

        public static CorePlayAlong Started(string id, float preset, bool wait)
        {
            var play = new CorePlayAlong();
            Assert.IsTrue(play.Start(Song(id), preset, wait, null), "start " + id);
            return play;
        }

        /// <summary>Ticks in fixed steps until <paramref name="until"/> is true; fails after <paramref name="maxSeconds"/>.</summary>
        public static void RunUntil(CorePlayAlong play, Func<CorePlayAlong, bool> until, float step, float maxSeconds)
        {
            float t = 0f;
            while (!until(play))
            {
                Assert.Less(t, maxSeconds, "not reached in " + maxSeconds + " s (beat " + play.Frame.Beat + ", phase " + play.Phase + ")");
                play.Tick(step);
                t += step;
            }
        }
    }

    public class PlayFlowTests
    {
        const double Tol = 1e-6;
        Action<LogType, string> _savedOutput;
        readonly List<string> _log = new List<string>();

        [SetUp]
        public void DivertLog()
        {
            _savedOutput = UGALog.Output;
            _log.Clear();
            UGALog.Output = (type, line) => { lock (_log) _log.Add(line); };
        }

        [TearDown]
        public void RestoreLog()
        {
            UGALog.Output = _savedOutput;
        }

        // ------------------------------------------------------------------ loading through Core

        [Test]
        public void Load_ValidatesTheThreeSongsWithCore()
        {
            Assert.AreEqual(30, PlayFlowFiles.Started("riptide-loop", 1f, false).Timeline.Spans.Count);
            Assert.AreEqual(9, PlayFlowFiles.Started("happy-birthday", 1f, false).Timeline.Spans.Count, "one span per chord of the file");
            Assert.AreEqual(10, PlayFlowFiles.Started("stand-by-me-loop", 1f, false).Timeline.Spans.Count);
            Song song;
            string error;
            Assert.IsFalse(CorePlayAlong.TryLoadSongFile(Path.Combine(PlayFlowFiles.StreamingAssets, "songs", "no-such-song.song.json"), out song, out error));
            Assert.IsNull(song);
            StringAssert.Contains("cannot read", error);
            Assert.IsFalse(new CorePlayAlong().Start(null, 1f, true, null));
        }

        // ------------------------------------------------------------------ count-in and the beat convention

        [Test]
        public void CountIn_IsOneBarOnNegativeBeats_ThenCoreStarts()
        {
            CorePlayAlong play = PlayFlowFiles.Started("riptide-loop", 1f, false);
            play.SimulateStrums = false;
            PracticeFrame f = play.Frame;
            Assert.AreEqual(PlayAlongPhase.CountIn, play.Phase);
            Assert.IsTrue(f.Active);
            Assert.AreEqual(-4.0, f.Beat, Tol);
            Assert.IsNull(f.Chord, "no chord sounds in the count-in");
            Assert.AreEqual("Am", f.NextChord);
            Assert.AreEqual(4.0, f.BeatsToChange, Tol);
            Assert.AreEqual(100f / 60f, f.BeatsPerSecond, 1e-5f);

            for (int i = 0; i < 239; i++) play.Tick(0.01f);   // 100 bpm = 0.6 s a beat: the count-in lasts 2.4 s
            Assert.AreEqual(PlayAlongPhase.CountIn, play.Phase, "still counting at 2.39 s");
            Assert.AreEqual(-0.01 / 0.6, play.Frame.Beat, 1e-4);
            play.Tick(0.02f);
            Assert.AreEqual(PlayAlongPhase.Playing, play.Phase);
            Assert.AreEqual("Am", play.Frame.Chord);
            Assert.AreEqual(0.01 / 0.6, play.Frame.Beat, 1e-4, "Core started exactly when the count-in ended");
            Assert.AreEqual("G", play.Frame.NextChord);
        }

        [Test]
        public void Pickup_IsANegativeFrameBeat_HappyBirthday()
        {
            CorePlayAlong play = PlayFlowFiles.Started("happy-birthday", 1f, true);
            Assert.AreEqual(-4.0, play.Frame.Beat, Tol, "one 3/4 bar before the pickup");
            Assert.AreEqual(3.0, play.Frame.BeatsToChange, Tol);
            Assert.AreEqual("G", play.Frame.NextChord);
            Assert.AreEqual(3, play.Frame.BeatsPerBar);
            PlayFlowFiles.RunUntil(play, p => p.Waiting, 0.01f, 5f);
            Assert.AreEqual(-1.0, play.Frame.Beat, Tol, "Core beat 0 (the pickup) is frame beat -1");
            Assert.AreEqual("G", play.Frame.Chord);
        }

        // ------------------------------------------------------------------ next DIFFERENT chord, beats to change

        [Test]
        public void NextChord_IsTheNextDifferentChord_NotCoresNextSpan()
        {
            CorePlayAlong play = PlayFlowFiles.Started("happy-birthday", 1f, false);
            play.SimulateStrums = false;
            PlayFlowFiles.RunUntil(play, p => p.Frame.Beat >= 3.5, 0.01f, 10f);
            PracticeFrame f = play.Frame;
            Assert.AreEqual(1, play.CurrentSpan, "span 1 is the first D");
            Assert.AreEqual("D", f.Chord);
            Assert.AreEqual("D", play.Timeline.Spans[2].Chord, "Core's next span is the same D across the line break");
            Assert.AreEqual("G", f.NextChord, "the frame's next chord is the next DIFFERENT one");
            Assert.AreEqual(9.0 - f.Beat, f.BeatsToChange, 1e-4, "G starts at frame beat 9");
        }

        [Test]
        public void BeatsToChange_RunsAcrossBarLinesAndSameChordSpans()
        {
            CorePlayAlong play = PlayFlowFiles.Started("happy-birthday", 1f, false);
            play.SimulateStrums = false;
            PlayFlowFiles.RunUntil(play, p => p.Frame.Beat >= 9.5, 0.01f, 15f);
            PracticeFrame f = play.Frame;
            Assert.AreEqual("G", f.Chord);
            Assert.AreEqual("C", f.NextChord);
            Assert.AreEqual(15.0 - f.Beat, f.BeatsToChange, 1e-4, "through the bar line at 12 and the G-to-G span break at 11");
            PlayFlowFiles.RunUntil(play, p => p.Frame.Beat >= 22.0, 0.01f, 15f);
            Assert.AreEqual("G", play.Frame.Chord);
            Assert.IsNull(play.Frame.NextChord, "last chord");
            Assert.AreEqual(double.PositiveInfinity, play.Frame.BeatsToChange);
        }

        // ------------------------------------------------------------------ waiting and confirmation

        [Test]
        public void WaitMode_HoldsOnTheChange_UntilTheSimulatedStrum()
        {
            CorePlayAlong play = PlayFlowFiles.Started("riptide-loop", 1f, true);
            play.SimulatedHoldSec = 0.5f;
            PlayFlowFiles.RunUntil(play, p => p.Waiting, 0.01f, 5f);
            Assert.AreEqual(0.0, play.Frame.Beat, Tol, "holds ON the first chord");
            Assert.AreEqual("Am", play.Frame.Chord);
            Assert.AreEqual("G", play.Frame.NextChord);
            Assert.AreEqual(2.0, play.Frame.BeatsToChange, Tol);
            Assert.IsTrue(play.Frame.Waiting);
            Assert.IsFalse(play.Frame.ChordConfirmed);

            for (int i = 0; i < 30; i++) play.Tick(0.01f);
            Assert.IsTrue(play.Frame.Waiting, "0.3 s: still holding");
            Assert.AreEqual(0.0, play.Frame.Beat, Tol, "Beat does not advance while waiting");
            Assert.AreEqual(0, play.SimulatedStrums);

            PlayFlowFiles.RunUntil(play, p => !p.Waiting, 0.01f, 1f);
            Assert.AreEqual(1, play.SimulatedStrums);
            Assert.IsTrue(play.Frame.ChordConfirmed, "the accepted strum confirms the chord");
            play.Tick(0.1f);
            Assert.Greater(play.Frame.Beat, 0.0, "moves on");

            PlayFlowFiles.RunUntil(play, p => p.Waiting, 0.01f, 5f);
            Assert.AreEqual(2.0, play.Frame.Beat, Tol, "holds again on G");
            Assert.AreEqual("G", play.Frame.Chord);
            Assert.IsFalse(play.Frame.ChordConfirmed, "a new chord starts unconfirmed");
        }

        [Test]
        public void WaitMode_AlsoHoldsOnASameChordSpan_AsCoreDoes()
        {
            CorePlayAlong play = PlayFlowFiles.Started("happy-birthday", 1f, true);
            PlayFlowFiles.RunUntil(play, p => p.Waiting && p.CurrentSpan == 2, 1f / 60f, 20f);
            Assert.AreEqual(5.0, play.Frame.Beat, Tol, "the D of line 2");
            Assert.AreEqual("D", play.Frame.Chord);
            Assert.AreEqual("G", play.Frame.NextChord, "next different chord, even while holding");
            Assert.AreEqual(4.0, play.Frame.BeatsToChange, Tol);
        }

        [Test]
        public void RealStrums_PassThrough_AndOnlyTheRightChordReleasesTheHold()
        {
            CorePlayAlong play = PlayFlowFiles.Started("riptide-loop", 1f, true);
            play.SimulateStrums = false;
            PlayFlowFiles.RunUntil(play, p => p.Waiting, 0.01f, 5f);
            for (int i = 0; i < 50; i++) play.Tick(0.1f);
            Assert.IsTrue(play.Waiting, "5 s later, no simulation: still holding");
            play.OnStrum("Em");
            Assert.IsTrue(play.Waiting, "a wrong chord does not release the hold");
            Assert.IsFalse(play.Frame.ChordConfirmed);
            play.OnStrum("Am");
            Assert.IsFalse(play.Waiting);
            Assert.IsTrue(play.Frame.ChordConfirmed);
            Assert.AreEqual(0, play.SimulatedStrums);
        }

        [Test]
        public void SimulatedStrums_AreLogged()
        {
            CorePlayAlong play = PlayFlowFiles.Started("riptide-loop", 1f, true);
            PlayFlowFiles.RunUntil(play, p => p.SimulatedStrums == 1, 0.01f, 5f);
            bool found = false;
            lock (_log)
            {
                foreach (string line in _log) found |= line.Contains("play_strum_simulated") && line.Contains("Am");
            }
            Assert.IsTrue(found, "a play_strum_simulated line naming Am; got: " + string.Join(" | ", _log));
        }

        // ------------------------------------------------------------------ tempo presets

        [Test]
        public void TempoPreset_ScalesTheBeatRate()
        {
            float[] presets = { 0.5f, 0.6f, 0.75f, 0.9f, 1.0f };
            for (int i = 0; i < presets.Length; i++)
            {
                CorePlayAlong play = PlayFlowFiles.Started("riptide-loop", presets[i], false);
                Assert.AreEqual(100.0 / 60.0 * presets[i], play.Frame.BeatsPerSecond, 1e-5, "preset " + presets[i]);
                for (int k = 0; k < 10; k++) play.Tick(0.1f);
                Assert.AreEqual(-4.0 + 100.0 / 60.0 * presets[i], play.Frame.Beat, 1e-4, "one second of count-in at " + presets[i]);
            }
        }

        [Test]
        public void TempoPreset_AlsoScalesCoresPart()
        {
            CorePlayAlong play = PlayFlowFiles.Started("riptide-loop", 0.5f, false);
            play.SimulateStrums = false;
            PlayFlowFiles.RunUntil(play, p => p.Phase == PlayAlongPhase.Playing, 0.01f, 10f);
            double before = play.Frame.Beat;
            for (int k = 0; k < 100; k++) play.Tick(0.01f);
            Assert.AreEqual(before + 100.0 / 60.0 * 0.5, play.Frame.Beat, 1e-3, "one second at 50% inside Core's session");
        }

        [Test]
        public void NearestPreset_UsesOnlyTheFlagsList()
        {
            Assert.AreEqual(0.75f, PlayAlongMath.NearestPreset(0.75f, new[] { 0.5f, 0.6f, 0.75f, 0.9f, 1.0f }), 1e-6f);
            Assert.AreEqual(0.75f, PlayAlongMath.NearestPreset(0.75f, new[] { 0.5f, 0.75f }), 1e-6f, "judge.json");
            Assert.AreEqual(0.5f, PlayAlongMath.NearestPreset(0.75f, new[] { 1.0f, 0.5f }), 1e-6f, "a tie goes to the slower");
            Assert.AreEqual(0.6f, PlayAlongMath.NearestPreset(0.75f, new[] { -1f, 0f, 0.6f }), 1e-6f);
            Assert.AreEqual(0.75f, PlayAlongMath.NearestPreset(0.75f, null), 1e-6f);
        }

        // ------------------------------------------------------------------ the end and the results

        [Test]
        public void WaitMode_CompletesHappyBirthdayAt75Percent_WithSimulatedResults()
        {
            CorePlayAlong play = PlayFlowFiles.Started("happy-birthday", 0.75f, true);
            int holds = 0;
            bool was = false;
            PlayFlowFiles.RunUntil(play, p =>
            {
                if (p.Waiting && !was) holds++;
                was = p.Waiting;
                return p.Phase == PlayAlongPhase.Ended;
            }, 1f / 60f, 120f);
            Assert.AreEqual(9, holds, "Core holds at every span");
            Assert.IsFalse(play.Frame.Active, "inactive after the end: hands rest, HUD hidden");
            PlayAlongResults r = play.Results;
            Assert.AreEqual(9, r.ChordsTotal);
            Assert.AreEqual(9, r.ChordsHit);
            Assert.AreEqual(0, r.ChordsMissed + r.ChordsSkipped);
            Assert.IsTrue(r.Simulated);
            Assert.IsTrue(float.IsNaN(r.MeanTimingMs), "simulated strums do not measure timing");
            play.Tick(1f);
            Assert.AreEqual(PlayAlongPhase.Ended, play.Phase, "stays ended");
        }

        [Test]
        public void FreeRunning_SimulatedStrumsHitEveryChord()
        {
            CorePlayAlong play = PlayFlowFiles.Started("riptide-loop", 1f, false);
            PlayFlowFiles.RunUntil(play, p => p.Phase == PlayAlongPhase.Ended, 1f / 60f, 60f);
            Assert.AreEqual(30, play.Results.ChordsTotal);
            Assert.AreEqual(30, play.Results.ChordsHit);
            Assert.AreEqual(30, play.SimulatedStrums);
        }

        [Test]
        public void RealStrumTiming_IsSignedMilliseconds()
        {
            CorePlayAlong play = PlayFlowFiles.Started("riptide-loop", 1f, false);
            play.SimulateStrums = false;
            PlayFlowFiles.RunUntil(play, p => p.Phase == PlayAlongPhase.Playing && p.Frame.Beat >= 0.25, 0.001f, 5f);
            play.OnStrum("Am");   // 0.25 beat = 150 ms late at 100 bpm
            PlayFlowFiles.RunUntil(play, p => p.Phase == PlayAlongPhase.Ended, 0.05f, 60f);
            Assert.AreEqual(1, play.Results.ChordsHit);
            Assert.AreEqual(29, play.Results.ChordsMissed);
            Assert.AreEqual(150f, play.Results.MeanTimingMs, 2f);
            Assert.IsFalse(play.Results.Simulated);
        }

        [Test]
        public void Frame_SharesThePattern_AndRejectsBadSteps()
        {
            CorePlayAlong play = PlayFlowFiles.Started("happy-birthday", 1f, false);
            string[] slots = play.Frame.StrumSlots;
            Assert.AreEqual(6, slots.Length);
            Assert.AreEqual(2, play.Frame.SlotsPerBeat, "6 slots per 3/4 bar (Core's PatternClock: subdivision = slots per bar)");
            double before = play.Frame.Beat;
            play.Tick(float.NaN);
            play.Tick(-1f);
            play.Tick(float.PositiveInfinity);
            Assert.AreEqual(before, play.Frame.Beat, Tol);
            play.Tick(0.1f);
            Assert.AreSame(slots, play.Frame.StrumSlots, "shared, not copied per frame");
            var idle = new CorePlayAlong();
            Assert.IsFalse(idle.Frame.Active);
            Assert.AreEqual(double.PositiveInfinity, idle.Frame.BeatsToChange);
        }

        // ------------------------------------------------------------------ pure helpers

        [Test]
        public void PlayAlongMath_Conversions()
        {
            Song hb = PlayFlowFiles.Song("happy-birthday");
            int[] next = PlayAlongMath.NextDifferent(new SongTimeline(hb, 1.0).Spans);
            CollectionAssert.AreEqual(new[] { 1, 3, 3, 5, 5, 6, 7, 8, -1 }, next, "G D D G G C G D G");
            Assert.AreEqual(-1.0, PlayAlongMath.FrameBeat(0.0, 1.0), Tol);
            Assert.AreEqual(2, PlayAlongMath.SlotsPerBeat(6, 3));
            Assert.AreEqual(2, PlayAlongMath.SlotsPerBeat(8, 4));
            Assert.AreEqual(1, PlayAlongMath.SlotsPerBeat(7, 4));
            Assert.AreEqual(1, PlayAlongMath.SlotsPerBeat(0, 4));
            Assert.AreEqual(4, PlayAlongMath.CountInBeatsLeft(-4.0, 0.0));
            Assert.AreEqual(4, PlayAlongMath.CountInBeatsLeft(-3.5, 0.0));
            Assert.AreEqual(3, PlayAlongMath.CountInBeatsLeft(-3.0, 0.0), "changes exactly on the beat");
            Assert.AreEqual(0, PlayAlongMath.CountInBeatsLeft(0.0, 0.0));
            Assert.AreEqual(6, PlayAlongMath.WholeBeatsLeft(5.5));
            Assert.AreEqual(1, PlayAlongMath.WholeBeatsLeft(0.5));
            Assert.AreEqual(0, PlayAlongMath.WholeBeatsLeft(0.0), "due now (wait mode holds)");
            Assert.AreEqual(0, PlayAlongMath.WholeBeatsLeft(double.PositiveInfinity));
            Assert.AreEqual(2.0, PlayAlongMath.BeatInBar(-1.0, 3), Tol, "the pickup is the bar's 3rd beat");
            Assert.AreEqual(0f, PatternStrip.WrapSlot((float)PlayAlongMath.SlotPosition(4.0, 2), 8), 1e-5f, "slot 0 on the bar line");
            Assert.AreEqual(4f, PatternStrip.WrapSlot((float)PlayAlongMath.SlotPosition(-1.0, 2), 6), 1e-5f);
        }

        [Test]
        public void Presenter_BuildsLinesInFrameBeats()
        {
            LyricLine[] lines;
            double[] starts;
            double first, end;
            PlayAlongPresenter.BuildLines(PlayFlowFiles.Song("happy-birthday"), out lines, out starts, out first, out end);
            Assert.AreEqual(4, lines.Length);
            CollectionAssert.AreEqual(new[] { -1.0, 5.0, 11.0, 17.0 }, starts);
            Assert.AreEqual(-1.0, first, Tol);
            Assert.AreEqual(24.0, end, Tol);
            Assert.AreEqual("Happy birthday to you", lines[0].Lyric);
            Assert.AreEqual(18, lines[0].Chords[1].At);
            Assert.AreEqual(2, PlayAlongPresenter.LineIndexAt(starts, 12.0));
            Assert.AreEqual(0, PlayAlongPresenter.LineIndexAt(starts, -3.0), "count-in shows the first line");

            PlayAlongPresenter.BuildLines(PlayFlowFiles.Song("riptide-loop"), out lines, out starts, out first, out end);
            Assert.AreEqual(10, lines.Length);
            Assert.IsFalse(lines[0].HasLyric);
            Assert.AreEqual(-1, lines[0].Chords[0].At, "chords-only: no at");
            Assert.AreEqual(80.0, end, Tol);
            Assert.AreEqual(6, PlayAlongPresenter.BassString("G"));
            Assert.AreEqual(5, PlayAlongPresenter.BassString("C"));
            Assert.AreEqual(4, PlayAlongPresenter.BassString("D"));
            Assert.AreEqual(5, PlayAlongPresenter.BassString("Am"));
            Assert.AreEqual(0, PlayAlongPresenter.BassString("F#"));
        }

        // ------------------------------------------------------------------ Core's chord shapes vs the ghost-hand poses

        static readonly string[] DistalBones = { "thumb_distal", "index_distal", "middle_distal", "ring_distal", "pinky_distal" };

        // The pose files are machine-written (art/blender/export_poses.py); the top-level "fingertips" list comes before "bind".
        static Vector3 PoseFingertip(string json, int digit)
        {
            int from = json.IndexOf("\"fingertips\"", StringComparison.Ordinal);
            Assert.GreaterOrEqual(from, 0, "fingertips list");
            var re = new Regex("\"bone\":\\s*\"" + DistalBones[digit] + "\",\\s*\"positionMm\":\\s*\\{\\s*\"x\":\\s*(-?[0-9.]+),\\s*\"y\":\\s*(-?[0-9.]+),\\s*\"z\":\\s*(-?[0-9.]+)");
            Match m = re.Match(json, from);
            Assert.IsTrue(m.Success, "fingertip of " + DistalBones[digit]);
            return new Vector3(float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        // One test per chord (no [Values]: the offline runner calls tests without arguments).
        [Test] public void CoreShape_G_MatchesItsPose_Within3mm() { CheckShapeAgainstPose("G"); }
        [Test] public void CoreShape_C_MatchesItsPose_Within3mm() { CheckShapeAgainstPose("C"); }
        [Test] public void CoreShape_D_MatchesItsPose_Within3mm() { CheckShapeAgainstPose("D"); }
        [Test] public void CoreShape_Em_MatchesItsPose_Within3mm() { CheckShapeAgainstPose("Em"); }
        [Test] public void CoreShape_Am_MatchesItsPose_Within3mm() { CheckShapeAgainstPose("Am"); }

        static void CheckShapeAgainstPose(string chord)
        {
            var dots = new FingertipDot[FloatingNeck.MaxDots];
            var marks = new StringMark[NeckLayout.StringCount];
            int n = CoreNeckLayout.ChordMarks(CoreNeckLayout.DefaultGeometry, chord, dots, marks);
            Assert.Greater(n, 0, chord + " has a shape in Core");
            string json = PlayFlowFiles.PoseText(chord);
            var used = new bool[5];
            for (int i = 0; i < n; i++)
            {
                int finger = dots[i].finger;
                Assert.That(finger, Is.InRange(1, 4), chord + " dot " + i + " has a finger");
                used[finger] = true;
                Vector3 tip = PoseFingertip(json, finger);
                var dot = new Vector3(dots[i].xMm, CoreNeckLayout.StringHeightMm, dots[i].zMm);
                float apart = Vector3.Distance(dot, tip);
                Assert.LessOrEqual(apart, 3f, chord + ": finger " + finger + " on string " + dots[i].stringNumber + " is " + apart + " mm from the pose fingertip " + tip);
            }
            for (int finger = 1; finger <= 4; finger++)
            {
                if (used[finger]) continue;
                Vector3 tip = PoseFingertip(json, finger);
                Assert.Greater(Mathf.Abs(tip.y - CoreNeckLayout.StringHeightMm), 3f, chord + ": unused finger " + finger + " presses in the pose at " + tip);
            }
        }
    }

    /// <summary>Builds the real neck and play HUD: needs the engine (Unity runs it; mono-tests reports ENGINE).</summary>
    public class PlayFlowEngineTests
    {
        [Test]
        public void Presenter_FollowsCoresSession_OnTheRealHudAndNeck()
        {
            Action<LogType, string> saved = UGALog.Output;
            UGALog.Output = (type, line) => { };
            var root = new GameObject("PlayFlowEngineTests");
            try
            {
                var head = new GameObject("Head");
                head.transform.SetParent(root.transform, false);
                FloatingNeck neck = FloatingNeck.Create(root.transform);
                Assert.IsTrue(neck.Build(CoreNeckLayout.Create(CoreNeckLayout.DefaultGeometry)));
                neck.PlaceFloating(head.transform);
                PlayAlongPresenter presenter = PlayAlongPresenter.Create(root.transform, head.transform, neck);

                CorePlayAlong play = PlayFlowFiles.Started("riptide-loop", 0.75f, true);
                presenter.PresentPlayAlong(play, 0f);
                Assert.AreEqual("Am", presenter.Card.CurrentChord, "count-in: the first chord is shown");
                Assert.AreEqual(4, presenter.Card.CountIn.BeatsLeft);
                Assert.AreEqual("Am", presenter.ShownShape);
                Assert.AreEqual(3, neck.DotCount, "Am: three dots");

                while (!play.Waiting) { play.Tick(1f / 60f); presenter.PresentPlayAlong(play, 1f / 60f); }
                Assert.IsTrue(presenter.Card.Waiting);
                Assert.AreEqual(0, presenter.Card.CountIn.BeatsLeft);
                while (!(play.Waiting && play.CurrentSpan == 1)) { play.Tick(1f / 60f); presenter.PresentPlayAlong(play, 1f / 60f); }
                Assert.AreEqual("G", presenter.Card.CurrentChord);
                Assert.AreEqual("G", presenter.ShownShape);
                Assert.AreEqual(3, neck.DotCount, "G: three dots");
                Assert.AreEqual(8, presenter.Card.Pattern.SlotCount);
                Assert.Greater(presenter.Rings.ActiveChanges, 0, "the landed ring stays while waiting");

                while (play.Phase != PlayAlongPhase.Ended) { play.Tick(0.05f); presenter.PresentPlayAlong(play, 0.05f); }
                Assert.IsTrue(presenter.ShowingResults);
                Assert.IsTrue(presenter.ResultsCard.IsShowing);
                Assert.AreEqual(string.Empty, presenter.Card.CurrentChord ?? string.Empty, "card cleared at the end");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UGALog.Output = saved;
            }
        }
    }
}
