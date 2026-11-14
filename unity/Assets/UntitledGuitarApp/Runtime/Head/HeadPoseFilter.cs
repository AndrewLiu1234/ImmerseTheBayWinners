// HeadPoseFilter: turns the stream of IMU samples into the wearer's head rotation. Plain C#, no MonoBehaviour, and it
// never reads a clock: every call that depends on time takes `nowSec`, so the tests are deterministic.
//
// Pipeline for GetRotation(nowSec):
//   newest sample -> predict ahead (angular velocity of the last two samples, clamped)
//                 -> invertYaw / invertPitch / invertRoll (signs of the yaw, pitch and roll angles)
//                 -> yaw offset set by Recenter()
// and it is identity whenever the state is HeadLocked (no sample yet, or none for staleMs).
//
// Two clocks are involved and they are never mixed. `recvSec` / `nowSec` are the Unity side's clock (when a packet arrived,
// what time it is now). `packetT` is the bridge's own timestamp, used only for differences (angular velocity), because
// it is far steadier than arrival times over UDP.
//
// Angle conventions are Unity's Quaternion.Euler(x, y, z): q = Ry(yaw) * Rx(pitch) * Rz(roll). Positive yaw turns toward
// +X (to the right), positive pitch looks down, positive roll tilts toward +Y.
//
// Everything is managed double-precision maths: no UnityEngine.Quaternion.Euler / Inverse / Slerp / AngleAxis (those are
// native calls), so the filter is deterministic, thread-agnostic and runs in a plain .NET host. Not thread-safe itself:
// use it from one thread (ImuHeadPose uses it from the main thread).

using System;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Head
{
    public enum HeadTrackingState
    {
        /// No usable IMU data (none yet, stale, or anchorMode forces it): Rotation is identity and content follows the head.
        HeadLocked = 0,
        /// Fresh IMU data: Rotation follows the wearer's head.
        Tracking = 1
    }

    public sealed class HeadPoseFilter
    {
        public const float DefaultStaleMs = 250f;
        public const float DefaultPredictionMs = 10f;
        /// Settings below this are raised to it (flags.json typos must not make the head permanently stale).
        public const float MinStaleMs = 20f;
        public const float MaxPredictionMs = 100f;

        /// Angular speeds above this are clamped before extrapolating (a glitchy sample pair must not fling the view).
        public const double MaxAngularSpeedDegPerSec = 720.0;
        /// Cap on the whole look-ahead: age of the newest sample plus predictionMs.
        public const double MaxHorizonSec = 0.05;
        /// Two samples closer than this in bridge time give no velocity (their difference is mostly noise).
        public const double MinBaselineSec = 0.0005;
        /// Two samples further apart than this are a gap, not motion: no prediction.
        public const double MaxBaselineSec = 0.1;
        /// Time constant of the packet-rate average.
        public const double RateWindowSec = 0.5;

        const double MaxAngularSpeedRad = MaxAngularSpeedDegPerSec * (Math.PI / 180.0);
        const double MinRateTimeSec = 0.01;       // averaging time needed before a rate is reported
        const double PoleSine = 1.0 - 1e-9;       // |sin(pitch)| above this counts as looking straight up or down

        double _staleSec = DefaultStaleMs / 1000.0;
        double _predictionSec = DefaultPredictionMs / 1000.0;
        bool _invertYaw;
        bool _invertPitch;
        bool _invertRoll;

        bool _hasLatest;
        double _recvSec;        // arrival time of the newest sample
        double _t1;             // bridge time of the newest sample
        Quat _q1 = Quat.Identity;
        bool _hasPrev;
        double _t0;             // bridge time of the sample the velocity is measured from
        Quat _q0 = Quat.Identity;

        double _yawOffsetRad;
        Quat _yawOffset = Quat.Identity;

        bool _rateStarted;
        double _rateLastSec;
        double _rateCount;      // exponentially decayed packet count ...
        double _rateTime;       // ... and the equally decayed time it was counted over

        /// True once at least one sample has been accepted.
        public bool HasSample { get { return _hasLatest; } }

        /// The yaw offset (radians) that Recenter() applies in front of the head rotation. Persist it across domain
        /// reloads if wanted; assigning it directly is allowed.
        public double YawOffsetRadians
        {
            get { return _yawOffsetRad; }
            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value)) value = 0.0;
                _yawOffsetRad = value;
                _yawOffset = value == 0.0 ? Quat.Identity : Quat.FromYaw(value);
            }
        }

        /// Applies the flags. Cheap enough to call every frame. Out-of-range values are clamped, NaN falls back to the default.
        public void Configure(float staleMs, float predictionMs, bool invertYaw, bool invertPitch, bool invertRoll)
        {
            if (float.IsNaN(staleMs) || float.IsInfinity(staleMs)) staleMs = DefaultStaleMs;
            if (staleMs < MinStaleMs) staleMs = MinStaleMs;
            if (float.IsNaN(predictionMs) || float.IsInfinity(predictionMs)) predictionMs = DefaultPredictionMs;
            if (predictionMs < 0f) predictionMs = 0f;
            if (predictionMs > MaxPredictionMs) predictionMs = MaxPredictionMs;
            _staleSec = staleMs / 1000.0;
            _predictionSec = predictionMs / 1000.0;
            _invertYaw = invertYaw;
            _invertPitch = invertPitch;
            _invertRoll = invertRoll;
        }

        /// <summary>
        /// Feeds one sample. recvSec: when it arrived (caller's clock). packetT: the bridge's timestamp in seconds.
        /// packetCount: how many datagrams this call stands for, 1 when every packet is pushed and larger when the caller
        /// only sees the newest of several (the packet-rate estimate counts datagrams, not calls).
        /// A zero, NaN or infinite quaternion, or a non-finite time, is ignored.
        /// </summary>
        public void Push(double recvSec, double packetT, Quaternion q, int packetCount = 1)
        {
            if (!IsFinite(recvSec) || !IsFinite(packetT)) return;
            double len2 = (double)q.x * q.x + (double)q.y * q.y + (double)q.z * q.z + (double)q.w * q.w;
            if (!IsFinite(len2) || len2 < 1e-12) return;
            double inv = 1.0 / Math.Sqrt(len2);
            Quat cur = new Quat(q.x * inv, q.y * inv, q.z * inv, q.w * inv);

            UpdateRate(recvSec, packetCount);

            if (!_hasLatest || !(packetT > _t1))
            {
                _hasPrev = false;       // first sample, or the bridge's clock did not advance (it restarted): no velocity yet
            }
            else
            {
                _t0 = _t1;              // velocity is measured between the last two samples
                _q0 = _q1;
                _hasPrev = true;
            }

            _t1 = packetT;
            _q1 = cur;
            _recvSec = recvSec;
            _hasLatest = true;
        }

        /// Forgets all samples and the rate estimate. The settings and the yaw offset stay.
        public void Reset()
        {
            _hasLatest = false;
            _hasPrev = false;
            _rateStarted = false;
            _rateCount = 0.0;
            _rateTime = 0.0;
        }

        /// Tracking while a sample is at most staleMs old (exactly staleMs still counts as fresh); HeadLocked otherwise.
        public HeadTrackingState GetState(double nowSec)
        {
            return IsFresh(nowSec) ? HeadTrackingState.Tracking : HeadTrackingState.HeadLocked;
        }

        /// The head rotation to display at nowSec: predicted ahead, inverted per flags, recentered; identity when HeadLocked.
        public Quaternion GetRotation(double nowSec)
        {
            if (!IsFresh(nowSec)) return Quaternion.identity;
            Quat q = Predict(nowSec);
            q = ApplyInversion(q);
            q = (_yawOffset * q).Normalized();
            return q.ToUnity();
        }

        /// Packets per second, averaged over about RateWindowSec of arrival time; 0 while HeadLocked or not yet known.
        public float GetPacketRateHz(double nowSec)
        {
            if (!IsFresh(nowSec) || _rateTime < MinRateTimeSec) return 0f;
            return (float)(_rateCount / _rateTime);
        }

        /// <summary>
        /// Makes the current heading "forward": sets a yaw-only offset so the newest sample (after the invert flags, before
        /// prediction) faces +Z. Pitch and roll are untouched. Absolute, not cumulative: calling it twice changes nothing.
        /// Returns false, changing nothing, when there is no fresh sample.
        /// </summary>
        public bool Recenter(double nowSec)
        {
            if (!IsFresh(nowSec)) return false;
            double yaw, pitch, roll;
            ToEuler(ApplyInversion(_q1), out yaw, out pitch, out roll);
            YawOffsetRadians = -yaw;
            return true;
        }

        /// Removes the Recenter() offset.
        public void ClearRecenter()
        {
            YawOffsetRadians = 0.0;
        }

        // ------------------------------------------------------------------------------------------------ internals

        bool IsFresh(double nowSec)
        {
            return _hasLatest && (nowSec - _recvSec) <= _staleSec;     // false for a NaN nowSec
        }

        static bool IsFinite(double v)
        {
            return !(double.IsNaN(v) || double.IsInfinity(v));
        }

        // Extrapolates the newest sample along the world-frame rotation that took the previous sample to it.
        Quat Predict(double nowSec)
        {
            Quat q1 = _q1;
            if (!_hasPrev || !(_predictionSec > 0.0)) return q1;     // predictionMs 0 switches extrapolation off completely
            double dt = _t1 - _t0;
            if (!(dt >= MinBaselineSec) || dt > MaxBaselineSec) return q1;

            double horizon = nowSec - _recvSec;     // how long the newest sample has been waiting ...
            if (horizon < 0.0) horizon = 0.0;
            horizon += _predictionSec;              // ... plus the latency we are asked to hide
            if (horizon > MaxHorizonSec) horizon = MaxHorizonSec;
            if (!(horizon > 0.0)) return q1;

            Quat d = q1 * _q0.Conjugate();          // q1 = d * q0
            if (d.W < 0.0) d = new Quat(-d.X, -d.Y, -d.Z, -d.W);   // take the short way round
            double s = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            if (s < 1e-12) return q1;               // not turning

            double angle = 2.0 * Math.Atan2(s, d.W);
            double speed = angle / dt;
            if (speed > MaxAngularSpeedRad) speed = MaxAngularSpeedRad;
            double half = 0.5 * speed * horizon;
            double k = Math.Sin(half) / s;
            Quat step = new Quat(d.X * k, d.Y * k, d.Z * k, Math.Cos(half));
            return (step * q1).Normalized();
        }

        Quat ApplyInversion(Quat q)
        {
            if (!_invertYaw && !_invertPitch && !_invertRoll) return q;
            double yaw, pitch, roll;
            ToEuler(q, out yaw, out pitch, out roll);
            return FromEuler(_invertYaw ? -yaw : yaw, _invertPitch ? -pitch : pitch, _invertRoll ? -roll : roll);
        }

        // Rate = decayed packet count / decayed time. A gap long enough that the stream was effectively down (fewer than one
        // packet per stale interval over the span) restarts the average, so a pause never drags the estimate down afterwards.
        void UpdateRate(double recvSec, int packetCount)
        {
            if (packetCount < 1) packetCount = 1;
            if (!_rateStarted)
            {
                _rateStarted = true;
                _rateLastSec = recvSec;
                _rateCount = 0.0;
                _rateTime = 0.0;
                return;
            }

            double dt = recvSec - _rateLastSec;
            if (!(dt > 0.0))
            {
                _rateCount += packetCount;     // same instant (or a clock step back): count them, spend no time
                return;
            }
            _rateLastSec = recvSec;
            if (packetCount * _staleSec < dt)
            {
                _rateCount = 0.0;
                _rateTime = 0.0;
                return;
            }
            double decay = Math.Exp(-dt / RateWindowSec);
            _rateCount = _rateCount * decay + packetCount;
            _rateTime = _rateTime * decay + dt;
        }

        // q = Ry(yaw) * Rx(pitch) * Rz(roll). Derived from the rotation matrix of the product:
        //   R02 = sin(yaw)cos(pitch)   R22 = cos(yaw)cos(pitch)   R12 = -sin(pitch)   R10 = cos(pitch)sin(roll)   R11 = cos(pitch)cos(roll)
        static void ToEuler(Quat q, out double yaw, out double pitch, out double roll)
        {
            double sinPitch = 2.0 * (q.X * q.W - q.Y * q.Z);
            if (sinPitch > 1.0) sinPitch = 1.0;
            else if (sinPitch < -1.0) sinPitch = -1.0;
            pitch = Math.Asin(sinPitch);
            if (Math.Abs(sinPitch) < PoleSine)
            {
                yaw = Math.Atan2(2.0 * (q.X * q.Z + q.Y * q.W), 1.0 - 2.0 * (q.X * q.X + q.Y * q.Y));
                roll = Math.Atan2(2.0 * (q.X * q.Y + q.Z * q.W), 1.0 - 2.0 * (q.X * q.X + q.Z * q.Z));
            }
            else
            {
                // Straight up or down: yaw and roll turn about the same axis, only their sum or difference is defined.
                yaw = Math.Atan2(-2.0 * (q.X * q.Z - q.Y * q.W), 1.0 - 2.0 * (q.Y * q.Y + q.Z * q.Z));
                roll = 0.0;
            }
        }

        static Quat FromEuler(double yaw, double pitch, double roll)
        {
            double hp = pitch * 0.5;
            double hr = roll * 0.5;
            Quat qx = new Quat(Math.Sin(hp), 0.0, 0.0, Math.Cos(hp));
            Quat qz = new Quat(0.0, 0.0, Math.Sin(hr), Math.Cos(hr));
            return Quat.FromYaw(yaw) * qx * qz;
        }

        // Double-precision quaternion with the same Hamilton product as UnityEngine.Quaternion.
        readonly struct Quat
        {
            public readonly double X, Y, Z, W;

            public static readonly Quat Identity = new Quat(0.0, 0.0, 0.0, 1.0);

            public Quat(double x, double y, double z, double w)
            {
                X = x; Y = y; Z = z; W = w;
            }

            public static Quat operator *(Quat a, Quat b)
            {
                return new Quat(
                    a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                    a.W * b.Y + a.Y * b.W + a.Z * b.X - a.X * b.Z,
                    a.W * b.Z + a.Z * b.W + a.X * b.Y - a.Y * b.X,
                    a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
            }

            public Quat Conjugate()
            {
                return new Quat(-X, -Y, -Z, W);
            }

            public Quat Normalized()
            {
                double len = Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
                if (!(len > 1e-12)) return Identity;
                double k = 1.0 / len;
                return new Quat(X * k, Y * k, Z * k, W * k);
            }

            public static Quat FromYaw(double radians)
            {
                double h = radians * 0.5;
                return new Quat(0.0, Math.Sin(h), 0.0, Math.Cos(h));
            }

            public Quaternion ToUnity()
            {
                return new Quaternion((float)X, (float)Y, (float)Z, (float)W);
            }
        }
    }
}
