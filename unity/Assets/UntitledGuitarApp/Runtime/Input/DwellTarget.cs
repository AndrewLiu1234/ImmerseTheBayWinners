// DwellTarget: something the wearer activates by looking at it (or by pressing Space while looking at it).
//
// It is only data and a timer: a world position (the transform), an angular radius in degrees, a label, a Progress and a
// Fired event. It draws nothing; whoever builds the button (HUD) draws it and may read Progress / State to animate it.
// It registers itself with GazeDwell when enabled and unregisters when disabled or destroyed, so no wiring is needed.
//
//   DwellTarget t = DwellTarget.Create("Start", worldPosition, 4f);   // 4 degrees: a large button
//   t.Fired += () => StartLesson();
//
// Nothing happens until a GazeDwell exists and has a Head (GazeDwell.GetOrCreate().Head = ...).
using System;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Gaze
{
    public sealed class DwellTarget : MonoBehaviour, IDwellItem
    {
        /// <summary>Large targets (docs/MASTER_PLAN.md section 16): the display is only about 46 x 27 degrees wide.</summary>
        public const float DefaultAngularRadiusDeg = 3f;

        /// <summary>A dwell survives until the gaze is this many times the radius away (hysteresis).</summary>
        public const float DefaultExitScale = 1.25f;

        readonly DwellTimer timer = new DwellTimer();
        float angularRadiusDeg = DefaultAngularRadiusDeg;
        float exitScale = DefaultExitScale;

        /// <summary>Name for logs and UI. Not used for matching.</summary>
        public string Label { get; set; } = "";

        /// <summary>World position the wearer must look at (the transform's position).</summary>
        public Vector3 Position
        {
            get { return transform.position; }
            set { transform.position = value; }
        }

        /// <summary>
        /// Angular radius in degrees, as seen from the head. A dwell starts when the gaze is within this angle of
        /// Position. Independent of distance: a 3 degree target is the same size on the display at 0.5 m or 5 m.
        /// </summary>
        public float AngularRadiusDeg
        {
            get { return angularRadiusDeg; }
            set { angularRadiusDeg = value > 0f ? value : 0f; SyncThresholds(); }
        }

        /// <summary>The exit angle is AngularRadiusDeg * ExitScale (at least 1). The gaze may drift this far out before the dwell is lost.</summary>
        public float ExitScale
        {
            get { return exitScale; }
            set { exitScale = value > 1f ? value : 1f; SyncThresholds(); }
        }

        /// <summary>The state machine. Tune DwellSeconds and CooldownSeconds on it; Enter/ExitAngleDeg follow AngularRadiusDeg.</summary>
        public DwellTimer Timer { get { return timer; } }

        /// <summary>0..1 while the wearer is dwelling on this target; 1 for the frame it fires; otherwise 0.</summary>
        public float Progress { get { return timer.Progress; } }

        public DwellState State { get { return timer.State; } }

        /// <summary>Raised on the main thread, from GazeDwell's LateUpdate, when the dwell completes or Space fires this target.</summary>
        public event Action Fired;

        void Awake()
        {
            SyncThresholds();
        }

        void OnEnable()
        {
            GazeDwell.Register(this);
        }

        void OnDisable()
        {
            GazeDwell.Unregister(this);
            timer.Reset();
        }

        void SyncThresholds()
        {
            timer.EnterAngleDeg = angularRadiusDeg;
            timer.ExitAngleDeg = angularRadiusDeg * exitScale;
        }

        internal void RaiseFired()
        {
            Action handler = Fired;
            if (handler != null) handler();
        }

        /// <summary>Creates a GameObject at <paramref name="worldPosition"/> with a DwellTarget on it.</summary>
        public static DwellTarget Create(string label, Vector3 worldPosition, float angularRadiusDeg, Transform parent = null)
        {
            var go = new GameObject("DwellTarget " + label);
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.position = worldPosition;
            DwellTarget target = go.AddComponent<DwellTarget>();
            target.Label = label ?? "";
            target.AngularRadiusDeg = angularRadiusDeg;
            return target;
        }
    }
}
