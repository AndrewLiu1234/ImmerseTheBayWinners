using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime;

namespace UntitledGuitarApp.Tests.EditMode
{
    public class DemoStateTests
    {
        static DemoState Make(int songs = 3, int chords = 5, int tempos = 5, int startTempo = 2)
        {
            return new DemoState(songs, chords, tempos, startTempo);
        }

        [Test]
        public void StartsIdle_AtGenerationZero()
        {
            DemoState s = Make();
            Assert.AreEqual(DemoMode.Idle, s.Mode);
            Assert.AreEqual(0, s.Generation);
            Assert.AreEqual(2, s.TempoIndex);
        }

        [Test]
        public void EnteringAMode_Restarts_AndReenteringRestartsAgain()
        {
            DemoState s = Make();
            Assert.AreEqual(DemoChange.Restart, s.Apply(DemoCommand.Play));
            Assert.AreEqual(DemoMode.Play, s.Mode);
            Assert.AreEqual(1, s.Generation);
            Assert.AreEqual(DemoChange.Restart, s.Apply(DemoCommand.Play));   // play the song again from the top
            Assert.AreEqual(2, s.Generation);
            Assert.AreEqual(DemoChange.Restart, s.Apply(DemoCommand.Learn));
            Assert.AreEqual(DemoMode.Learn, s.Mode);
            Assert.AreEqual(DemoChange.Restart, s.Apply(DemoCommand.Idle));
            Assert.AreEqual(DemoMode.Idle, s.Mode);
            Assert.AreEqual(4, s.Generation);
        }

        [Test]
        public void Next_CyclesSongsInPlay_AndChordsInLearn_WithWrap()
        {
            DemoState s = Make(songs: 3, chords: 2);
            s.Apply(DemoCommand.Play);
            s.Apply(DemoCommand.Next);
            s.Apply(DemoCommand.Next);
            Assert.AreEqual(2, s.SongIndex);
            s.Apply(DemoCommand.Next);
            Assert.AreEqual(0, s.SongIndex, "wraps after the last song");

            s.Apply(DemoCommand.Learn);
            s.Apply(DemoCommand.Next);
            Assert.AreEqual(1, s.ChordIndex);
            s.Apply(DemoCommand.Next);
            Assert.AreEqual(0, s.ChordIndex, "wraps after the last chord");
            Assert.AreEqual(0, s.SongIndex, "learn's Next leaves the song alone");
        }

        [Test]
        public void Next_WhileIdle_DoesNothing_AndSaysWhy()
        {
            DemoState s = Make();
            Assert.AreEqual(DemoChange.None, s.Apply(DemoCommand.Next));
            Assert.AreEqual(0, s.Generation);
            Assert.IsNotNull(s.LastRefusal);
        }

        [Test]
        public void Tempo_ClampsAtBothEnds()
        {
            DemoState s = Make(tempos: 3, startTempo: 1);
            Assert.AreEqual(DemoChange.TempoForNextPlay, s.Apply(DemoCommand.TempoUp));
            Assert.AreEqual(2, s.TempoIndex);
            Assert.AreEqual(DemoChange.None, s.Apply(DemoCommand.TempoUp), "already the fastest");
            Assert.AreEqual(2, s.TempoIndex);
            s.Apply(DemoCommand.TempoDown);
            s.Apply(DemoCommand.TempoDown);
            Assert.AreEqual(DemoChange.None, s.Apply(DemoCommand.TempoDown), "already the slowest");
            Assert.AreEqual(0, s.TempoIndex);
        }

        [Test]
        public void Tempo_RestartsOnlyWhilePlaying()
        {
            DemoState s = Make(tempos: 5, startTempo: 2);
            int g = s.Generation;
            Assert.AreEqual(DemoChange.TempoForNextPlay, s.Apply(DemoCommand.TempoDown));
            Assert.AreEqual(g, s.Generation, "idle: no restart");
            s.Apply(DemoCommand.Play);
            g = s.Generation;
            Assert.AreEqual(DemoChange.Restart, s.Apply(DemoCommand.TempoUp));
            Assert.AreEqual(g + 1, s.Generation, "playing: restart at the new tempo");
        }

        [Test]
        public void Skip_WorksInLearnAndPlay_NotIdle_RecenterAnywhere()
        {
            DemoState s = Make();
            Assert.AreEqual(DemoChange.None, s.Apply(DemoCommand.Skip), "idle: nothing to skip");
            Assert.AreEqual(DemoChange.Recenter, s.Apply(DemoCommand.Recenter));
            s.Apply(DemoCommand.Learn);
            int g = s.Generation;
            Assert.AreEqual(DemoChange.Skip, s.Apply(DemoCommand.Skip));
            Assert.AreEqual(g, s.Generation, "skip is not a restart");
            s.Apply(DemoCommand.Play);
            g = s.Generation;
            Assert.AreEqual(DemoChange.Skip, s.Apply(DemoCommand.Skip), "play: releases a wait-mode hold");
            Assert.AreEqual(g, s.Generation);
        }

        [Test]
        public void Align_FromPlay_GoesIdleWithARestart_FromIdle_DoesNotRestart()
        {
            DemoState s = Make();
            int g = s.Generation;
            Assert.AreEqual(DemoChange.Align, s.Apply(DemoCommand.Align));
            Assert.AreEqual(DemoMode.Idle, s.Mode);
            Assert.AreEqual(g, s.Generation, "already idle: nothing to restart");
            s.Apply(DemoCommand.Play);
            g = s.Generation;
            Assert.AreEqual(DemoChange.Align, s.Apply(DemoCommand.Align));
            Assert.AreEqual(DemoMode.Idle, s.Mode, "the song stops before the glances");
            Assert.AreEqual(g + 1, s.Generation);
        }

        [Test]
        public void Reanchor_KeepsTheMode()
        {
            DemoState s = Make();
            s.Apply(DemoCommand.Play);
            int g = s.Generation;
            Assert.AreEqual(DemoChange.Reanchor, s.Apply(DemoCommand.Reanchor));
            Assert.AreEqual(DemoMode.Play, s.Mode);
            Assert.AreEqual(g, s.Generation);
            Assert.AreEqual(DemoCommand.Align, DemoKeys.CommandFor(k => k == KeyCode.A));
            Assert.AreEqual(DemoCommand.Reanchor, DemoKeys.CommandFor(k => k == KeyCode.Z));
        }

        [Test]
        public void SetIndices_Clamps_AndChangesNeitherModeNorGeneration()
        {
            DemoState s = Make(songs: 3, chords: 5, tempos: 2, startTempo: 0);
            s.Apply(DemoCommand.Play);
            int g = s.Generation;
            s.SetIndices(7, -2, 1);
            Assert.AreEqual(2, s.SongIndex);
            Assert.AreEqual(0, s.ChordIndex);
            Assert.AreEqual(1, s.TempoIndex);
            Assert.AreEqual(DemoMode.Play, s.Mode);
            Assert.AreEqual(g, s.Generation);
        }

        [Test]
        public void ZeroCounts_RefusePlayAndLearn()
        {
            DemoState s = Make(songs: 0, chords: 0, tempos: 0, startTempo: 3);
            Assert.AreEqual(0, s.TempoIndex);
            Assert.AreEqual(DemoChange.None, s.Apply(DemoCommand.Play));
            Assert.AreEqual(DemoChange.None, s.Apply(DemoCommand.Learn));
            Assert.AreEqual(DemoChange.None, s.Apply(DemoCommand.TempoUp));
            Assert.AreEqual(DemoMode.Idle, s.Mode);
            Assert.AreEqual(0, s.Generation);
        }

        [Test]
        public void SetCounts_ClampsIndices_AndLeavesAModeThatLostItsItems()
        {
            DemoState s = Make(songs: 3, chords: 5, tempos: 5, startTempo: 4);
            s.Apply(DemoCommand.Play);
            s.Apply(DemoCommand.Next);
            s.Apply(DemoCommand.Next);
            s.SetCounts(3, 5, 2);   // the judge profile has two tempo presets
            Assert.AreEqual(1, s.TempoIndex);
            Assert.AreEqual(DemoMode.Play, s.Mode);
            s.SetCounts(0, 5, 2);
            Assert.AreEqual(DemoMode.Idle, s.Mode, "no songs left: back to idle");
            Assert.AreEqual(0, s.SongIndex);
        }

        [TestCase(new float[] { 0.5f, 0.6f, 0.75f, 0.9f, 1.0f }, 0.75f, 2)]
        [TestCase(new float[] { 0.5f, 0.75f }, 0.75f, 1)]
        [TestCase(new float[] { 0.5f, 1.0f }, 0.75f, 0)]   // a tie keeps the first (slower) preset
        [TestCase(new float[] { 1.0f }, 0.75f, 0)]
        [TestCase(new float[0], 0.75f, 0)]
        public void NearestIndex_PicksTheClosestPreset(float[] presets, float target, int expected)
        {
            Assert.AreEqual(expected, DemoState.NearestIndex(presets, target));
        }

        [Test]
        public void NearestIndex_NullList_IsZero()
        {
            Assert.AreEqual(0, DemoState.NearestIndex(null, 0.75f));
        }

        [Test]
        public void Keys_AndCommands_AreAligned_AndEveryKeyIsInTheLegend()
        {
            Assert.AreEqual(DemoKeys.Keys.Length, DemoKeys.Commands.Length);
            var seen = new HashSet<KeyCode>();
            foreach (KeyCode k in DemoKeys.Keys) Assert.IsTrue(seen.Add(k), "duplicate key " + k);
            StringAssert.Contains("L learn", DemoKeys.Legend);
            StringAssert.Contains("R recenter", DemoKeys.Legend);
        }

        [TestCase(true, false, true)]    // scripted verdicts on a live mic: disclosed
        [TestCase(true, true, false)]    // the replay badge is showing: it is the stronger disclosure, same spot
        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        public void ScriptedChecksLabel_ShowsOnlyWhenTheReplayBadgeDoesNot(bool scripted, bool replayShowing, bool expected)
        {
            Assert.AreEqual(expected, DemoDirector.ShowScriptedChecks(scripted, replayShowing));
            StringAssert.Contains("SIMULATED", DemoDirector.ScriptedChecksText);
        }

        [Test]
        public void ArgumentValue_ReadsTheWordAfterTheFlag()
        {
            string[] args = { "player", "-batchmode", "-ugaDemoMode", "play", "-ugaSong", "happy-birthday", "-ugaTempo" };
            Assert.AreEqual("play", DemoDirector.ArgumentValue(args, DemoDirector.ModeArgument));
            Assert.AreEqual("happy-birthday", DemoDirector.ArgumentValue(args, DemoDirector.SongArgument));
            Assert.IsNull(DemoDirector.ArgumentValue(args, DemoDirector.TempoArgument), "a flag with nothing after it");
            Assert.IsNull(DemoDirector.ArgumentValue(args, DemoDirector.ChordArgument), "absent");
            Assert.IsNull(DemoDirector.ArgumentValue(null, DemoDirector.ModeArgument));
        }

        [Test]
        public void CommandFor_ReturnsTheFirstPressedKeyInTableOrder()
        {
            Assert.AreEqual(DemoCommand.None, DemoKeys.CommandFor(k => false));
            Assert.AreEqual(DemoCommand.None, DemoKeys.CommandFor(null));
            Assert.AreEqual(DemoCommand.Play, DemoKeys.CommandFor(k => k == KeyCode.P));
            Assert.AreEqual(DemoCommand.Idle, DemoKeys.CommandFor(k => k == KeyCode.Escape));
            Assert.AreEqual(DemoCommand.TempoUp, DemoKeys.CommandFor(k => k == KeyCode.RightBracket));
            Assert.AreEqual(DemoCommand.Learn, DemoKeys.CommandFor(k => k == KeyCode.L || k == KeyCode.P), "L comes first in the table");
        }
    }
}
