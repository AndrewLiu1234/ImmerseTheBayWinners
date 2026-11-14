// GhostPose: one authored hand pose (Pose JSON v1) and the maths that go with it: parsing and validation, the blend
// between two poses, and the forward kinematics that turn 16 rotations into 21 joint positions.
// Format specification: unity/Assets/StreamingAssets/poses/README.md. Writer: art/blender/export_poses.py.
// Plain C# (no MonoBehaviour). Poses are immutable after parsing, so one instance can be shared by every consumer.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    public sealed class GhostPose
    {
        // ---- constants that must stay in step with art/blender/export_poses.py (BONES, KNOWN_POSES, SCHEMA, FK_LIMIT_MM) ----
        public const int SchemaVersion = 1;
        public const int BoneCount = 16;
        public const int DigitCount = 5;                         // 0 thumb, 1 index, 2 middle, 3 ring, 4 pinky
        public const int JointCount = BoneCount + DigitCount;    // 21 points: the 16 bone heads, then the 5 fingertips
        public const string Units = "mm";
        public const string Space = "neck-local-unity";
        public const string HandFretting = "fretting";
        public const string HandStrumming = "strumming";
        public const string FileSuffix = ".pose.json";
        public const float UnitTolerance = 1e-3f;                // a rotation's length may differ from 1 by this much
        public const float FkToleranceMm = 0.5f;                 // rotations must rebuild the file's fingertips this closely

        public const int Wrist = 0;
        public const int ThumbProximal = 1, ThumbIntermediate = 2, ThumbDistal = 3;
        public const int IndexProximal = 4, IndexIntermediate = 5, IndexDistal = 6;
        public const int MiddleProximal = 7, MiddleIntermediate = 8, MiddleDistal = 9;
        public const int RingProximal = 10, RingIntermediate = 11, RingDistal = 12;
        public const int PinkyProximal = 13, PinkyIntermediate = 14, PinkyDistal = 15;

        static readonly string[] s_boneNames =
        {
            "wrist",
            "thumb_proximal", "thumb_intermediate", "thumb_distal",
            "index_proximal", "index_intermediate", "index_distal",
            "middle_proximal", "middle_intermediate", "middle_distal",
            "ring_proximal", "ring_intermediate", "ring_distal",
            "pinky_proximal", "pinky_intermediate", "pinky_distal",
        };
        static readonly int[] s_boneParents = { -1, 0, 1, 2, 0, 4, 5, 0, 7, 8, 0, 10, 11, 0, 13, 14 };
        static readonly string[] s_hands = { HandFretting, HandStrumming };
        static readonly string[] s_frettingPoses = { "rest", "G", "C", "D", "Em", "Am", "E", "A", "Dm" };
        static readonly string[] s_strummingPoses = { "rest", "down-start", "down-end", "up-start", "up-end", "pick-ready" };

        /// <summary>The 16 canonical bone names, parents before children. Index = bone index everywhere in this class.</summary>
        public static readonly ReadOnlyCollection<string> BoneNames = Array.AsReadOnly(s_boneNames);
        /// <summary>For each bone the index of its parent bone, or -1 for the wrist.</summary>
        public static readonly ReadOnlyCollection<int> BoneParents = Array.AsReadOnly(s_boneParents);
        public static readonly ReadOnlyCollection<string> Hands = Array.AsReadOnly(s_hands);
        /// <summary>Pose names the fretting hand may have. Names only: frets and fingers live in Core.</summary>
        public static readonly ReadOnlyCollection<string> FrettingPoseNames = Array.AsReadOnly(s_frettingPoses);
        public static readonly ReadOnlyCollection<string> StrummingPoseNames = Array.AsReadOnly(s_strummingPoses);

        /// <summary>First bone of a digit (0 thumb .. 4 pinky).</summary>
        public static int ProximalBone(int digit) { return 1 + 3 * digit; }
        /// <summary>Last bone of a digit (0 thumb .. 4 pinky). Its far end is the fingertip.</summary>
        public static int DistalBone(int digit) { return 3 + 3 * digit; }

        /// <summary>Index of a canonical bone name, or -1.</summary>
        public static int BoneIndex(string boneName)
        {
            if (boneName == null) return -1;
            for (int i = 0; i < BoneCount; i++)
                if (string.Equals(s_boneNames[i], boneName, StringComparison.Ordinal)) return i;
            return -1;
        }

        public static bool IsKnownHand(string hand)
        {
            return string.Equals(hand, HandFretting, StringComparison.Ordinal) || string.Equals(hand, HandStrumming, StringComparison.Ordinal);
        }

        public static bool IsKnownPose(string hand, string poseName)
        {
            if (poseName == null) return false;
            string[] names = string.Equals(hand, HandFretting, StringComparison.Ordinal) ? s_frettingPoses
                : string.Equals(hand, HandStrumming, StringComparison.Ordinal) ? s_strummingPoses : null;
            if (names == null) return false;
            for (int i = 0; i < names.Length; i++)
                if (string.Equals(names[i], poseName, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>The file name the exporter gives a pose: fretting-G.pose.json.</summary>
        public static string FileNameFor(string hand, string poseName) { return hand + "-" + poseName + FileSuffix; }

        // ---- one pose --------------------------------------------------------------------------------------------------
        readonly Quaternion[] _rotations = new Quaternion[BoneCount];
        readonly Vector3[] _fingertipsMm = new Vector3[DigitCount];
        readonly Vector3[] _bindMm;   // JointCount entries (16 bone heads, 5 fingertips) in the hand's model space, or null

        GhostPose(string hand, string name, Vector3 rootPositionMm, Vector3[] bindMm)
        {
            Hand = hand;
            Name = name;
            RootPositionMm = rootPositionMm;
            _bindMm = bindMm;
        }

        /// <summary>"fretting" or "strumming".</summary>
        public string Hand { get; private set; }
        /// <summary>The pose name: a chord (G, Em ...), rest, or a strumming key pose (down-start ...).</summary>
        public string Name { get; private set; }
        /// <summary>Position of the wrist joint in the neck frame, Unity axes, millimetres.</summary>
        public Vector3 RootPositionMm { get; private set; }
        /// <summary>True when the file carries the bind positions, so ComputeJointPositionsMm works.</summary>
        public bool HasBind { get { return _bindMm != null; } }
        public string SourceBlend { get; private set; }
        public string SourceDate { get; private set; }
        public string Exporter { get; private set; }

        /// <summary>The rotation stored for a bone (see the README for what it is relative to). Identity if out of range.</summary>
        public Quaternion GetRotation(int bone)
        {
            return bone >= 0 && bone < BoneCount ? _rotations[bone] : Quaternion.identity;
        }

        /// <summary>Copies the 16 rotations into a caller buffer. False (nothing written) if the buffer is too small.</summary>
        public bool CopyRotations(Quaternion[] rotations)
        {
            if (rotations == null || rotations.Length < BoneCount) return false;
            for (int i = 0; i < BoneCount; i++) rotations[i] = _rotations[i];
            return true;
        }

        /// <summary>Fingertip of a digit (0 thumb .. 4 pinky) as authored: neck frame, Unity axes, millimetres.</summary>
        public Vector3 GetFingertipMm(int digit)
        {
            return digit >= 0 && digit < DigitCount ? _fingertipsMm[digit] : Vector3.zero;
        }

        /// <summary>Bind position of a joint (0..15 bone heads, 16..20 fingertips) in the hand's model space, millimetres.</summary>
        public Vector3 GetBindPositionMm(int joint)
        {
            return _bindMm != null && joint >= 0 && joint < JointCount ? _bindMm[joint] : Vector3.zero;
        }

        /// <summary>Largest distance between this pose's bind joints and another's (0 = same rig). Infinity without bind data.</summary>
        public float MaxBindDistanceMm(GhostPose other)
        {
            if (other == null || _bindMm == null || other._bindMm == null) return float.PositiveInfinity;
            float worst = 0f;
            for (int i = 0; i < JointCount; i++)
            {
                float d = (_bindMm[i] - other._bindMm[i]).magnitude;
                if (d > worst) worst = d;
            }
            return worst;
        }

        // ---- blending (allocation-free; safe to call every frame) ------------------------------------------------------
        /// <summary>Smoothstep on t clamped to 0..1: slow start, slow end.</summary>
        public static float Ease(float t)
        {
            if (!(t > 0f)) return 0f;   // also catches NaN
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Blends two poses of the same hand into a caller buffer: per-bone shortest-path slerp of the 16 rotations and a
        /// straight line for the wrist position, both at Ease(t). t = 0 gives 'from', t = 1 gives 'to'.
        /// Returns false, and writes nothing, when a pose is null, the hands differ or the buffer has fewer than 16 entries.
        /// </summary>
        public static bool Blend(GhostPose from, GhostPose to, float t, Quaternion[] rotations, out Vector3 rootPositionMm)
        {
            return BlendLinear(from, to, Ease(t), rotations, out rootPositionMm);
        }

        /// <summary>Blend without easing: 'u' is used as it is (clamped to 0..1). Use it with your own timing curve.</summary>
        public static bool BlendLinear(GhostPose from, GhostPose to, float u, Quaternion[] rotations, out Vector3 rootPositionMm)
        {
            rootPositionMm = Vector3.zero;
            if (from == null || to == null || rotations == null || rotations.Length < BoneCount) return false;
            if (!string.Equals(from.Hand, to.Hand, StringComparison.Ordinal)) return false;
            if (!(u > 0f)) u = 0f;
            else if (u > 1f) u = 1f;
            for (int i = 0; i < BoneCount; i++) rotations[i] = Slerp(from._rotations[i], to._rotations[i], u);
            Vector3 a = from.RootPositionMm, b = to.RootPositionMm;
            rootPositionMm = new Vector3(a.x + (b.x - a.x) * u, a.y + (b.y - a.y) * u, a.z + (b.z - a.z) * u);
            return true;
        }

        /// <summary>Shortest-path spherical interpolation between two unit quaternions; the result has unit length.</summary>
        public static Quaternion Slerp(Quaternion a, Quaternion b, float u)
        {
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            if (dot < 0f)
            {
                b = new Quaternion(-b.x, -b.y, -b.z, -b.w);   // q and -q are the same rotation: take the short way round
                dot = -dot;
            }
            float wa, wb;
            if (dot > 0.9995f)
            {
                wa = 1f - u;   // nearly the same rotation: a straight line is exact enough and avoids dividing by ~0
                wb = u;
            }
            else
            {
                float theta = Mathf.Acos(dot);
                float invSin = 1f / Mathf.Sin(theta);
                wa = Mathf.Sin((1f - u) * theta) * invSin;
                wb = Mathf.Sin(u * theta) * invSin;
            }
            return Normalized(wa * a.x + wb * b.x, wa * a.y + wb * b.y, wa * a.z + wb * b.z, wa * a.w + wb * b.w, a);
        }

        // ---- forward kinematics (allocation-free) ----------------------------------------------------------------------
        /// <summary>
        /// Turns the 16 stored (parent-relative) rotations into each bone's total turn from the bind pose:
        /// total[wrist] = rotation[wrist], total[b] = total[parent(b)] * rotation[b]. A bone of the imported rig then has
        /// the neck-frame rotation total[b] * bindRotationInModelSpace[b]. The two buffers may be the same array.
        /// </summary>
        public static bool AccumulateRotations(Quaternion[] rotations, Quaternion[] totals)
        {
            if (rotations == null || totals == null || rotations.Length < BoneCount || totals.Length < BoneCount) return false;
            for (int i = 0; i < BoneCount; i++)
            {
                int parent = s_boneParents[i];
                totals[i] = parent < 0 ? rotations[i] : totals[parent] * rotations[i];
            }
            return true;
        }

        /// <summary>
        /// Rebuilds the hand from rotations alone: positionsMm[0..15] = the bone heads, [16..20] = the fingertips
        /// (thumb .. pinky), in the neck frame, Unity axes, millimetres. 'rotations' are 16 parent-relative rotations (this
        /// pose's own, or a Blend result) and 'totals' receives AccumulateRotations of them. Needs HasBind.
        /// </summary>
        public bool ComputeJointPositionsMm(Quaternion[] rotations, Vector3 rootPositionMm, Quaternion[] totals, Vector3[] positionsMm)
        {
            if (_bindMm == null || positionsMm == null || positionsMm.Length < JointCount) return false;
            if (!AccumulateRotations(rotations, totals)) return false;
            positionsMm[0] = rootPositionMm;
            for (int i = 1; i < BoneCount; i++)
            {
                int parent = s_boneParents[i];
                positionsMm[i] = positionsMm[parent] + totals[parent] * (_bindMm[i] - _bindMm[parent]);
            }
            for (int digit = 0; digit < DigitCount; digit++)
            {
                int bone = DistalBone(digit);
                positionsMm[BoneCount + digit] = positionsMm[bone] + totals[bone] * (_bindMm[BoneCount + digit] - _bindMm[bone]);
            }
            return true;
        }

        // ---- parsing and validation ------------------------------------------------------------------------------------
        /// <summary>
        /// Parses and validates one Pose JSON v1 text. Never throws: on failure 'pose' is null and 'error' lists every
        /// problem found, separated by "; ". On success 'error' is null.
        /// </summary>
        public static bool TryParse(string json, out GhostPose pose, out string error)
        {
            pose = null;
            error = null;
            if (string.IsNullOrEmpty(json) || json.Trim().Length == 0)
            {
                error = "the file is empty";
                return false;
            }
            PoseFile file;
            try
            {
                file = JsonUtility.FromJson<PoseFile>(json);
            }
            catch (Exception e)
            {
                error = "not valid JSON (" + e.Message + ")";
                return false;
            }
            if (file == null)
            {
                error = "the file is empty";
                return false;
            }
            try
            {
                var problems = new List<string>();
                GhostPose built = Build(file, problems);
                if (problems.Count > 0)
                {
                    error = string.Join("; ", problems.ToArray());
                    return false;
                }
                pose = built;
                return true;
            }
            catch (Exception e)
            {
                error = "unexpected error while reading the pose (" + e.GetType().Name + ": " + e.Message + ")";
                return false;
            }
        }

        static GhostPose Build(PoseFile file, List<string> problems)
        {
            if (file.schema != SchemaVersion)
                problems.Add("schema is " + file.schema + ", this build reads schema " + SchemaVersion);
            if (!IsKnownHand(file.hand))
                problems.Add("hand is '" + file.hand + "', expected '" + HandFretting + "' or '" + HandStrumming + "'");
            else if (!IsKnownPose(file.hand, file.name))
                problems.Add("name '" + file.name + "' is not a known " + file.hand + " pose (GhostPose." + (file.hand == HandFretting ? "FrettingPoseNames" : "StrummingPoseNames") + ")");
            if (!string.Equals(file.units, Units, StringComparison.Ordinal))
                problems.Add("units is '" + file.units + "', expected '" + Units + "'");
            if (!string.Equals(file.space, Space, StringComparison.Ordinal))
                problems.Add("space is '" + file.space + "', expected '" + Space + "'");
            Vector3 root = ToVector(file.rootPositionMm);
            if (!IsFinite(root))
                problems.Add("rootPositionMm is not a finite position");

            Vector3[] bind = ReadBind(file.bind, problems);
            var pose = new GhostPose(file.hand, file.name, root, bind);
            if (file.source != null)
            {
                pose.SourceBlend = file.source.blend;
                pose.SourceDate = file.source.date;
                pose.Exporter = file.source.exporter;
            }

            // bones: each of the 16 canonical names exactly once, any order, a unit quaternion each
            var seen = new bool[BoneCount];
            int boneEntries = file.bones == null ? 0 : file.bones.Length;
            if (boneEntries == 0) problems.Add("bones is missing or empty");
            for (int k = 0; k < boneEntries; k++)
            {
                BoneEntry entry = file.bones[k];
                int bone = entry == null ? -1 : BoneIndex(entry.name);
                if (bone < 0)
                {
                    problems.Add("bones[" + k + "] has the unknown name '" + (entry == null ? null : entry.name) + "'");
                    continue;
                }
                if (seen[bone])
                {
                    problems.Add("bone " + entry.name + " is listed twice");
                    continue;
                }
                seen[bone] = true;
                Xyzw q = entry.rotation;
                float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                if (float.IsNaN(length) || float.IsInfinity(length) || Mathf.Abs(length - 1f) > UnitTolerance)
                {
                    problems.Add("bone " + entry.name + ": rotation is not a unit quaternion (length " + length.ToString("0.######", CultureInfo.InvariantCulture) + ")");
                    continue;
                }
                pose._rotations[bone] = Normalized(q.x, q.y, q.z, q.w, Quaternion.identity);
            }
            if (boneEntries > 0)
            {
                string missing = null;
                for (int i = 0; i < BoneCount; i++)
                    if (!seen[i]) missing = missing == null ? s_boneNames[i] : missing + ", " + s_boneNames[i];
                if (missing != null) problems.Add("missing bones: " + missing);
            }

            // fingertips: the five distal bones exactly once
            var seenTip = new bool[DigitCount];
            int tipEntries = file.fingertips == null ? 0 : file.fingertips.Length;
            if (tipEntries == 0) problems.Add("fingertips is missing or empty");
            for (int k = 0; k < tipEntries; k++)
            {
                PointEntry entry = file.fingertips[k];
                int digit = entry == null ? -1 : DigitOfDistalBone(entry.bone);
                if (digit < 0)
                    problems.Add("fingertips[" + k + "] names '" + (entry == null ? null : entry.bone) + "', which is not a distal bone");
                else if (seenTip[digit])
                    problems.Add("fingertip " + entry.bone + " is listed twice");
                else if (!IsFinite(ToVector(entry.positionMm)))
                    problems.Add("fingertip " + entry.bone + " is not a finite position");
                else
                {
                    seenTip[digit] = true;
                    pose._fingertipsMm[digit] = ToVector(entry.positionMm);
                }
            }
            if (tipEntries > 0)
                for (int digit = 0; digit < DigitCount; digit++)
                    if (!seenTip[digit] && !ProblemMentions(problems, s_boneNames[DistalBone(digit)]))
                        problems.Add("missing fingertip: " + s_boneNames[DistalBone(digit)]);

            // the rotations must rebuild the file's own fingertips: catches a file written with another convention
            if (problems.Count == 0 && bind != null)
            {
                var totals = new Quaternion[BoneCount];
                var positions = new Vector3[JointCount];
                pose.ComputeJointPositionsMm(pose._rotations, pose.RootPositionMm, totals, positions);
                for (int digit = 0; digit < DigitCount; digit++)
                {
                    float off = (positions[BoneCount + digit] - pose._fingertipsMm[digit]).magnitude;
                    if (!(off <= FkToleranceMm))
                        problems.Add("the rotations put " + s_boneNames[DistalBone(digit)] + "'s tip " + off.ToString("0.###", CultureInfo.InvariantCulture) + " mm from the stored fingertip (limit "
                            + FkToleranceMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm): re-export with art/blender/export_poses.py");
                }
            }
            return pose;
        }

        /// <summary>The optional "bind" block: 16 joints and 5 fingertips, or null when the file has none.</summary>
        static Vector3[] ReadBind(BindBlock block, List<string> problems)
        {
            int joints = block == null || block.joints == null ? 0 : block.joints.Length;
            int tips = block == null || block.fingertips == null ? 0 : block.fingertips.Length;
            if (joints == 0 && tips == 0) return null;
            var bind = new Vector3[JointCount];
            var seen = new bool[JointCount];
            for (int k = 0; k < joints; k++)
            {
                PointEntry entry = block.joints[k];
                int bone = entry == null ? -1 : BoneIndex(entry.bone);
                if (bone < 0 || seen[bone] || !IsFinite(ToVector(entry.positionMm)))
                {
                    problems.Add("bind.joints[" + k + "] ('" + (entry == null ? null : entry.bone) + "') is unknown, repeated or not finite");
                    continue;
                }
                seen[bone] = true;
                bind[bone] = ToVector(entry.positionMm);
            }
            for (int k = 0; k < tips; k++)
            {
                PointEntry entry = block.fingertips[k];
                int digit = entry == null ? -1 : DigitOfDistalBone(entry.bone);
                if (digit < 0 || seen[BoneCount + digit] || !IsFinite(ToVector(entry.positionMm)))
                {
                    problems.Add("bind.fingertips[" + k + "] ('" + (entry == null ? null : entry.bone) + "') is unknown, repeated or not finite");
                    continue;
                }
                seen[BoneCount + digit] = true;
                bind[BoneCount + digit] = ToVector(entry.positionMm);
            }
            int found = 0;
            for (int i = 0; i < JointCount; i++) if (seen[i]) found++;
            if (found != JointCount)
                problems.Add("bind must list all 16 joints and 5 fingertips (found " + found + " of " + JointCount + ")");
            return bind;
        }

        static int DigitOfDistalBone(string boneName)
        {
            int bone = BoneIndex(boneName);
            return bone >= ThumbDistal && (bone - ThumbDistal) % 3 == 0 ? (bone - ThumbDistal) / 3 : -1;
        }

        static bool ProblemMentions(List<string> problems, string word)
        {
            for (int i = 0; i < problems.Count; i++)
                if (problems[i].IndexOf(word, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static Vector3 ToVector(Xyz v) { return new Vector3(v.x, v.y, v.z); }

        static bool IsFinite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        }

        static Quaternion Normalized(float x, float y, float z, float w, Quaternion fallback)
        {
            float length = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (!(length > 1e-12f)) return fallback;
            float inv = 1f / length;
            return new Quaternion(x * inv, y * inv, z * inv, w * inv);
        }

        // ---- the JSON shape, for UnityEngine.JsonUtility (field names are the JSON keys; unknown keys are ignored) -------
        // No field has an initialiser: Unity's two pages on JsonUtility.FromJson disagree on whether one would survive a
        // missing key, so a missing key is always read as null / 0 here and then refused or treated as absent above.
        // Positions and rotations use our own structs so the JSON keys x, y, z, w are plain serializable fields.
#pragma warning disable 0649
        [Serializable]
        sealed class PoseFile
        {
            public int schema;
            public string hand;
            public string name;
            public string units;
            public string space;
            public SourceBlock source;
            public Xyz rootPositionMm;
            public BoneEntry[] bones;
            public PointEntry[] fingertips;
            public BindBlock bind;
        }

        [Serializable]
        sealed class SourceBlock
        {
            public string blend;
            public string date;
            public string exporter;
            public string blender;
        }

        [Serializable]
        sealed class BoneEntry
        {
            public string name;
            public Xyzw rotation;
        }

        [Serializable]
        sealed class PointEntry
        {
            public string bone;
            public Xyz positionMm;
        }

        [Serializable]
        sealed class BindBlock
        {
            public PointEntry[] joints;
            public PointEntry[] fingertips;
        }

        [Serializable]
        struct Xyz
        {
            public float x;
            public float y;
            public float z;
        }

        [Serializable]
        struct Xyzw
        {
            public float x;
            public float y;
            public float z;
            public float w;
        }
#pragma warning restore 0649
    }
}
