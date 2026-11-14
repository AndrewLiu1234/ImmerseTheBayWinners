using UnityEngine;

namespace UntitledGuitarApp.Runtime
{
    /// <summary>
    /// The only object in Scenes/Main.unity. Everything else is created from code here, so nobody ever hand-edits scene
    /// YAML (unity/CLAUDE.md: "one Bootstrap object; everything else spawns from code/prefabs").
    /// Walking-skeleton version: the XR modules (flags, stereo rig, head pose, neck, ghost hands, HUD, spectator, audio
    /// capture, gaze input) are composed here as each one is merged. Editor/SceneBuilder.cs creates the scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Bootstrap : MonoBehaviour
    {
        /// <summary>The live instance (one per app). Null before Awake and after the object is destroyed.</summary>
        public static Bootstrap Instance { get; private set; }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);   // a second Bootstrap (for example from an additive scene) must never compose twice
                return;
            }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
