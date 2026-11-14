#if UNITY_EDITOR
// EditorSimRig: the menu "UGA/Editor Sim Rig" (Play Mode): toggles the mouse-look head, Runtime/Head/EditorSimHead, on the
// selected Transform, else on the object called "Head" (StereoRig.Head), else on the main camera. Hold the RIGHT mouse button in
// the Game view and drag to look around. WAV replay (audioSource = "wav") is the other half of the sim rig and needs no menu.
//
// The component itself is in the Runtime assembly (inside #if UNITY_EDITOR) because Unity does not attach MonoBehaviours that
// live in the Editor assembly to GameObjects (Manual/SpecialFolders.html); see EditorSimHead.cs. This file only holds the menu.
using UnityEditor;
using UnityEngine;
using UntitledGuitarApp.Runtime.Head;

namespace UntitledGuitarApp.Editor
{
    public static class EditorSimRig
    {
        /// <summary>Menu item: toggles the rig on the selected Transform (or the head, or the main camera).</summary>
        public const string MenuPath = "UGA/Editor Sim Rig";

        /// <summary>Attaches the mouse-look head to <paramref name="target"/>; null (with a logged warning) if Unity refused it.</summary>
        public static EditorSimHead Attach(Transform target)
        {
            string error;
            EditorSimHead rig = EditorSimHead.Attach(target, out error);
            if (rig == null)
                Debug.LogWarning("UGA Editor Sim Rig: NOT attached to '" + (target != null ? target.name : "(nothing)") + "': " + error);
            return rig;
        }

        /// <summary>Removes the mouse-look head from <paramref name="target"/>; true if there was one.</summary>
        public static bool Detach(Transform target)
        {
            return EditorSimHead.Detach(target);
        }

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
                return;
            }
            EditorSimHead rig = Attach(target);
            if (rig != null)
                Debug.Log("UGA Editor Sim Rig: attached to '" + target.name + "' (EditorSimHead, execution order " + EditorSimHead.ExecutionOrder
                    + ": after ImuHeadPose, before every head reader). Hold the right mouse button in the Game view and drag to look around.");
        }

        [MenuItem(MenuPath, true)]
        static bool ToggleFromMenuValidate()
        {
            if (!Application.isPlaying)
            {
                Menu.SetChecked(MenuPath, false);
                return false;
            }
            Menu.SetChecked(MenuPath, EditorSimHead.IsAttached(ResolveTarget()));
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
