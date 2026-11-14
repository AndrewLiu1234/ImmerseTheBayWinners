namespace UntitledGuitarApp.Core.Contracts
{
    public enum AudioEventKind
    {
        /// <summary>A note/strum attack (spectral flux). Emitted for every onset, whatever the <see cref="ListenMode"/>.</summary>
        Onset = 0,
        /// <summary><see cref="AudioEvent.Tuner"/> is set.</summary>
        Tuner = 1,
        /// <summary><see cref="AudioEvent.Pluck"/> is set (pluck check of the expected string).</summary>
        Pluck = 2,
        /// <summary><see cref="AudioEvent.Strum"/> is set (strum check of the expected chord).</summary>
        Strum = 3,
        /// <summary><see cref="AudioEvent.LevelDb"/> holds the measured noise floor (dBFS RMS).</summary>
        NoiseFloor = 4,
    }

    /// <summary>
    /// One result from the audio worker thread, carried to the main thread through a <c>ConcurrentQueue</c> and fed to
    /// <see cref="IPracticeSession.OnAudio"/>. A readonly struct so Onset/Tuner events (per hop) don't allocate.
    /// Only the payload named by <see cref="Kind"/> is meaningful; the others are null/default.
    /// </summary>
    public readonly struct AudioEvent
    {
        public readonly AudioEventKind Kind;
        /// <summary>
        /// Session-clock seconds of the event: the onset time for Onset/Pluck/Strum (the verdict itself arrives up to ~250 ms
        /// later), the hop end for Tuner/NoiseFloor. Same clock as <see cref="IPracticeSession.Tick"/>. Raw: the latency
        /// offset (flag <c>audio.latencyOffsetMs</c>) is applied by Core's practice logic, not here.
        /// </summary>
        public readonly double TimeSec;
        public readonly TunerReading Tuner;
        public readonly StringResult Pluck;
        public readonly StrumResult Strum;
        /// <summary>NoiseFloor: dBFS RMS of the quiet passage. Otherwise 0.</summary>
        public readonly double LevelDb;

        public AudioEvent(AudioEventKind kind, double timeSec, TunerReading tuner, StringResult pluck, StrumResult strum, double levelDb)
        {
            Kind = kind; TimeSec = timeSec; Tuner = tuner; Pluck = pluck; Strum = strum; LevelDb = levelDb;
        }
    }
}
