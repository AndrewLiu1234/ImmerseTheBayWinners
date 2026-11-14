// ImuHeadPoseCore: everything ImuHeadPose does except being a MonoBehaviour, so it can be driven (and tested) by plain
// code. Call Tick once per frame from the main thread with the current time and FlagStore.Current.
//
// It owns the UDP receiver and the HeadPoseFilter and decides the tracking state:
//   Tracking   - a valid packet arrived within imu.staleMs
//   HeadLocked - no packet yet, packets stopped, or anchorMode is "headLocked" (that flag wins even when packets flow)
// It reads the flags on every Tick (a few field reads; no subscription to FlagStore.Changed to forget on a domain
// reload), so a hot-reloaded staleMs / predictionMs / invert* / anchorMode applies on the next frame, and a changed
// udpPort restarts the receiver on the new port.
//
// Logging (UGALog, main thread only, never per frame): imu_listening, imu_state (the starting state once, then each
// change, with a reason), imu_recenter, imu_stopped, and warnings imu_bind_failed (once per failure episode),
// imu_bad_port (once per bad value), imu_packets_rejected (at most one per 5 s while datagrams keep failing to parse).

using System;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Head
{
    public sealed class ImuHeadPoseCore
    {
        public const int DefaultUdpPort = 47800;
        const string AnchorModeHeadLocked = "headLocked";
        const double RejectReportIntervalSec = 5.0;

        readonly HeadPoseFilter _filter = new HeadPoseFilter();
        ImuUdpReceiver _receiver;
        int _port;
        bool _portSet;
        long _lastSeq;
        ImuSample _sample;
        bool _anchorHeadLocked;
        HeadTrackingState _state = HeadTrackingState.HeadLocked;
        bool _stateLogged;
        Quaternion _rotation = Quaternion.identity;
        float _packetRateHz;
        long _reportedRejected;
        double _nextRejectCheckSec;
        bool _shutdown;

        /// Current state, as of the last Tick.
        public HeadTrackingState State { get { return _state; } }

        /// Head rotation to apply, as of the last Tick or Recenter; identity when HeadLocked.
        public Quaternion Rotation { get { return _rotation; } }

        /// Packets per second from the IMU bridge, as of the last Tick; 0 when stale.
        public float PacketRateHz { get { return _packetRateHz; } }

        /// The Recenter() yaw offset in radians (for keeping it across a domain reload).
        public double YawOffsetRadians
        {
            get { return _filter.YawOffsetRadians; }
            set { _filter.YawOffsetRadians = value; }
        }

        /// The port the receiver was last set up for, or -1 before the first Tick (it can be invalid; no receiver then).
        public int Port { get { return _portSet ? _port : -1; } }

        /// True while the receiver has the port bound.
        public bool ReceiverBound { get { return _receiver != null && _receiver.IsBound; } }

        /// Valid packets received since the receiver was (re)started.
        public long ReceivedCount { get { return _receiver != null ? _receiver.ReceivedCount : 0L; } }

        /// Once per frame, main thread. Returns true when State changed during this call (the caller raises its event).
        public bool Tick(double nowSec, FlagSet flags)
        {
            if (_shutdown) return false;
            SyncFlags(flags);
            PullLatest();
            ReportReceiver(nowSec);
            return Evaluate(nowSec);
        }

        /// Makes the current heading forward (yaw only). Returns false, changing nothing, when there is no fresh IMU data.
        public bool Recenter(double nowSec)
        {
            if (_shutdown) return false;
            PullLatest();
            bool ok = _filter.Recenter(nowSec);
            UGALog.Event("imu_recenter", "ok", ok, "yawOffsetDeg", _filter.YawOffsetRadians * (180.0 / Math.PI));
            if (ok && _state == HeadTrackingState.Tracking) _rotation = _filter.GetRotation(nowSec);
            return ok;
        }

        /// Stops the receiver thread (joined, socket closed) and goes quiet. Safe to call repeatedly; no further events.
        public void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            ImuUdpReceiver receiver = _receiver;
            long received = receiver != null ? receiver.ReceivedCount : 0L;
            long rejected = receiver != null ? receiver.RejectedCount : 0L;
            StopReceiver();
            _state = HeadTrackingState.HeadLocked;
            _rotation = Quaternion.identity;
            _packetRateHz = 0f;
            if (receiver != null) UGALog.Event("imu_stopped", "received", received, "rejected", rejected);
        }

        // ------------------------------------------------------------------------------------------------ internals

        void SyncFlags(FlagSet flags)
        {
            ImuFlags imu = flags != null ? flags.imu : null;
            if (imu != null)
                _filter.Configure(imu.staleMs, imu.predictionMs, imu.invertYaw, imu.invertPitch, imu.invertRoll);
            else
                _filter.Configure(HeadPoseFilter.DefaultStaleMs, HeadPoseFilter.DefaultPredictionMs, false, false, false);

            _anchorHeadLocked = flags != null
                && string.Equals(flags.anchorMode, AnchorModeHeadLocked, StringComparison.OrdinalIgnoreCase);

            int port = imu != null ? imu.udpPort : DefaultUdpPort;
            if (_portSet && port == _port) return;

            StopReceiver();
            _port = port;
            _portSet = true;
            _lastSeq = 0;
            _reportedRejected = 0;
            if (port < 1 || port > 65535)
            {
                UGALog.Warn("imu_bad_port", "imu.udpPort " + port + " is not a valid UDP port (1-65535); staying head-locked");
                return;
            }
            _receiver = new ImuUdpReceiver(port);
            _receiver.Start();
        }

        void StopReceiver()
        {
            ImuUdpReceiver receiver = _receiver;
            if (receiver == null) return;
            _receiver = null;
            receiver.Stop();
            if (receiver.StopTimedOut)
                UGALog.Warn("imu_stop_timeout", "the IMU receive thread on port " + receiver.Port + " did not stop within 1 s");
        }

        void PullLatest()
        {
            ImuUdpReceiver receiver = _receiver;
            if (receiver == null || !receiver.TryTakeLatest(ref _sample)) return;
            long covered = _sample.Seq - _lastSeq;
            _lastSeq = _sample.Seq;
            if (covered < 1) covered = 1;
            if (covered > 1000000) covered = 1000000;
            _filter.Push(_sample.RecvSec, _sample.T, _sample.Q, (int)covered);
        }

        void ReportReceiver(double nowSec)
        {
            ImuUdpReceiver receiver = _receiver;
            if (receiver == null) return;

            if (receiver.TakeListening()) UGALog.Event("imu_listening", "port", _port);
            string failure = receiver.TakeFailure();
            if (failure != null) UGALog.Warn("imu_bind_failed", failure + "; staying head-locked, retrying every 2 s");

            if (nowSec >= _nextRejectCheckSec)
            {
                _nextRejectCheckSec = nowSec + RejectReportIntervalSec;
                long rejected = receiver.RejectedCount;
                if (rejected != _reportedRejected)
                {
                    UGALog.Warn("imu_packets_rejected", (rejected - _reportedRejected) + " datagram(s) on port " + _port
                        + " were not valid IMU packets (" + receiver.ReceivedCount + " accepted so far)");
                    _reportedRejected = rejected;
                }
            }
        }

        bool Evaluate(double nowSec)
        {
            HeadTrackingState state = _anchorHeadLocked ? HeadTrackingState.HeadLocked : _filter.GetState(nowSec);
            _rotation = state == HeadTrackingState.Tracking ? _filter.GetRotation(nowSec) : Quaternion.identity;
            _packetRateHz = _filter.GetPacketRateHz(nowSec);
            if (state == _state && _stateLogged) return false;

            // One line per change, plus one for the state we start in (so a log always says why the head is locked,
            // even when anchorMode forces it from the first frame and the state never "changes").
            bool changed = state != _state;
            _state = state;
            _stateLogged = true;
            UGALog.Event("imu_state", "state", state == HeadTrackingState.Tracking ? "Tracking" : "HeadLocked", "reason", Reason(state));
            return changed;
        }

        string Reason(HeadTrackingState state)
        {
            if (state == HeadTrackingState.Tracking) return "packets";
            if (_anchorHeadLocked) return "anchorMode";
            return _filter.HasSample ? "stale" : "no_packets";
        }
    }
}
