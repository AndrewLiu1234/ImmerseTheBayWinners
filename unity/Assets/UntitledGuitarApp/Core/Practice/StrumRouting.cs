using System;
using UntitledGuitarApp.Core.Audio;

namespace UntitledGuitarApp.Core.Practice
{
    /// <summary>
    /// Strum check → practice sessions (SL3). The caller drains <see cref="StrumFeed.TryRead"/> on the main thread and routes
    /// each <see cref="StrumEvent"/> here; time is the event's onset time on the caller clock. Play-along and the change drill
    /// take the chord heard (<see cref="StrumEvent.HeardChord"/>: the best chord-level match, "" when nothing rang — a fault
    /// on the right chord still counts as that chord there); Learn mode takes the whole result (clean + top issue + tip).
    /// </summary>
    public static class StrumRouting
    {
        public static PlayAlongTarget Feed(PlayAlongSession session, in StrumEvent e)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            return session.OnStrum(e.HeardChord, e.TimeSec);
        }

        public static ChangeDrillState Feed(ChangeDrillSession session, in StrumEvent e)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            return session.OnStrum(e.HeardChord, e.TimeSec);
        }

        public static LearnChordState Feed(LearnChordSession session, in StrumEvent e)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            return session.OnStrum(e.Result, e.TimeSec);
        }
    }
}
