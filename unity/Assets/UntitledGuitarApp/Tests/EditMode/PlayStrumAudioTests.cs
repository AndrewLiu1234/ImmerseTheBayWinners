// PlayStrumAudioTests: CorePlayAlong.FeedAudio, the real-strum path of play mode (Core's StrumFeed -> StrumRouting ->
// PlayAlongSession.OnStrum). Everything except the audio quality (Core's tested domain) is pinned here: the wiring. Offline
// (mono-tests.sh) as well as in Unity. Songs come from StreamingAssets, found from this file's own path (PlayFlowFiles).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Songs;
using UntitledGuitarApp.Runtime.Logging;
using UntitledGuitarApp.Runtime.Practice;

namespace UntitledGuitarApp.Tests.EditMode
{
    /// <summary>Self-contained song loading (mono-tests compiles one test file at a time; PlayFlowTests' helper is not visible offline).</summary>
    static class PlayStrumFiles
    {
        public static string AssetsFolder([CallerFilePath] string here = "")
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(here));
            return Path.GetFullPath(Path.Combine(dir, "..", "..", ".."));
        }

        public static CorePlayAlong Started(string id, float preset, bool wait)
        {
            string folder = Path.Combine(AssetsFolder(), "StreamingAssets", CorePlayAlong.SongsFolderName);
            if (!Directory.Exists(folder)) Assert.Inconclusive("songs folder not found: " + folder);
            Song song;
            string error;
            Assert.IsTrue(CorePlayAlong.TryLoadSongFile(CorePlayAlong.SongPath(folder, id), out song, out error), error);
            var play = new CorePlayAlong();
            Assert.IsTrue(play.Start(song, preset, wait, null), "start " + id);
            return play;
        }
    }

    public class PlayStrumAudioTests
    {
        const int Rate = 48000;
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

        /// <summary>1 s: silence, then a 0.6 s loud E4 (330 Hz), then silence. Enough for one onset and its 250 ms strum window.</summary>
        static float[] Burst()
        {
            var x = new float[Rate];
            int start = (int)(0.1 * Rate);
            for (int i = 0; i < (int)(0.6 * Rate); i++)
                x[start + i] = (float)(0.5 * Mathf.Sin((float)(2.0 * Mathf.PI * 330.0 * i / Rate)));
            return x;
        }

        static void RunUntil(CorePlayAlong play, Func<CorePlayAlong, bool> until, float step, float maxSeconds)
        {
            float t = 0f;
            while (!until(play))
            {
                Assert.Less(t, maxSeconds, "not reached in " + maxSeconds + " s (beat " + play.Frame.Beat + ", phase " + play.Phase + ")");
                play.Tick(step);
                t += step;
            }
        }

        static bool Logged(string line)
        {
            return line.IndexOf("play_strum_real", StringComparison.Ordinal) >= 0;
        }

        [Test]
        public void FeedAudio_DuringCountIn_DropsTheStrum_AndTheSongStartsNormally()
        {
            var play = PlayStrumFiles.Started("riptide-loop", 0.75f, true);
            play.SimulateStrums = false;
            float[] burst = Burst();
            play.FeedAudio(burst, 0, burst.Length, Rate);   // expected = null during the count-in: onsets are dropped
            RunUntil(play, p => p.Phase == PlayAlongPhase.Playing, 0.01f, 30f);
            Assert.AreEqual(0, play.RealStrums, "a count-in strum must never reach the session");
            Assert.IsFalse(play.Frame.ChordConfirmed, "the song must start on its own clock, not on the stray strum");
            Assert.AreEqual(0, play.SimulatedStrums);
        }

        [Test]
        public void FeedAudio_RoutesJudgedStrums_IntoTheSession_AsReal()
        {
            var play = PlayStrumFiles.Started("riptide-loop", 0.75f, true);
            play.SimulateStrums = false;
            RunUntil(play, p => p.Phase == PlayAlongPhase.Playing, 0.01f, 30f);
            float[] burst = Burst();
            play.FeedAudio(burst, 0, burst.Length, Rate);
            Assert.GreaterOrEqual(play.RealStrums, 1, "the burst must be judged and routed");
            Assert.AreEqual(0, play.SimulatedStrums);
            Assert.IsFalse(play.Results.Simulated, "real strums must not mark the score simulated");
            int logged = 0;
            for (int i = 0; i < _log.Count; i++) if (Logged(_log[i])) logged++;
            Assert.GreaterOrEqual(logged, 1, "every routed strum logs play_strum_real");
            Assert.AreEqual(PlayAlongPhase.Playing, play.Phase, "one burst must not finish the song");
        }

        [Test]
        public void FeedAudio_RejectsBadInput_AndRebuildsOnRateChanges()
        {
            var play = PlayStrumFiles.Started("riptide-loop", 0.75f, true);
            play.SimulateStrums = false;
            RunUntil(play, p => p.Phase == PlayAlongPhase.Playing, 0.01f, 30f);

            play.FeedAudio(null, 0, 0, Rate);          // never throws
            play.FeedAudio(Burst(), 0, 10, 8000);      // a rate Core refuses: ignored
            Assert.AreEqual(0, play.RealStrums);

            float[] burst = Burst();
            play.FeedAudio(burst, 0, burst.Length, Rate);
            play.FeedAudio(Burst(), 0, Burst().Length, 44100);   // a rate change rebuilds the feed
            Assert.GreaterOrEqual(play.RealStrums, 1);

            play.ResetAudio();
            play.FeedAudio(Burst(), 0, Burst().Length, Rate);    // rebuilt lazily: still works
            Assert.GreaterOrEqual(play.RealStrums, 1);
            Assert.AreEqual(PlayAlongPhase.Playing, play.Phase);
        }

        [Test]
        public void SimulatedFallback_StillWorks_WhenNoAudioIsFed()
        {
            var play = PlayStrumFiles.Started("riptide-loop", 0.75f, true);   // SimulateStrums defaults true
            RunUntil(play, p => p.Phase == PlayAlongPhase.Playing, 0.01f, 30f);
            RunUntil(play, p => p.SimulatedStrums == 1, 0.01f, 5f);
            Assert.AreEqual(0, play.RealStrums);
            Assert.IsTrue(play.Results.Simulated);
        }
    }
}
