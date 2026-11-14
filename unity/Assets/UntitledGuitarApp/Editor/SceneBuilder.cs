using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UntitledGuitarApp.Editor
{
    /// <summary>
    /// Makes Assets/UntitledGuitarApp/Scenes/Main.unity: exactly one root object, "Bootstrap", carrying
    /// UntitledGuitarApp.Runtime.Bootstrap, and makes it the only enabled scene in Build Settings. Scene files are never
    /// hand-edited (the protect-files hook blocks it); this script is how Main.unity gets made and repaired.
    ///   Batch mode (editor closed), from the repo root:
    ///   Unity -batchmode -quit -projectPath unity -executeMethod UntitledGuitarApp.Editor.SceneBuilder.BuildMainScene -logFile build/scene-builder.log
    ///   In the editor: menu UGA > Rebuild Main Scene.
    /// Idempotent in batch mode: a Main.unity that is already correct is left untouched, so the file does not churn in git.
    /// Failure: exits with code 1 in batch mode (EditorApplication.Exit), throws in a live editor (same rule as BuildScript).
    /// APIs: EditorSceneManager.NewScene / OpenScene / SaveScene / SaveCurrentModifiedScenesIfUserWantsTo,
    /// EditorBuildSettings.scenes, EditorBuildSettingsScene, AssetDatabase.SaveAssets (UnityEditor, 2021.3 Scripting API).
    /// </summary>
    public static class SceneBuilder
    {
        public const string MainScenePath = "Assets/UntitledGuitarApp/Scenes/Main.unity";
        public const string BootstrapObjectName = "Bootstrap";

        [MenuItem("UGA/Rebuild Main Scene")]
        public static void BuildMainScene()
        {
            try
            {
                bool written = EnsureMainScene();
                bool listed = EnsureBuildSettings();
                Debug.Log("[SceneBuilder] " + MainScenePath + (written ? " written" : " already correct") +
                          "; Build Settings " + (listed ? "updated" : "already correct"));
            }
            catch (Exception e)
            {
                Debug.LogError("[SceneBuilder] FAILED: " + e);
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
                else
                    throw;
            }
        }

        /// <summary>True when the scene at <paramref name="scene"/> holds exactly one root, the Bootstrap object, and that
        /// object has only a Transform and a Bootstrap component (no missing scripts).</summary>
        public static bool IsCorrect(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            if (roots.Length != 1 || roots[0].name != BootstrapObjectName) return false;
            Component[] components = roots[0].GetComponents<Component>();
            if (components.Length != 2) return false;
            foreach (Component c in components)
                if (c == null) return false;   // a missing script shows up as a null component
            return roots[0].GetComponent<UntitledGuitarApp.Runtime.Bootstrap>() != null;
        }

        static bool EnsureMainScene()
        {
            if (Application.isBatchMode && File.Exists(MainScenePath))
            {
                // A fresh batch-mode editor holds only an empty, unsaved scene, so opening Single closes nothing of value.
                Scene existing = EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
                if (IsCorrect(existing)) return false;
            }
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                throw new OperationCanceledException("kept the open scenes unsaved; Main.unity was not rebuilt");

            string folder = Path.GetDirectoryName(MainScenePath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject(BootstrapObjectName);
            root.AddComponent<UntitledGuitarApp.Runtime.Bootstrap>();
            if (!EditorSceneManager.SaveScene(scene, MainScenePath))
                throw new InvalidOperationException("could not save " + MainScenePath);
            AssetDatabase.SaveAssets();
            return true;
        }

        static bool EnsureBuildSettings()
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            if (scenes.Length == 1 && scenes[0].enabled && scenes[0].path == MainScenePath) return false;
            // Main.unity is the app's only scene (everything else is composed in code), so it is the whole list.
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(MainScenePath, true) };
            AssetDatabase.SaveAssets();
            return true;
        }
    }
}
