// EditorSimHead: test the app in Play Mode without the glasses. Hold the RIGHT mouse button in the Game view and drag to turn
// the head like a mouse-look camera. Attach it with the menu "UGA/Editor Sim Rig" (Editor/EditorSimRig.cs) or from code with
// EditorSimHead.Attach(head).
//
// Why it lives in Runtime (inside #if UNITY_EDITOR) and not in the Editor assembly: "Unity doesn't allow components derived
// from MonoBehaviour to be assigned to GameObjects if the scripts are in the Editor folder" (Manual/SpecialFolders.html,
// 2021.3), so AddComponent of the old Editor-assembly EditorSimRig failed with "Can't add script behaviour ... because it is an
// editor script" while the menu still said "attached". The class does not exist in player builds.
//
// When it writes the head: in Update at execution order -90 (DefaultExecutionOrder: "Scripts are executed in order from
// lowest first to highest last"), i.e. right AFTER ImuHeadPose (-100), which writes the same Transform in its Update (identity
// while head-locked), and BEFORE every script that reads the head this frame: SpectatorCam and FloatingNeck (0), GazeDwell
// (500), Reticle (1000), NeckHudCard (9000), LearnHud (20000). So while attached the sim head wins and every reader sees it in
// the same frame. ImuHeadPose is left alone (its Target and API untouched): detaching needs no restore, and a script reload
// cannot leave the IMU disconnected.
//
// Input: the legacy UnityEngine.Input class (mouse button + mouse position), which Unity 2021.3 uses unless the project
// switches to the Input System package (not installed; packages are frozen). If that ever happens Input throws
// InvalidOperationException; the rig catches it once, logs a warning and stops instead of spamming the console.
using System;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Head
{
    /// <summary>The mouse-look maths (plain C#, also compiled into players so the tests run anywhere).</summary>
    public static class SimHeadLook
    {
        /// <summary>
        /// Turns a mouse movement into new yaw and pitch angles (degrees). Mouse right = turn right; mouse up = look up
        /// (negative pitch about X in Unity) unless <paramref name="invertY"/>. Pitch is clamped to +-maxPitch, yaw wraps to 0..360.
        /// </summary>
        public static void ApplyLook(ref float yawDeg, ref float pitchDeg, float dxPixels, float dyPixels,
            float degreesPerPixel, float maxPitch, bool invertY)
        {
            yawDeg = Mathf.Repeat(yawDeg + dxPixels * degreesPerPixel, 360f);
            float dp = dyPixels * degreesPerPixel;
            pitchDeg = Mathf.Clamp(pitchDeg + (invertY ? dp : -dp), -maxPitch, maxPitch);
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// Mouse-look for the Transform it is attached to (normally the head, <c>StereoRig.Head</c>). While attached it owns that
    /// Transform's rotation: it writes it every frame in Update at order <see cref="ExecutionOrder"/>, after ImuHeadPose and
    /// before every head reader. Remove it (menu, or <see cref="Detach"/>) to hand the Transform back. Editor only.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrder)]
    [DisallowMultipleComponent]
    public sealed class EditorSimHead : MonoBehaviour
    {
        /// <summary>After ImuHeadPose (-100) and before every script that reads the head (the lowest reader runs at 0).</summary>
        public const int ExecutionOrder = -90;

        /// <summary>0 = left, 1 = right, 2 = middle.</summary>
        public const int LookMouseButton = 1;

        public const float DefaultDegreesPerPixel = 0.15f;
        public const float DefaultMaxPitchDegrees = 85f;

        /// <summary>Degrees of turn per pixel of mouse movement.</summary>
        public float degreesPerPixel = DefaultDegreesPerPixel;

        /// <summary>Looking up or down is limited to this many degrees from the horizon.</summary>
        public float maxPitchDegrees = DefaultMaxPitchDegrees;

        /// <summary>Flip the vertical direction (mouse up = look down).</summary>
        public bool invertY;

        float _yaw;
        float _pitch;
        bool _dragging;
        Vector3 _lastMouse;
        bool _inputBroken;

        /// <summary>
        /// Attaches the rig to <paramref name="target"/> and returns it (the existing one if there already is one). Returns null
        /// and fills <paramref name="error"/> when the target is null or Unity refused the component (AddComponent gave null).
        /// </summary>
        public static EditorSimHead Attach(Transform target, out string error)
        {
            error = null;
            if (target == null) { error = "no target Transform"; return null; }
            EditorSimHead rig = target.GetComponent<EditorSimHead>();
            if (rig != null) return rig;
            try { rig = target.gameObject.AddComponent<EditorSimHead>(); }
            catch (Exception e) { error = "AddComponent threw " + e.GetType().Name + ": " + e.Message; return null; }
            if (rig == null) error = "Unity refused to add the component (AddComponent returned null; see the error above in the Console)";
            return rig;
        }

        /// <summary>Removes the rig from <paramref name="target"/>; true if there was one.</summary>
        public static bool Detach(Transform target)
        {
            if (target == null) return false;
            EditorSimHead rig = target.GetComponent<EditorSimHead>();
            if (rig == null) return false;
            if (Application.isPlaying) Destroy(rig);
            else DestroyImmediate(rig);
            return true;
        }

        /// <summary>True when <paramref name="target"/> carries the rig.</summary>
        public static bool IsAttached(Transform target)
        {
            return target != null && target.GetComponent<EditorSimHead>() != null;
        }

        void OnEnable()
        {
            // Start from wherever the Transform is looking now, so attaching never makes the view jump.
            Vector3 euler = transform.rotation.eulerAngles;
            _yaw = euler.y;
            _pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, euler.x), -maxPitchDegrees, maxPitchDegrees);
            _dragging = false;
        }

        void Update()
        {
            if (_inputBroken) return;

            bool down;
            Vector3 mouse;
            try
            {
                down = UnityEngine.Input.GetMouseButton(LookMouseButton);
                mouse = UnityEngine.Input.mousePosition;
            }
            catch (InvalidOperationException e)
            {
                _inputBroken = true;
                Debug.LogWarning("UGA Editor Sim Rig stopped: the legacy Input class is disabled in Player Settings (" + e.Message + ")");
                return;
            }

            if (down)
            {
                if (_dragging)
                {
                    SimHeadLook.ApplyLook(ref _yaw, ref _pitch, mouse.x - _lastMouse.x, mouse.y - _lastMouse.y,
                        degreesPerPixel, maxPitchDegrees, invertY);
                }
                _dragging = true;
                _lastMouse = mouse;
            }
            else
            {
                _dragging = false;
            }

            // Written every frame (not only while dragging) so the sim head stays put against ImuHeadPose's identity reset.
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }
    }
#endif
}
