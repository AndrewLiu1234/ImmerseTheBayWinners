// GhostPoseLibrary: loads every *.pose.json under <StreamingAssets>/poses and hands the poses out by hand and name.
// Plain C# (no MonoBehaviour): create one from code, call Reload() once on the main thread, then TryGet / TryBlend.
// The app runs only in the macOS editor and the macOS standalone player, where StreamingAssets is a plain folder,
// so System.IO is enough. Nothing here throws into the frame loop: problems are logged through UGALog and kept in
// Errors / Warnings.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    public sealed class GhostPoseLibrary
    {
        public const string FolderName = "poses";
        /// <summary>Two poses of one hand come from the same rig when their bind joints agree this closely.</summary>
        public const float SameRigToleranceMm = 0.05f;

        static readonly List<GhostPose> s_noPoses = new List<GhostPose>();
        static readonly List<string> s_noText = new List<string>();

        readonly string _folderOverride;
        Dictionary<string, Dictionary<string, GhostPose>> _byHand = new Dictionary<string, Dictionary<string, GhostPose>>(StringComparer.Ordinal);
        List<GhostPose> _poses = s_noPoses;
        List<string> _errors = s_noText;
        List<string> _warnings = s_noText;
        bool _loaded;

        /// <summary>Reads from StreamingAssets/poses.</summary>
        public GhostPoseLibrary() : this(null) { }

        /// <summary>Reads from the given folder (tests, tools). Null means StreamingAssets/poses.</summary>
        public GhostPoseLibrary(string folder) { _folderOverride = folder; }

        /// <summary>StreamingAssets/poses. Uses Application.streamingAssetsPath, so call it on the main thread.</summary>
        public static string DefaultFolder { get { return Path.Combine(Application.streamingAssetsPath, FolderName); } }

        /// <summary>The folder this library reads.</summary>
        public string Folder { get { return _folderOverride ?? DefaultFolder; } }

        /// <summary>Number of poses loaded by the last Reload.</summary>
        public int Count { get { return _poses.Count; } }

        /// <summary>The poses loaded by the last Reload, in file-name order.</summary>
        public IReadOnlyList<GhostPose> Poses { get { return _poses; } }

        /// <summary>One line per file the last Reload refused: "file: what is wrong".</summary>
        public IReadOnlyList<string> Errors { get { return _errors; } }

        /// <summary>One line per file that loaded but looks wrong (no bind block, or a different rig than its hand's other poses).</summary>
        public IReadOnlyList<string> Warnings { get { return _warnings; } }

        /// <summary>
        /// Reads the folder again and replaces the whole set at once. Returns the number of poses loaded. Never throws:
        /// a file that fails validation is skipped and logged (UGALog "ghost_pose_rejected"), the others still load.
        /// </summary>
        public int Reload()
        {
            var poses = new List<GhostPose>();
            var errors = new List<string>();
            var warnings = new List<string>();
            string folder = null;
            try
            {
                folder = Folder;
                ReadFolder(folder, poses, errors, warnings);
            }
            catch (Exception e)
            {
                errors.Add("reading the pose folder failed (" + e.GetType().Name + ": " + e.Message + ")");
            }

            var byHand = new Dictionary<string, Dictionary<string, GhostPose>>(StringComparer.Ordinal);
            for (int i = 0; i < poses.Count; i++)
            {
                Dictionary<string, GhostPose> byName;
                if (!byHand.TryGetValue(poses[i].Hand, out byName))
                {
                    byName = new Dictionary<string, GhostPose>(StringComparer.Ordinal);
                    byHand[poses[i].Hand] = byName;
                }
                byName[poses[i].Name] = poses[i];
            }
            _byHand = byHand;
            _poses = poses;
            _errors = errors;
            _warnings = warnings;
            _loaded = true;

            try
            {
                for (int i = 0; i < errors.Count; i++) UGALog.Error("ghost_pose_rejected", errors[i]);
                for (int i = 0; i < warnings.Count; i++) UGALog.Warn("ghost_pose_warning", warnings[i]);
                UGALog.Event("ghost_poses_loaded", "count", poses.Count, "rejected", errors.Count, "folder", folder ?? "");
            }
            catch (Exception)
            {
                // logging must never break loading
            }
            return poses.Count;
        }

        /// <summary>
        /// Looks a pose up, for example TryGet("fretting", "G", out pose). Names are case-sensitive. Allocation-free.
        /// The first call loads the folder if Reload() has not been called yet.
        /// </summary>
        public bool TryGet(string hand, string name, out GhostPose pose)
        {
            pose = null;
            if (hand == null || name == null) return false;
            if (!_loaded) Reload();
            Dictionary<string, GhostPose> byName;
            return _byHand.TryGetValue(hand, out byName) && byName.TryGetValue(name, out pose);
        }

        /// <summary>
        /// Blend helper by name: GhostPose.Blend between two poses of one hand, eased, into the caller's 16-entry buffer.
        /// Allocation-free. False (nothing written) when either pose is not loaded or the buffer is too small.
        /// </summary>
        public bool TryBlend(string hand, string fromName, string toName, float t, Quaternion[] rotations, out Vector3 rootPositionMm)
        {
            rootPositionMm = Vector3.zero;
            GhostPose from, to;
            if (!TryGet(hand, fromName, out from) || !TryGet(hand, toName, out to)) return false;
            return GhostPose.Blend(from, to, t, rotations, out rootPositionMm);
        }

        /// <summary>
        /// Reads every *.pose.json under 'folder' (sub-folders included) and appends to the three lists: the poses that
        /// passed validation, one line per refused file, and one line per loaded-but-suspicious file. No logging and no
        /// exceptions, so tests and tools can call it directly.
        /// </summary>
        public static void ReadFolder(string folder, List<GhostPose> poses, List<string> errors, List<string> warnings)
        {
            if (poses == null || errors == null || warnings == null) return;
            string[] files;
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                {
                    warnings.Add("pose folder not found: " + folder);
                    return;
                }
                files = Directory.GetFiles(folder, "*" + GhostPose.FileSuffix, SearchOption.AllDirectories);
            }
            catch (Exception e)
            {
                errors.Add("cannot list " + folder + " (" + e.GetType().Name + ": " + e.Message + ")");
                return;
            }
            Array.Sort(files, StringComparer.Ordinal);

            var fileOf = new Dictionary<string, string>(StringComparer.Ordinal);       // "hand/name" -> file that holds it
            var rigOf = new Dictionary<string, GhostPose>(StringComparer.Ordinal);     // hand -> first pose with bind data
            for (int i = 0; i < files.Length; i++)
            {
                string file = Path.GetFileName(files[i]);
                if (!file.EndsWith(GhostPose.FileSuffix, StringComparison.Ordinal) || file.StartsWith(".", StringComparison.Ordinal)) continue;
                string text;
                try
                {
                    text = File.ReadAllText(files[i]);
                }
                catch (Exception e)
                {
                    errors.Add(file + ": cannot be read (" + e.GetType().Name + ": " + e.Message + ")");
                    continue;
                }
                GhostPose pose;
                string error;
                if (!GhostPose.TryParse(text, out pose, out error))
                {
                    errors.Add(file + ": " + error);
                    continue;
                }
                string expected = GhostPose.FileNameFor(pose.Hand, pose.Name);
                if (!string.Equals(file, expected, StringComparison.Ordinal))
                {
                    errors.Add(file + ": holds the pose " + pose.Hand + "/" + pose.Name + ", so the file must be named " + expected);
                    continue;
                }
                string key = pose.Hand + "/" + pose.Name;
                string earlier;
                if (fileOf.TryGetValue(key, out earlier))
                {
                    errors.Add(file + ": the pose " + key + " was already loaded from " + earlier);
                    continue;
                }
                fileOf[key] = files[i];

                if (!pose.HasBind)
                {
                    warnings.Add(file + ": no bind block, so its rotations cannot be checked or turned into joint positions: re-export with art/blender/export_poses.py 1.1.0 or newer");
                }
                else
                {
                    GhostPose rig;
                    if (!rigOf.TryGetValue(pose.Hand, out rig))
                    {
                        rigOf[pose.Hand] = pose;
                    }
                    else
                    {
                        float apart = pose.MaxBindDistanceMm(rig);
                        if (apart > SameRigToleranceMm)
                            warnings.Add(file + ": its bind joints differ from " + GhostPose.FileNameFor(rig.Hand, rig.Name) + " by " + apart.ToString("0.###")
                                + " mm, so the two were exported from different rigs: re-export every " + pose.Hand + " pose from one .blend");
                    }
                }
                poses.Add(pose);
            }
        }
    }
}
