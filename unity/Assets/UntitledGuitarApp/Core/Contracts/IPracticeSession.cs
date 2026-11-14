using System;

namespace UntitledGuitarApp.Core.Contracts
{
    /// <summary>Which practice flow a session runs (was <c>string Mode</c> in the sketch).</summary>
    public enum PracticeMode
    {
        /// <summary>One chord: finger-by-finger build, pluck check per string, then strum check (untimed).</summary>
        Learn = 0,
        /// <summary>A song at a tempo preset, optionally in wait mode.</summary>
        PlayAlong = 1,
        /// <summary>Clean-change counter between two chords (SHOULD, SL7).</summary>
        ChangeDrill = 2,
    }

    /// <summary>Player / operator input the session must handle (keyboard, clicker or demo operator).</summary>
    public enum PracticeCommand
    {
        /// <summary>Begin at the current time; the session emits its first ChordTarget and ListenFor.</summary>
        Start = 0,
        /// <summary>Freeze the playhead; audio is ignored until Resume.</summary>
        Pause = 1,
        Resume = 2,
        /// <summary>Escape hatch: accept the current step/chord as-is and move on (wait mode never deadlocks).</summary>
        Skip = 3,
        /// <summary>Back to the start of the session; stats reset.</summary>
        Restart = 4,
    }

    /// <summary>
    /// A practice flow (Core/Practice: LearnChordSession, PlayAlongSession, ChangeDrillSession). Main thread only;
    /// deterministic: the same commands, audio events and tick times give the same emitted events. Time is always passed in
    /// (session-clock seconds, monotonic, same clock as <see cref="AudioEvent.TimeSec"/>); never reads a clock itself.
    /// Constructed by Runtime's Bootstrap with its song/chord, tempo and flag config (constructors are not part of this contract).
    /// </summary>
    public interface IPracticeSession
    {
        PracticeMode Mode { get; }

        /// <summary>Handle a command at <paramref name="nowSec"/>.</summary>
        void OnCommand(PracticeCommand command, double nowSec);

        /// <summary>Handle one audio event (dequeued on the main thread). Events may arrive up to ~350 ms after their TimeSec.</summary>
        void OnAudio(AudioEvent e);

        /// <summary>Advance the playhead to <paramref name="nowSec"/>; call once per frame. Must not allocate unless it emits.</summary>
        void Tick(double nowSec);

        /// <summary>Raised synchronously from OnCommand/OnAudio/Tick. Handlers must not call back into the session.</summary>
        event Action<PracticeEvent> Emitted;
    }
}
