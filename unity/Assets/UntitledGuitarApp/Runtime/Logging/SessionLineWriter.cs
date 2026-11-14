// SessionLineWriter.cs: a bounded, never-blocking queue of text lines that one background thread drains into a TextWriter.
// This is the engine under SessionRecorder; it is a separate class (no statics, no Unity calls) so tests can drive it with
// a StringWriter or a writer they can stall.
//
//   * TryWrite(line) may be called from any thread. It never waits for the disk and never takes a lock that the writer
//     thread holds while writing: it bumps an atomic counter, enqueues into a ConcurrentQueue and pokes an event.
//   * "Capacity" is the number of accepted lines that are not yet fully written (queued plus the one being written). When
//     that many are outstanding the new line is DROPPED and counted in Dropped. Nothing waits, nothing is retried.
//   * One writer thread (background, so it can never keep the player alive) appends "line\n" for every accepted line and
//     flushes whenever the queue runs empty, so a file being recorded is readable a moment later.
//   * Close(timeoutMs) refuses new lines, lets the writer drain what was accepted, flushes and closes the TextWriter.
//     A disk that stalls longer than the timeout is abandoned: Close returns false and the thread finishes by itself.
//   * An I/O error stops the writing (the error text is in Error); lines offered after that are counted as dropped.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace UntitledGuitarApp.Runtime.Logging
{
    public sealed class SessionLineWriter
    {
        readonly TextWriter _out;
        readonly int _capacity;
        readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);
        readonly Thread _thread;

        int _pending;                          // accepted and not yet fully written
        int _inFlight;                         // TryWrite calls currently between "gate check" and "queued"
        long _accepted;
        long _dropped;
        long _written;
        volatile bool _closing;                // no new lines are accepted
        volatile bool _stopWriter;             // the writer thread may exit once the queue is empty (set after the last line is queued)
        volatile string _error;
        int _closeCalled;

        public SessionLineWriter(TextWriter output, int capacity)
        {
            if (output == null) throw new ArgumentNullException("output");
            _out = output;
            _capacity = capacity < 1 ? 1 : capacity;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "UGA-SessionWriter";
            _thread.Start();                   // last: every field above is assigned before the thread can look at it
        }

        public int Capacity { get { return _capacity; } }
        /// <summary>Lines taken into the queue.</summary>
        public long Accepted { get { return Interlocked.Read(ref _accepted); } }
        /// <summary>Lines refused because the queue was full (or the writer had failed).</summary>
        public long Dropped { get { return Interlocked.Read(ref _dropped); } }
        /// <summary>Lines the writer thread has handed to the TextWriter.</summary>
        public long Written { get { return Interlocked.Read(ref _written); } }
        /// <summary>Accepted lines not yet written.</summary>
        public int Pending { get { return Volatile.Read(ref _pending); } }
        /// <summary>null while healthy; the exception text once the TextWriter has failed.</summary>
        public string Error { get { return _error; } }

        /// <summary>
        /// Offers one line (no trailing newline; CR and LF inside it are replaced by spaces so one call is always one line).
        /// Returns true when it was queued, false when it was dropped or the writer is closing. Never blocks.
        /// </summary>
        public bool TryWrite(string line)
        {
            if (line == null) return false;
            Interlocked.Increment(ref _inFlight);              // a full fence: BeginClose() either sees us or we see _closing
            try
            {
                if (_closing) return false;
                if (_error != null)
                {
                    Interlocked.Increment(ref _dropped);
                    return false;
                }
                if (Interlocked.Increment(ref _pending) > _capacity)
                {
                    Interlocked.Decrement(ref _pending);
                    Interlocked.Increment(ref _dropped);
                    return false;
                }
                Enqueue(line);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        /// <summary>
        /// First half of a close, for a caller that wants to put one last line behind everything else: from here on TryWrite
        /// refuses every line, and the call returns once the TryWrite calls that were already past the gate have queued theirs.
        /// Waits at most one second (a producer thread that is descheduled inside TryWrite). Idempotent.
        /// </summary>
        internal void BeginClose()
        {
            _closing = true;
            Interlocked.MemoryBarrier();                       // publish _closing before reading _inFlight
            SpinWait spin = new SpinWait();
            long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency;
            while (Volatile.Read(ref _inFlight) != 0 && System.Diagnostics.Stopwatch.GetTimestamp() < deadline) spin.SpinOnce();
        }

        // Queues one line even though the writer is closing and whatever the capacity: the closing "session_end" line, which
        // must be the last one in the file and must not be lost to a full queue. Only after BeginClose().
        internal bool TryWriteReserved(string line)
        {
            if (line == null || _error != null) return false;
            Interlocked.Increment(ref _pending);
            Enqueue(line);
            return true;
        }

        void Enqueue(string line)
        {
            _queue.Enqueue(OneLine(line));
            Interlocked.Increment(ref _accepted);
            _wake.Set();
        }

        /// <summary>
        /// Stops accepting lines, writes what was accepted, flushes and closes the TextWriter. Returns true when the writer
        /// thread finished within <paramref name="timeoutMs"/>; false when the disk is stuck (the thread is a background
        /// thread and ends on its own). Safe to call twice.
        /// </summary>
        public bool Close(int timeoutMs)
        {
            BeginClose();
            _stopWriter = true;                                // only now may the writer thread leave: a reserved last line is queued by here
            _wake.Set();
            bool first = Interlocked.Exchange(ref _closeCalled, 1) == 0;
            bool finished = _thread.Join(timeoutMs < 0 ? 0 : timeoutMs);
            if (finished && first)
            {
                string straggler;                              // a TryWrite that raced the closing flag
                while (_queue.TryDequeue(out straggler)) Interlocked.Increment(ref _dropped);
                try { _out.Flush(); } catch (Exception) { }
                try { _out.Dispose(); } catch (Exception) { }
            }
            return finished;
        }

        void Run()
        {
            try
            {
                while (true)
                {
                    _wake.Reset();                             // BEFORE draining: a Set() after this point is never lost
                    bool wroteAny = false;
                    string line;
                    while (_queue.TryDequeue(out line))
                    {
                        if (_error == null)
                        {
                            try
                            {
                                _out.Write(line);
                                _out.Write('\n');
                                Interlocked.Increment(ref _written);
                            }
                            catch (Exception e)
                            {
                                _error = e.GetType().Name + ": " + e.Message;
                                Interlocked.Increment(ref _dropped);
                            }
                        }
                        else
                        {
                            Interlocked.Increment(ref _dropped);
                        }
                        Interlocked.Decrement(ref _pending);
                        wroteAny = true;
                    }
                    if (wroteAny && _error == null)
                    {
                        try { _out.Flush(); }
                        catch (Exception e) { _error = e.GetType().Name + ": " + e.Message; }
                    }
                    if (_stopWriter && _queue.IsEmpty) break;
                    _wake.Wait(50);                            // the timeout is only a safety net
                }
            }
            catch (Exception e)
            {
                _error = e.GetType().Name + ": " + e.Message;
            }
        }

        static string OneLine(string line)
        {
            if (line.IndexOf('\n') < 0 && line.IndexOf('\r') < 0) return line;
            return line.Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
