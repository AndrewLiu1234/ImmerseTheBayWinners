using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace UntitledGuitarApp.Editor
{
    /// <summary>
    /// The one place that changes PlayerSettings / ProjectSettings (project rule: never hand-edit ProjectSettings).
    /// Run it headless, with the editor closed:
    ///   Unity -batchmode -quit -projectPath unity -executeMethod UntitledGuitarApp.Editor.ProjectSetup.Apply -logFile build/unity-setup.log
    /// Idempotent: every value is compared before it is written, and a second run writes nothing.
    ///
    /// The APIs below were checked against Unity 2021.3.3f1 itself (offline docs, the editor's XML docs, the IL of
    /// UnityEditor.OSXStandalone.Extensions.dll) and compile against its reference assemblies.
    /// What unity/CLAUDE.md lists and that cannot be set from code: Built-in render pipeline (the project default,
    /// nothing to set), the package list (rule 6: a human adds packages), the scene Main.unity (made in the editor).
    /// </summary>
    public static class ProjectSetup
    {
        public const string CompanyName = "UntitledGuitarApp";
        public const string ProductName = "Untitled Guitar App";
        public const string BundleId = "com.untitledguitarapp.app";

        // unity/CLAUDE.md: "Mic: ... handle a denied permission". A macOS player that records without an
        // NSMicrophoneUsageDescription is refused the microphone, so the text has to exist in the built app's Info.plist.
        public const string MicrophoneUsageDescription =
            "Untitled Guitar App listens to your guitar through the microphone to tell you which strings you played.";

        /// <summary>Entry point for -executeMethod. Applies every setting, saves, and logs what it did.</summary>
        public static void Apply()
        {
            int changed = ApplySettings();
            AssetDatabase.SaveAssets();   // UnityEditor.AssetDatabase.SaveAssets: writes ProjectSettings/ProjectSettings.asset
            Debug.Log("[ProjectSetup] done, " + changed + " setting(s) changed. " + Describe());
        }

        /// <summary>Applies every setting and returns how many values it had to change (0 when all were already right).</summary>
        public static int ApplySettings()
        {
            int changed = 0;

            // UnityEditor.PlayerSettings.companyName (static string). Also names the player's log folder:
            // ~/Library/Logs/UntitledGuitarApp/Untitled Guitar App/Player.log (docs/MASTER_PLAN.md section 6, Logs).
            changed += Set("companyName", PlayerSettings.companyName, CompanyName,
                v => PlayerSettings.companyName = v);

            // UnityEditor.PlayerSettings.productName (static string).
            changed += Set("productName", PlayerSettings.productName, ProductName,
                v => PlayerSettings.productName = v);

            // UnityEditor.PlayerSettings.GetApplicationIdentifier / SetApplicationIdentifier(NamedBuildTarget, string),
            // overloads added in 2021.2 (the BuildTargetGroup overloads still exist). NamedBuildTarget.Standalone covers macOS.
            changed += Set("bundle id (Standalone)",
                PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Standalone), BundleId,
                v => PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, v));

            // UnityEditor.PlayerSettings.macOS.microphoneUsageDescription (static string). Read by the macOS player build
            // (UnityEditor.OSXStandalone.Extensions.dll copies it into MacPlayerBuildConfig.MicrophoneUsageDescription).
            changed += Set("macOS microphoneUsageDescription",
                PlayerSettings.macOS.microphoneUsageDescription, MicrophoneUsageDescription,
                v => PlayerSettings.macOS.microphoneUsageDescription = v);

            changed += EnsureMacArchitecture();
            changed += EnsureLogStackTraces();
            changed += EnsureRunInBackground();
            return changed;
        }

        /// <summary>
        /// UnityEditor.PlayerSettings.runInBackground (static bool, "Run In Background"): keep running without focus. The app
        /// draws to the glasses (display 2) and listens to the mic while the Mac screen or another window has focus, so it must
        /// never pause; MicCapture warns once when it is off. StereoRig also sets Application.runInBackground in players.
        /// </summary>
        public static int EnsureRunInBackground()
        {
            if (PlayerSettings.runInBackground)
                return 0;
            Debug.Log("[ProjectSetup] runInBackground: False -> True");
            PlayerSettings.runInBackground = true;
            return 1;
        }

        /// <summary>
        /// macOS architecture = Apple silicon. UnityEditor.OSXStandalone.UserBuildSettings.architecture (static property of type
        /// MacOSArchitecture { x64 = 0, ARM64 = 1, x64ARM64 = 2 }; it lives in the macOS platform module, which the editor
        /// references automatically). Its getter and setter call EditorUserBuildSettings.Get/SetPlatformSettings("OSXUniversal",
        /// "Architecture", ...). That value is kept in Library/, NOT in ProjectSettings, so it is per machine and never
        /// committed; an unset value means Intel + Apple silicon. BuildScript.BuildMac calls this again before every build.
        /// </summary>
        public static int EnsureMacArchitecture()
        {
            if (UnityEditor.OSXStandalone.UserBuildSettings.architecture == UnityEditor.OSXStandalone.MacOSArchitecture.ARM64)
                return 0;
            Debug.Log("[ProjectSetup] macOS architecture: " + UnityEditor.OSXStandalone.UserBuildSettings.architecture + " -> ARM64");
            UnityEditor.OSXStandalone.UserBuildSettings.architecture = UnityEditor.OSXStandalone.MacOSArchitecture.ARM64;
            return 1;
        }

        /// <summary>
        /// unity/CLAUDE.md: UGALog writes "[UGA] {json}" lines "with stack traces off for LogType.Log".
        /// UnityEditor.PlayerSettings.Get/SetStackTraceLogType(LogType, StackTraceLogType); the setter also calls
        /// Application.SetStackTraceLogType for the running editor, and the value is stored in ProjectSettings so builds keep it.
        /// </summary>
        public static int EnsureLogStackTraces()
        {
            if (PlayerSettings.GetStackTraceLogType(LogType.Log) == StackTraceLogType.None)
                return 0;
            Debug.Log("[ProjectSetup] stack trace for LogType.Log: " + PlayerSettings.GetStackTraceLogType(LogType.Log) + " -> None");
            PlayerSettings.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            return 1;
        }

        /// <summary>One line with the values this class manages, plus a few it only reads (to show they are what the plan expects).</summary>
        public static string Describe()
        {
            return "company='" + PlayerSettings.companyName + "' product='" + PlayerSettings.productName +
                   "' bundleId=" + PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Standalone) +
                   " macArchitecture=" + UnityEditor.OSXStandalone.UserBuildSettings.architecture +
                   " logStackTrace(Log)=" + PlayerSettings.GetStackTraceLogType(LogType.Log) +
                   " runInBackground=" + PlayerSettings.runInBackground +
                   " | read only: apiCompatibility=" + PlayerSettings.GetApiCompatibilityLevel(NamedBuildTarget.Standalone) +
                   " scriptingBackend=" + PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone) +
                   " activeBuildTarget=" + EditorUserBuildSettings.activeBuildTarget;
        }

        static int Set(string label, string current, string wanted, Action<string> write)
        {
            if (string.Equals(current, wanted, StringComparison.Ordinal))
                return 0;
            Debug.Log("[ProjectSetup] " + label + ": '" + current + "' -> '" + wanted + "'");
            write(wanted);
            return 1;
        }
    }
}
