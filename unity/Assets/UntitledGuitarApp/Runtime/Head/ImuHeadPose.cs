// ImuHeadPose: the wearer's head rotation from the glasses' IMU. Add it from code and set Target:
//
//     var head = new GameObject("Head").AddComponent<ImuHeadPose>();
//     head.Target = rigTransform;                  // Update sets rigTransform.localRotation every frame
//
// The IMU bridge (tools/imu-bridge) sends the orientation as UDP JSON to 127.0.0.1:<FlagStore.Current.imu.udpPort>. A
// background thread receives it; this component applies the newest sample once per frame. When packets stop for
// imu.staleMs, or anchorMode is "headLocked", State is HeadLocked and Rotation is identity (content follows the head).
// Target may stay null if you only want to read State / Rotation. If anything else also rotates Target (the editor
// simulation rig, say), disable this component or leave Target empty so the two do not fight.
//
// All the logic lives in ImuHeadPoseCore, HeadPoseFilter, ImuUdpReceiver and ImuPacketParser (plain C#, tested without
// Play Mode). This class only owns the lifetime: everything is created in OnEnable and torn down in OnDisable /
// OnDestroy / OnApplicationQuit (the thread is joined and the socket closed), so Play Mode exits and script reloads
// leave nothing behind.

using System;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Head
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]   // run before ordinary scripts so they read this frame's rotation
    public sealed class ImuHeadPose : MonoBehaviour
    {
        [SerializeField] Transform _target;
        [SerializeField, HideInInspector] float _yawOffsetRad;   // keeps Recenter() across a script reload

        ImuHeadPoseCore _core;

        /// The transform whose localRotation follows the head. May be null.
        public Transform Target
        {
            get { return _target; }
            set { _target = value; }
        }

        /// HeadLocked until the first valid packet and whenever packets stop or anchorMode is "headLocked".
        public HeadTrackingState State { get { return _core != null ? _core.State : HeadTrackingState.HeadLocked; } }

        /// The head rotation as of this frame's Update; identity when HeadLocked.
        public Quaternion Rotation { get { return _core != null ? _core.Rotation : Quaternion.identity; } }

        /// Packets per second the bridge is delivering; 0 when no packets are arriving.
        public float PacketRateHz { get { return _core != null ? _core.PacketRateHz : 0f; } }

        /// Raised from Update (main thread) after State changes. Not raised when the component is disabled.
        public event Action<HeadTrackingState> StateChanged;

        /// Makes the direction the wearer currently faces "forward" (yaw only; pitch and roll are kept). Do it before
        /// aligning the neck: recentering afterwards shifts the world. Ignored, with a log line, while HeadLocked.
        public void Recenter()
        {
            if (_core == null)
            {
                UGALog.Warn("imu_recenter", "ignored: ImuHeadPose is not enabled");
                return;
            }
            _core.Recenter(ImuUdpReceiver.NowSec());
            ApplyToTarget();
        }

        void OnEnable()
        {
            _core = new ImuHeadPoseCore();
            _core.YawOffsetRadians = _yawOffsetRad;
        }

        void Update()
        {
            ImuHeadPoseCore core = _core;
            if (core == null) return;
            bool changed = core.Tick(ImuUdpReceiver.NowSec(), FlagStore.Current);
            ApplyToTarget();
            if (changed)
            {
                Action<HeadTrackingState> handler = StateChanged;
                if (handler != null) handler(core.State);
            }
        }

        void OnDisable() { Release(); }
        void OnDestroy() { Release(); }
        void OnApplicationQuit() { Release(); }

        void Release()
        {
            ImuHeadPoseCore core = _core;
            if (core == null) return;
            _core = null;
            _yawOffsetRad = (float)core.YawOffsetRadians;
            core.Shutdown();
        }

        void ApplyToTarget()
        {
            Transform target = _target;
            ImuHeadPoseCore core = _core;
            if (target != null && core != null) target.localRotation = core.Rotation;
        }
    }
}
