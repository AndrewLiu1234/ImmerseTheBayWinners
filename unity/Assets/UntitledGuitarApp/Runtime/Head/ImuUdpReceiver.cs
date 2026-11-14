// ImuUdpReceiver: the background thread that listens for the IMU bridge's UDP packets, plus the latest-value slot it
// publishes into. Plain C# (no MonoBehaviour, no Unity engine calls), so it is safe off the main thread and testable.
//
// Design rules:
//  * The thread owns its socket from bind to close. Nobody else touches it, so shutting down never depends on closing a
//    socket that another thread is blocked on (which is unreliable on some runtimes). The loop waits with Socket.Poll
//    for at most 20 ms at a time and then looks at the stop flag, so Stop() returns within about that long.
//  * Only the newest sample matters, so the thread publishes into a three-slot "latest value" mailbox instead of a queue:
//    nothing can grow, and neither side ever waits for the other.
//  * Nothing is allocated per packet: one preallocated buffer, one parse that creates no objects, one struct copy.
//  * If the port cannot be bound (another process, or a second Unity instance, owns it) the thread reports ONE failure
//    message, keeps retrying every 2 s without further reports, and starts delivering as soon as the port frees up.
//  * The thread never calls UGALog or any Unity API; failures are handed to the main thread through TakeFailure().

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Head
{
    /// One accepted packet plus the receiver's bookkeeping.
    public struct ImuSample
    {
        /// 1 for the first valid packet, then +1 per valid packet: the difference of two values counts the datagrams in between.
        public long Seq;
        /// When the datagram arrived, in seconds on the <see cref="ImuUdpReceiver.NowSec"/> clock.
        public double RecvSec;
        /// The bridge's own timestamp (seconds since the bridge started).
        public double T;
        /// Head orientation in Unity's frame, normalized.
        public Quaternion Q;
    }

    /// <summary>
    /// Single-producer / single-consumer mailbox that always holds the newest value (a "triple buffer"). The producer
    /// writes into a private slot and swaps it with the middle slot; the consumer swaps the middle slot with its own.
    /// Both sides are wait-free, nothing is allocated after construction, and a reader never sees a half-written value.
    /// </summary>
    public sealed class ImuSampleSlot
    {
        const int DirtyBit = 4;
        const int IndexMask = 3;

        readonly ImuSample[] _slots = new ImuSample[3];
        int _write;             // slot the producer fills next (producer only)
        int _read = 1;          // slot the consumer last received (consumer only)
        int _middle = 2;        // slot in the middle, plus DirtyBit when it holds a value the consumer has not taken

        /// Producer thread only.
        public void Publish(in ImuSample sample)
        {
            _slots[_write] = sample;
            int previous = Interlocked.Exchange(ref _middle, _write | DirtyBit);   // full fence: publishes the write above
            _write = previous & IndexMask;
        }

        /// Consumer thread only. Returns false, leaving sample untouched, when nothing new was published since the last call.
        public bool TryTake(ref ImuSample sample)
        {
            if ((Volatile.Read(ref _middle) & DirtyBit) == 0) return false;
            int previous = Interlocked.Exchange(ref _middle, _read);               // full fence: orders the reads below
            _read = previous & IndexMask;
            sample = _slots[_read];
            return true;
        }
    }

    public sealed class ImuUdpReceiver
    {
        /// Datagrams larger than this are cut by the OS and then rejected as truncated JSON.
        public const int BufferSize = 4096;
        /// Wait between attempts to bind the port again.
        public const int RetryMs = 2000;

        const int PollMicroseconds = 20000;
        const int JoinTimeoutMs = 1000;

        static readonly long OriginTicks = Stopwatch.GetTimestamp();
        static readonly double SecondsPerTick = 1.0 / Stopwatch.Frequency;

        /// Seconds on a monotonic clock that every thread can read. ImuSample.RecvSec and the main thread's "now" must
        /// both come from here, so that "how old is the newest sample" is meaningful.
        public static double NowSec()
        {
            return (Stopwatch.GetTimestamp() - OriginTicks) * SecondsPerTick;
        }

        readonly int _port;
        readonly byte[] _buffer = new byte[BufferSize];
        readonly ImuSampleSlot _slot = new ImuSampleSlot();
        readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        readonly object _lifecycle = new object();

        Thread _thread;
        bool _started;
        Socket _socket;                 // receive thread only
        bool _failureReported;          // receive thread only: one report per failure episode
        long _sequence;                 // receive thread only
        int _bound;                     // 1 while the socket is bound
        int _listening;                 // 1 after each successful bind until TakeListening() reads it
        string _pendingFailure;         // newest failure message not yet taken
        long _received;                 // valid packets
        long _rejected;                 // datagrams the parser refused
        int _stopTimedOut;

        public ImuUdpReceiver(int port)
        {
            _port = port;
        }

        public int Port { get { return _port; } }

        /// True while the socket is bound (false while retrying).
        public bool IsBound { get { return Volatile.Read(ref _bound) != 0; } }

        /// True while the receive thread is alive.
        public bool IsRunning
        {
            get
            {
                Thread thread = _thread;
                return thread != null && thread.IsAlive;
            }
        }

        /// Valid packets received so far.
        public long ReceivedCount { get { return Interlocked.Read(ref _received); } }

        /// Datagrams that arrived but were not valid IMU packets.
        public long RejectedCount { get { return Interlocked.Read(ref _rejected); } }

        /// True if Stop() gave up waiting for the thread (it is a background thread and still closes its socket on exit).
        public bool StopTimedOut { get { return Volatile.Read(ref _stopTimedOut) != 0; } }

        /// Starts the thread. A receiver is single-use: a second Start, or a Start after Stop, does nothing.
        public void Start()
        {
            lock (_lifecycle)
            {
                if (_started) return;
                _started = true;
                Thread thread = new Thread(Run);
                thread.Name = "UGA-ImuUdp";
                thread.IsBackground = true;
                _thread = thread;
                thread.Start();
            }
        }

        /// Asks the thread to finish, waits for it (at most one second) and thereby closes the socket. Safe to call repeatedly,
        /// from any thread, and before Start.
        public void Stop()
        {
            Thread thread;
            lock (_lifecycle)
            {
                _started = true;        // never start after a stop
                thread = _thread;
            }
            _stop.Set();
            if (thread == null || thread == Thread.CurrentThread) return;
            if (!thread.Join(JoinTimeoutMs)) Volatile.Write(ref _stopTimedOut, 1);
        }

        /// Main thread: copies the newest sample into `sample` if one arrived since the last call.
        public bool TryTakeLatest(ref ImuSample sample)
        {
            return _slot.TryTake(ref sample);
        }

        /// Returns the message of a new failure (cannot bind, socket error) once, then null until the next failure episode.
        public string TakeFailure()
        {
            return Interlocked.Exchange(ref _pendingFailure, null);
        }

        /// True once after each successful bind.
        public bool TakeListening()
        {
            return Interlocked.Exchange(ref _listening, 0) != 0;
        }

        // ------------------------------------------------------------------------------------------------ the thread

        void Run()
        {
            try
            {
                while (!_stop.IsSet)
                {
                    if (_socket == null && !TryOpen())
                    {
                        _stop.Wait(RetryMs);        // returns at once when Stop() sets the event
                        continue;
                    }
                    if (!ReceiveOnce())
                    {
                        CloseSocket();
                        _stop.Wait(RetryMs);
                    }
                }
            }
            catch (Exception ex)
            {
                Fail("receive thread stopped by " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                CloseSocket();
            }
        }

        bool TryOpen()
        {
            Socket socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, _port));
                socket.Blocking = false;
                try { socket.ReceiveBufferSize = 1 << 18; } catch (SocketException) { }   // best effort
                _socket = socket;
                _failureReported = false;
                Volatile.Write(ref _bound, 1);
                Interlocked.Exchange(ref _listening, 1);
                return true;
            }
            catch (SocketException ex)
            {
                Discard(socket);
                Fail("cannot listen on 127.0.0.1:" + _port + " (" + ex.SocketErrorCode + "): " + ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                Discard(socket);
                Fail("cannot listen on 127.0.0.1:" + _port + ": " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        // One wait-and-read step. Returns false when the socket is broken and has to be reopened.
        bool ReceiveOnce()
        {
            try
            {
                if (!_socket.Poll(PollMicroseconds, SelectMode.SelectRead)) return true;   // idle: look at the stop flag
                SocketError error;
                int length = _socket.Receive(_buffer, 0, _buffer.Length, SocketFlags.None, out error);
                double arrival = NowSec();
                if (error != SocketError.Success)
                {
                    if (error == SocketError.WouldBlock || error == SocketError.Interrupted || error == SocketError.TimedOut) return true;
                    Fail("receive on 127.0.0.1:" + _port + " failed (" + error + ")");
                    return false;
                }
                if (length <= 0) return true;

                double t;
                Quaternion q;
                if (ImuPacketParser.TryParse(_buffer, length, out t, out q))
                {
                    long seq = ++_sequence;
                    ImuSample sample;
                    sample.Seq = seq;
                    sample.RecvSec = arrival;
                    sample.T = t;
                    sample.Q = q;
                    _slot.Publish(sample);
                    Interlocked.Exchange(ref _received, seq);
                }
                else
                {
                    Interlocked.Increment(ref _rejected);
                }
                return true;
            }
            catch (SocketException ex)
            {
                Fail("receive on 127.0.0.1:" + _port + " failed (" + ex.SocketErrorCode + "): " + ex.Message);
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        void Fail(string message)
        {
            Volatile.Write(ref _bound, 0);
            if (_failureReported) return;
            _failureReported = true;
            Interlocked.Exchange(ref _pendingFailure, message);
        }

        void CloseSocket()
        {
            Volatile.Write(ref _bound, 0);
            Socket socket = _socket;
            _socket = null;
            Discard(socket);
        }

        static void Discard(Socket socket)
        {
            if (socket == null) return;
            try { socket.Close(); }
            catch (Exception) { }       // nothing useful to do if closing a socket fails
        }
    }
}
