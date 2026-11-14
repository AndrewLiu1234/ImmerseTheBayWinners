using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UntitledGuitarApp.Editor
{
    /// <summary>
    /// macOS player build. scripts/build-mac.sh calls BuildMac with the editor closed:
    ///   Unity -batchmode -quit -projectPath unity -buildTarget OSXUniversal -executeMethod UntitledGuitarApp.Editor.BuildScript.BuildMac
    /// Output: &lt;repo root&gt;/build/UntitledGuitarApp.app (build/ is gitignored). Builds the enabled scenes in Build Settings,
    /// or, when there are none, one temporary empty scene (created under Assets/__BuildTemp and deleted again).
    /// Failure: in batch mode the process exits with code 1 (EditorApplication.Exit); inside a running editor it throws, so a
    /// failed build can never close somebody's open editor.
    /// </summary>
    public static class BuildScript
    {
        public const string AppName = "UntitledGuitarApp.app";

        const string TempFolderName = "__BuildTemp";
        const string TempFolder = "Assets/" + TempFolderName;
        const string TempScenePath = TempFolder + "/EmptyBuildScene.unity";

        /// <summary>&lt;repo root&gt;/build/UntitledGuitarApp.app. Application.dataPath is &lt;repo&gt;/unity/Assets in the editor.</summary>
        public static string OutputPath
        {
            get
            {
                string projectRoot = Path.GetDirectoryName(Application.dataPath);   // <repo>/unity
                string repoRoot = Path.GetDirectoryName(projectRoot);               // <repo>
                return Path.Combine(repoRoot, "build", AppName);
            }
        }

        /// <summary>Entry point for -executeMethod (and for the editor menu / Unity MCP).</summary>
        public static void BuildMac()
        {
            string failure;
            try
            {
                failure = Build();
            }
            catch (Exception e)
            {
                failure = "exception: " + e;
            }

            if (failure == null)
                return;

            Debug.LogError("[BuildScript] BUILD FAILED: " + failure);
            if (Application.isBatchMode)
                EditorApplication.Exit(1);   // UnityEditor.EditorApplication.Exit(int): the non-zero code scripts/build-mac.sh sees
            else
                throw new InvalidOperationException("macOS build failed: " + failure);
        }

        /// <summary>Returns null on success, otherwise a description of what went wrong.</summary>
        static string Build()
        {
            // The architecture is an editor-local setting (Library/), so set it here as well as in ProjectSetup.Apply.
            ProjectSetup.EnsureMacArchitecture();

            string output = OutputPath;
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            DeletePreviousBuild(output);   // so that "the .app exists" can only mean "this build produced it"

            bool createdTempScene = false;
            try
            {
                string[] scenes = ScenesToBuild(out createdTempScene);
                Debug.Log("[BuildScript] BuildMac -> " + output + " | scenes: " + string.Join(", ", scenes) +
                          " | " + UnityEditor.OSXStandalone.UserBuildSettings.architecture);

                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = output,
                    target = BuildTarget.StandaloneOSX,
                    options = BuildOptions.None,
                };
                BuildReport report = BuildPipeline.BuildPlayer(options);   // UnityEditor.BuildPipeline.BuildPlayer(BuildPlayerOptions)
                BuildSummary summary = report.summary;

                if (summary.result != BuildResult.Succeeded)
                    return Describe(report);
                if (!Directory.Exists(output))
                    return "BuildPlayer reported " + summary.result + " but " + output + " does not exist";

                Debug.Log("[BuildScript] BUILD SUCCEEDED: " + output + " | " + summary.totalSize + " bytes | " +
                          summary.totalTime.TotalSeconds.ToString("0.0") + " s | warnings=" + summary.totalWarnings);
                return null;
            }
            finally
            {
                if (createdTempScene)
                    RemoveTempScene();
            }
        }

        static string[] ScenesToBuild(out bool createdTempScene)
        {
            createdTempScene = false;
            var paths = new List<string>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)   // UnityEditor.EditorBuildSettings.scenes
            {
                if (scene.enabled)
                    paths.Add(scene.path);
            }
            if (paths.Count > 0)
                return paths.ToArray();

            Debug.LogWarning("[BuildScript] No enabled scene in Build Settings: building one temporary empty scene instead.");
            if (!AssetDatabase.IsValidFolder(TempFolder))
                AssetDatabase.CreateFolder("Assets", TempFolderName);
            createdTempScene = true;   // from here on the caller deletes the temp folder, whatever fails next

            // A fresh batch-mode editor holds one empty, unsaved "Untitled" scene, and NewScene(Additive) throws next to an unsaved
            // scene ("Cannot create a new scene additively with an untitled scene unsaved"), so there the scene is replaced, which is
            // harmless. In a live editor stay additive, so nobody's open scenes are touched, and close the new scene after saving it.
            NewSceneMode mode = Application.isBatchMode ? NewSceneMode.Single : NewSceneMode.Additive;
            Scene temp = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
            bool saved = EditorSceneManager.SaveScene(temp, TempScenePath);
            if (SceneManager.sceneCount > 1)
                EditorSceneManager.CloseScene(temp, true);
            if (!saved)
                throw new InvalidOperationException("could not save the temporary empty scene to " + TempScenePath);
            return new[] { TempScenePath };
        }

        static void RemoveTempScene()
        {
            if (!AssetDatabase.DeleteAsset(TempFolder))   // deletes the folder, the scene and their .meta files
                Debug.LogWarning("[BuildScript] could not delete " + TempFolder + "; delete it (and " + TempFolder + ".meta) by hand");
        }

        static void DeletePreviousBuild(string output)
        {
            if (!Directory.Exists(output))
                return;
            // Only ever delete the one folder this script produces: <something>/build/UntitledGuitarApp.app
            string parent = Path.GetFileName(Path.GetDirectoryName(output));
            if (Path.GetFileName(output) != AppName || parent != "build")
                throw new InvalidOperationException("refusing to delete unexpected path " + output);
            Directory.Delete(output, true);
        }

        static string Describe(BuildReport report)
        {
            var sb = new StringBuilder();
            sb.Append("result=").Append(report.summary.result).Append(" errors=").Append(report.summary.totalErrors);
            foreach (BuildStep step in report.steps)
            {
                foreach (BuildStepMessage message in step.messages)
                {
                    if (message.type == LogType.Error || message.type == LogType.Exception)
                        sb.Append("\n  ").Append(message.content);
                }
            }
            return sb.ToString();
        }
    }
}
