// GazeDwell: hands-free input. The wearer's hands are on a guitar, so the app is driven by looking: each frame this finds
// the registered DwellTarget nearest the head's forward ray (within that target's angular radius), advances its timer, and
// raises events. The Space bar fires the focused target at once, or raises Confirmed when nothing is focused.
// "Gaze" is the head's forward direction: the glasses only report head rotation (docs/MASTER_PLAN.md section 20).
//
// This MonoBehaviour is a thin shell: the per-frame logic (focus, hysteresis, cooldown, confirm) is DwellEngine, plain C#
// with EditMode tests. GazeDwell reads the head, keeps the registry of targets, and turns the engine's results into events.
//
// Wiring, all from code:
//   GazeDwell gaze = GazeDwell.GetOrCreate();
//   gaze.Head = rig.HeadTransform;                           // the transform whose forward is the gaze
//   gaze.TargetFired += t => Debug.Log(t.Label);
//   var reticle = new GameObject("Reticle").AddComponent<Reticle>(); reticle.Gaze = gaze;
//   DwellTarget.Create("Next", worldPosition, 4f).Fired += OnNext;
// Setting Head to null switches gaze selection off (use that in headLocked mode, where everything moves with the head);
// the Space bar keeps working.
//
// Everything runs on the main thread in LateUpdate (after the head pose has been updated this frame). No physics, no
// colliders, no per-frame allocations: targets are copied into a preallocated array, which only grows when the number of
// targets does. Listener exceptions are caught and logged so one bad handler cannot stop the others.
using System;
using System.Collections.Generic;
using UnityEngine;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Gaze
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(500)]
    public sealed class GazeDwell : MonoBehaviour
    {
        /// <summary>
        /// The most time one frame may advance a dwell by. A hitch (scene load, GC) must not complete a dwell the
        /// wearer was not looking at for that long.
        /// </summary>
        public const float MaxFrameSeconds = 0.1f;

        // Targets register themselves here. Static, so a target can be created before any GazeDwell exists.
        static readonly List<DwellTarget> Registered = new List<DwellTarget>(16);
        static GazeDwell active;

        /// <summary>The running GazeDwell, or null if there is none.</summary>
        public static GazeDwell Instance { get { return active; } }

        /// <summary>Returns the running GazeDwell, creating one on a new GameObject if there is none.</summary>
        public static GazeDwell GetOrCreate()
        {
            if (active != null) return active;
            return new GameObject("GazeDwell").AddComponent<GazeDwell>();
        }

        internal static void Register(DwellTarget target)
        {
            if (target != null && !Registered.Contains(target)) Registered.Add(target);
        }

        internal static void Unregister(DwellTarget target)
        {
            Registered.Remove(target);
        }

        readonly DwellEngine engine = new DwellEngine();
        DwellTarget[] snapshot = new DwellTarget[16];   // grows only when more targets exist than ever before
        bool confirmRequested;

        /// <summary>The transform whose forward direction is the gaze (the camera rig's head). null = no gaze selection.</summary>
        public Transform Head { get; set; }

        /// <summary>Key that fires the focused target at once, or raises Confirmed. KeyCode.None disables it. Legacy Input only.</summary>
        public KeyCode ConfirmKey = KeyCode.Space;

        /// <summary>The target the gaze is on, or null. Becomes null for a frame after a fire (the target is in cooldown).</summary>
        public DwellTarget Focused
        {
            get
            {
                DwellTarget f = engine.Focused as DwellTarget;
                return f != null ? f : null;   // a destroyed target reads as null
            }
        }

        /// <summary>Progress (0..1) of the focused target's dwell; 0 when nothing is focused.</summary>
        public float FocusProgress
        {
            get
            {
                DwellTarget f = Focused;
                return f != null ? f.Progress : 0f;
            }
        }

        /// <summary>True when Head was set on the last frame, i.e. gaze selection is live.</summary>
        public bool HasGaze { get; private set; }

        /// <summary>World position of the gaze ray's origin on the last frame the head was set. Read it from a Fired handler to capture the ray.</summary>
        public Vector3 GazeOrigin { get; private set; }

        /// <summary>World direction (the head's forward) of the gaze ray on the last frame the head was set.</summary>
        public Vector3 GazeDirection { get; private set; } = Vector3.forward;

        /// <summary>The gaze moved onto a target (it is now Focused).</summary>
        public event Action<DwellTarget> TargetEntered;

        /// <summary>The gaze left a target, or the target stopped being selectable (fired, disabled).</summary>
        public event Action<DwellTarget> TargetExited;

        /// <summary>A target's dwell completed, or Space fired it. DwellTarget.Fired is raised just before this.</summary>
        public event Action<DwellTarget> TargetFired;

        /// <summary>Space was pressed (or Confirm() called) while no target was focused: a global "OK".</summary>
        public event Action Confirmed;

        /// <summary>
        /// Same as pressing the confirm key: fires the focused target, or raises Confirmed if none is focused. Takes
        /// effect in this frame's LateUpdate (or the next frame's, if called after it). For foot pedals, sim rigs and tests.
        /// </summary>
        public void Confirm()
        {
            confirmRequested = true;
        }

        void OnEnable()
        {
            if (active != null && active != this)
            {
                UGALog.Warn("gaze_duplicate", "a second GazeDwell was disabled; only one may run at a time");
                enabled = false;
                return;
            }
            active = this;
        }

        void OnDisable()
        {
            if (active != this) return;   // a rejected duplicate must not disturb the running one
            active = null;
            // Nothing will advance the targets any more: release whatever was half dwelled on (cooldowns are left alone).
            for (int i = 0; i < Registered.Count; i++)
            {
                DwellTarget t = Registered[i];
                if (t != null) t.Timer.Tick(float.PositiveInfinity, 0f);
            }
            engine.Reset();
        }

        void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > MaxFrameSeconds) dt = MaxFrameSeconds;

            int count = TakeSnapshot();

            Transform head = Head;
            bool hasGaze = head != null;
            HasGaze = hasGaze;
            if (hasGaze)
            {
                GazeOrigin = head.position;
                GazeDirection = head.forward;
            }

            bool confirm = confirmRequested;
            confirmRequested = false;
            if (!confirm && ConfirmKey != KeyCode.None && Input.GetKeyDown(ConfirmKey)) confirm = true;

            engine.Step(GazeOrigin, GazeDirection, hasGaze, snapshot, count, dt, confirm);

            // Raise events only now that all state is settled: handlers may create, disable or destroy targets.
            if (engine.FocusChanged)
            {
                DwellTarget previous = engine.Previous as DwellTarget;
                DwellTarget next = engine.Focused as DwellTarget;
                if (previous != null)
                {
                    UGALog.Event("gaze_exit", "label", previous.Label ?? "");
                    Raise(TargetExited, previous);
                }
                if (next != null)
                {
                    UGALog.Event("gaze_enter", "label", next.Label ?? "");
                    Raise(TargetEntered, next);
                }
            }

            for (int i = 0; i < engine.FiredCount; i++)
            {
                DwellTarget t = engine.FiredAt(i) as DwellTarget;
                if (t == null) continue;
                bool bySpace = ReferenceEquals(engine.Forced, t);
                UGALog.Event("gaze_fire", "label", t.Label ?? "", "via", bySpace ? "space" : "dwell");
                RaiseFired(t);
                Raise(TargetFired, t);
            }

            if (engine.ConfirmedRaised)
            {
                UGALog.Event("gaze_confirm");
                Raise(Confirmed);
            }

            Array.Clear(snapshot, 0, count);
        }

        /// <summary>Copies the live targets into the preallocated snapshot array; returns how many.</summary>
        int TakeSnapshot()
        {
            for (int i = Registered.Count - 1; i >= 0; i--)
            {
                if (Registered[i] == null) Registered.RemoveAt(i);   // destroyed without a chance to unregister
            }

            int count = Registered.Count;
            if (count > snapshot.Length)
            {
                int capacity = snapshot.Length;
                while (capacity < count) capacity *= 2;
                Array.Resize(ref snapshot, capacity);
            }
            for (int i = 0; i < count; i++) snapshot[i] = Registered[i];
            return count;
        }

        static void Raise(Action<DwellTarget> handler, DwellTarget target)
        {
            if (handler == null) return;
            try { handler(target); }
            catch (Exception e) { UGALog.Error("gaze_handler_exception", e.ToString()); }
        }

        static void Raise(Action handler)
        {
            if (handler == null) return;
            try { handler(); }
            catch (Exception e) { UGALog.Error("gaze_handler_exception", e.ToString()); }
        }

        static void RaiseFired(DwellTarget target)
        {
            try { target.RaiseFired(); }
            catch (Exception e) { UGALog.Error("gaze_handler_exception", e.ToString()); }
        }
    }
}
