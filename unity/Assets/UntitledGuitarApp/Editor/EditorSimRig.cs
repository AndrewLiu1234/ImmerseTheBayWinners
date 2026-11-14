#if UNITY_EDITOR
// EditorSimRig: test the app in Play Mode without the glasses. Hold the RIGHT mouse button in the Game view and drag to turn
// a Transform (the head) like a mouse-look camera.
//
// Self-contained on purpose: it uses only UnityEngine and UnityEditor, no other UntitledGuitarApp class, so it keeps working
// whatever the other workers change. Because it lives in the Editor assembly, Runtime code cannot call it directly; attach it
// from Editor code (EditorSimRig.Attach(head)) or from the menu below.
//
// Input: the legacy UnityEngine.Input class (mouse button + mouse position), which is what Unity 2021.3 uses unless the
// project switches to the Input System package (not installed, and packages are frozen). If that ever happens Input throws
// InvalidOperationException; the rig catches it once, logs a warning and stops instead of spamming the console.
//
// UNVERIFIED: nobody has run this in the Editor yet (offline compile only).
using System;
using UnityEditor;
using UnityEngine;

namespace UntitledGuitarApp.Editor
{
    /// <summary>
    /// Mouse-look for the Transform it is attached to. While attached it owns that Transform's rotation (it keeps writing
    /// it every frame, after other scripts, so the sim head wins over an IMU script or a headLocked reset). Remove it
    /// (menu, or <see cref="Detach"/>) to hand the Transform back. Only attached in Play Mode.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    public sealed class EditorSimRig : MonoBehaviour
    {
        /// <summary>Menu item: toggles the rig on the selected Transform (or the head, or the main camera).</summary>
        public const string MenuPath = "UGA/Editor Sim Rig";

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

        // ------------------------------------------------------------------ attach from code

        /// <summary>
        /// Attaches the rig to <paramref name="target"/> (the head Transform) and returns it; returns the existing rig if
        /// there already is one, or null for a null target. Call it from Editor code in Play Mode.
        /// </summary>
        public static EditorSimRig Attach(Transform target)
        {
            if (target == null) return null;
            EditorSimRig rig = target.GetComponent<EditorSimRig>();
            if (rig == null) rig = target.gameObject.AddComponent<EditorSimRig>();
            return rig;
        }

        /// <summary>Removes the rig from <paramref name="target"/>; true if there was one.</summary>
        public static bool Detach(Transform target)
        {
            if (target == null) return false;
            EditorSimRig rig = target.GetComponent<EditorSimRig>();
            if (rig == null) return false;
            if (Application.isPlaying) Destroy(rig);
            else DestroyImmediate(rig);
            return true;
        }

        // ------------------------------------------------------------------ the mouse-look

        void OnEnable()
        {
            // Start from wherever the Transform is looking now, so attaching never makes the view jump.
            Vector3 euler = transform.rotation.eulerAngles;
            _yaw = euler.y;
            _pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, euler.x), -maxPitchDegrees, maxPitchDegrees);
            _dragging = false;
        }

        void LateUpdate()
        {
            if (_inputBroken) return;

            bool down;
            Vector3 mouse;
            try
            {
                down = Input.GetMouseButton(LookMouseButton);
                mouse = Input.mousePosition;
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
                    ApplyLook(ref _yaw, ref _pitch, mouse.x - _lastMouse.x, mouse.y - _lastMouse.y,
                        degreesPerPixel, maxPitchDegrees, invertY);
                }
                _dragging = true;
                _lastMouse = mouse;
            }
            else
            {
                _dragging = false;
            }

            // Written every frame (not only while dragging) so the sim head stays put against scripts that reset the rotation.
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

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

        // ------------------------------------------------------------------ menu

        [MenuItem(MenuPath, false, 200)]
        static void ToggleFromMenu()
        {
            Transform target = ResolveTarget();
            if (target == null)
            {
                Debug.LogWarning("UGA Editor Sim Rig: select the head Transform in the Hierarchy first (no object named 'Head' and no camera found).");
                return;
            }

            if (Detach(target))
            {
                Debug.Log("UGA Editor Sim Rig: detached from '" + target.name + "'.");
            }
            else
            {
                Attach(target);
                Debug.Log("UGA Editor Sim Rig: attached to '" + target.name + "'. Hold the right mouse button in the Game view and drag to look around.");
            }
        }

        [MenuItem(MenuPath, true)]
        static bool ToggleFromMenuValidate()
        {
            if (!Application.isPlaying)
            {
                Menu.SetChecked(MenuPath, false);
                return false;
            }
            Transform target = ResolveTarget();
            Menu.SetChecked(MenuPath, target != null && target.GetComponent<EditorSimRig>() != null);
            return true;
        }

        // The selected Transform wins; otherwise an object called "Head"; otherwise the main (or any) camera.
        static Transform ResolveTarget()
        {
            Transform selected = Selection.activeTransform;
            if (selected != null) return selected;

            GameObject head = GameObject.Find("Head");
            if (head != null) return head.transform;

            Camera main = Camera.main;
            if (main != null) return main.transform;

            Camera[] all = Camera.allCameras;
            return all.Length > 0 ? all[0].transform : null;
        }
    }
}
#endif
