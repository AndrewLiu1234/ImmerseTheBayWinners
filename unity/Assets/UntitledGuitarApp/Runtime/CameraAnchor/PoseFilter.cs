// PoseFilter (SL12): smooths the camera-vision bridge's marker pose stream and rejects outliers.
// Pure math on UnityEngine structs (Vector3/Quaternion are value types; no native engine calls), so it runs
// in the offline mono-tests (kit/mono-tests.sh) and in EditMode.
//
// Contract with the bridge (tools/camera-vision/camera_bridge.py): poses arrive at ~30 Hz as
// (position mm in the camera frame, quaternion xyzw). The filter:
//   * rejects a sample that jumps more than maxJumpMm / maxJumpDeg from the last accepted sample (a detection glitch);
//   * exponentially smooths translation and slerps rotation (alpha per sample);
//   * reports Lost when no sample arrived for lostTimeoutS (freeze the last alignment until the marker returns).
// Zero allocations per call after construction.
using System;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.CameraAnchor
{
    public struct CameraPose
    {
        public Vector3 Pos;          // marker origin, camera frame, metres
        public Quaternion Rot;       // marker orientation, camera frame
    }

    public sealed class PoseFilter
    {
        public readonly float PosAlpha;
        public readonly float RotAlpha;
        public readonly float MaxJumpMm;
        public readonly float MaxJumpDeg;
        public readonly float LostTimeoutS;

        bool _hasLast;
        CameraPose _lastAccepted;
        CameraPose _smoothed;
        double _lastFeedSec = double.NegativeInfinity;
        long _rejected;

        public PoseFilter(float posAlpha = 0.35f, float rotAlpha = 0.35f, float maxJumpMm = 50f,
                          float maxJumpDeg = 20f, float lostTimeoutS = 0.6f)
        {
            PosAlpha = posAlpha;
            RotAlpha = rotAlpha;
            MaxJumpMm = maxJumpMm;
            MaxJumpDeg = maxJumpDeg;
            LostTimeoutS = lostTimeoutS;
        }

        /// <summary>Samples rejected as outliers since the last reset (detection glitches).</summary>
        public long Rejected
        {
            get { return _rejected; }
        }

        /// <summary>True when no sample arrived within <see cref="LostTimeoutS"/> seconds.</summary>
        public bool Lost(double nowSec)
        {
            return !_hasLast || nowSec - _lastFeedSec > LostTimeoutS;
        }

        /// <summary>Feed one pose sample. Returns false when it was rejected as an outlier (the output is unchanged).</summary>
        public bool Feed(CameraPose sample, double nowSec)
        {
            if (_hasLast && (nowSec - _lastFeedSec) > LostTimeoutS)
            {
                // the marker moved while it was unseen: snap to the new pose instead of easing from the stale one
                _smoothed = sample;
                _lastAccepted = sample;
                _lastFeedSec = nowSec;
                return true;
            }
            if (_hasLast && nowSec >= _lastFeedSec)
            {
                float jumpMm = (sample.Pos - _lastAccepted.Pos).magnitude * 1000f;   // Pos is metres, MaxJumpMm is mm
                float jumpDeg = Quaternion.Angle(sample.Rot, _lastAccepted.Rot);
                if (jumpMm > MaxJumpMm || jumpDeg > MaxJumpDeg)
                {
                    _rejected++;
                    _lastFeedSec = nowSec;   // a glitch still counts as "the marker was seen"
                    return false;
                }
            }
            _lastFeedSec = nowSec;
            _lastAccepted = sample;
            if (!_hasLast)
            {
                _smoothed = sample;          // first sample after a loss: snap, don't ease
                _hasLast = true;
                return true;
            }
            _smoothed.Pos = Vector3.Lerp(_smoothed.Pos, sample.Pos, PosAlpha);
            _smoothed.Rot = Quaternion.Slerp(_smoothed.Rot, sample.Rot, RotAlpha);
            return true;
        }

        /// <summary>The smoothed pose (the last one accepted; valid when the filter has ever been fed).</summary>
        public bool TryGetSmoothed(out CameraPose pose)
        {
            pose = _smoothed;
            return _hasLast;
        }
    }
}
