// NeckAnchor: the one owner of WHERE the neck (and everything parented to its Socket / StrumZone: the ghost hands) lives.
// Bootstrap creates it once; nothing else should call FloatingNeck.PlaceFloating / SetWorldPose after that.
//
//     NeckAnchor anchor = NeckAnchor.Create(transform, neck, Rig.Head, HeadPose);   // places the neck at once
//     anchor.Recenter();                                                           // the R key: IMU recenter + re-place
//     anchor.SetAlignment(pose);                                                   // NeckAligner does this on success
//
// POLICY (NeckAnchorPolicy.Decide, unit-tested over every combination):
//   anchorMode == "headLocked" (flag)  OR  the head is not tracked  ->  FollowHead: the floating neck hangs in front of the
//       head and turns with it. With no fresh IMU data ImuHeadPose snaps the head to identity, so world-anchored content
//       could end up far outside the 27-degree-tall view (the P0 this fixes); head-locked content never can.
//   tracked, no alignment  ->  World: the floating neck is placed once in front of the head as it is at that moment, then
//       stays put in the room while the head turns.
//   tracked, aligned       ->  Aligned: the neck sits on the real neck (the pose NeckAligner found).
// The policy is re-evaluated every LateUpdate (after ImuHeadPose's Update has applied this frame's rotation); the neck is
// re-placed only when the outcome changes, on Recenter, or on SetAlignment. Returning to tracking re-places the floating neck
// in front of the head, or restores the aligned pose: the alignment is remembered through an IMU dropout.
//
// "Tracked" = ImuHeadPose.State == Tracking, or SimulatedHead. In the editor the mouse-look rig (Editor/EditorSimRig, a
// component on the head) turns the head while ImuHeadPose has no packets; it is detected by its type name twice a second
// (Component.GetComponent(string), docs/apis/unity/neck-align.md), so world anchoring and alignment keep working in the
// editor (L4). A null ImuHeadPose counts as tracked (previews, tests).
//
// Recenter(): ImuHeadPose.Recenter() changes the yaw that means "forward", which turns the whole virtual world about the head.
// The real guitar has not moved relative to the head, so a remembered alignment is turned by the same yaw (measured as the
// head's rotation just before and just after the call). A recenter while the head is not tracked cannot be measured (the
// head is held at identity), so it drops the alignment (logged).
// Main thread only; no allocation per frame.
using System;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Head;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Neck
{
    /// <summary>Where the neck is put. See <see cref="NeckAnchorPolicy.Decide"/>.</summary>
    public enum NeckAnchorPlacement
    {
        /// <summary>Not placed yet.</summary>
        None = -1,
        /// <summary>Floating neck attached to the head (head-locked).</summary>
        FollowHead = 0,
        /// <summary>Floating neck placed once in front of the head, then fixed in the room.</summary>
        World = 1,
        /// <summary>On the real neck (NeckAligner's pose).</summary>
        Aligned = 2,
    }

    /// <summary>The anchoring rule as a pure function (unit-tested).</summary>
    public static class NeckAnchorPolicy
    {
        /// <summary>The flag value that forces head-locked content (compared as ImuHeadPoseCore does: ordinal, ignoring case).</summary>
        public const string HeadLockedMode = "headLocked";

        public static bool IsHeadLockedMode(string anchorMode)
        {
            return string.Equals(anchorMode, HeadLockedMode, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Tracked = fresh IMU data (or a simulated head); headLockedMode = the anchorMode flag.</summary>
        public static NeckAnchorPlacement Decide(bool headLockedMode, bool headTracked, bool hasAlignment)
        {
            if (headLockedMode || !headTracked) return NeckAnchorPlacement.FollowHead;
            return hasAlignment ? NeckAnchorPlacement.Aligned : NeckAnchorPlacement.World;
        }

        /// <summary>Whether ImuHeadPose's state counts as tracked (a simulated head always does; no ImuHeadPose does too).</summary>
        public static bool IsTracked(bool hasHeadPose, HeadTrackingState state, bool simulatedHead)
        {
            return simulatedHead || !hasHeadPose || state == HeadTrackingState.Tracking;
        }
    }

    [DisallowMultipleComponent]
    [DefaultExecutionOrder(900)]   // LateUpdate after ordinary scripts; ImuHeadPose (-100) has applied this frame's rotation in Update
    public sealed class NeckAnchor : MonoBehaviour
    {
        /// <summary>Type name of the editor's mouse-look rig (Editor/EditorSimRig.cs), looked up by name from Runtime code.</summary>
        public const string EditorSimRigTypeName = "EditorSimRig";

        const float SimCheckIntervalSeconds = 0.5f;

        FloatingNeck _neck;
        Transform _head;
        ImuHeadPose _headPose;
        bool _simulatedHeadSet;
        bool _simulatedHead;
        bool _simDetected;
        float _nextSimCheck;
        bool _hasAlignment;
        AlignedPose _alignment;
        NeckAnchorPlacement _placement = NeckAnchorPlacement.None;

        /// <summary>Creates the anchor on a new child of <paramref name="parent"/> and places the neck at once.</summary>
        public static NeckAnchor Create(Transform parent, FloatingNeck neck, Transform head, ImuHeadPose headPose)
        {
            var go = new GameObject("NeckAnchor");
            if (parent != null) go.transform.SetParent(parent, false);
            NeckAnchor anchor = go.AddComponent<NeckAnchor>();
            anchor._neck = neck;
            anchor._head = head;
            anchor._headPose = headPose;
            if (neck != null) neck.Viewer = head;
            anchor.Refresh();
            return anchor;
        }

        public FloatingNeck Neck
        {
            get { return _neck; }
        }

        public Transform Head
        {
            get { return _head; }
        }

        public ImuHeadPose HeadPose
        {
            get { return _headPose; }
        }

        /// <summary>What the neck is doing now.</summary>
        public NeckAnchorPlacement Placement
        {
            get { return _placement; }
        }

        /// <summary>
        /// Treat the head as tracked although ImuHeadPose is not (an editor simulation that turns the head). Setting it turns the
        /// automatic EditorSimRig detection off.
        /// </summary>
        public bool SimulatedHead
        {
            get { return _simulatedHeadSet ? _simulatedHead : _simDetected; }
            set
            {
                _simulatedHeadSet = true;
                _simulatedHead = value;
            }
        }

        /// <summary>True when the head's rotation is real (IMU tracking, or a simulated head): world anchoring and alignment work.</summary>
        public bool HeadTracked
        {
            get { return NeckAnchorPolicy.IsTracked(_headPose != null, _headPose != null ? _headPose.State : HeadTrackingState.HeadLocked, SimulatedHead); }
        }

        /// <summary>True when the anchorMode flag forces head-locked content.</summary>
        public bool HeadLockedMode
        {
            get
            {
                FlagSet flags = FlagStore.Current;
                return flags != null && NeckAnchorPolicy.IsHeadLockedMode(flags.anchorMode);
            }
        }

        public bool HasAlignment
        {
            get { return _hasAlignment; }
        }

        /// <summary>The remembered aligned pose (valid when <see cref="HasAlignment"/>).</summary>
        public AlignedPose Alignment
        {
            get { return _alignment; }
        }

        /// <summary>Raised (main thread) after the neck changed placement.</summary>
        public event Action<NeckAnchorPlacement> PlacementChanged;

        /// <summary>Remembers an aligned pose and shows it (when tracked and the flag allows).</summary>
        public void SetAlignment(AlignedPose pose)
        {
            if (!pose.IsFinite)
            {
                UGALog.Warn("neck_anchor", "ignored a non-finite aligned pose");
                return;
            }
            _alignment = pose;
            _hasAlignment = true;
            Apply(Decide(), true, "aligned");
        }

        /// <summary>Forgets the alignment (back to the floating neck).</summary>
        public void ClearAlignment()
        {
            if (!_hasAlignment) return;
            _hasAlignment = false;
            Apply(Decide(), true, "alignment_cleared");
        }

        /// <summary>
        /// The R key: ImuHeadPose.Recenter(), then the neck is re-placed: the floating neck in front of the head again, an
        /// alignment turned by the recenter's yaw so it stays on the real neck.
        /// </summary>
        public void Recenter()
        {
            bool trackedBefore = HeadTracked && !HeadLockedMode;
            if (_headPose != null && _head != null)
            {
                Quaternion before = _head.rotation;
                _headPose.Recenter();
                Quaternion after = _head.rotation;
                if (_hasAlignment)
                {
                    if (trackedBefore)
                    {
                        Quaternion delta = AlignMath.Normalized(after * AlignMath.Conjugate(before));
                        _alignment = AlignMath.RotateAbout(_alignment, _head.position, delta);
                    }
                    else
                    {
                        _hasAlignment = false;
                        UGALog.Warn("neck_anchor", "recentered while the head was not tracked: the alignment is dropped, align again");
                    }
                }
            }
            Apply(Decide(), true, "recenter");
        }

        /// <summary>Re-evaluates the policy now (also done every LateUpdate). Re-places only when the outcome changed.</summary>
        public void Refresh()
        {
            Apply(Decide(), false, "refresh");
        }

        NeckAnchorPlacement Decide()
        {
            return NeckAnchorPolicy.Decide(HeadLockedMode, HeadTracked, _hasAlignment);
        }

        void Apply(NeckAnchorPlacement placement, bool force, string reason)
        {
            if (_neck == null) return;
            if (!force && placement == _placement) return;
            NeckAnchorPlacement previous = _placement;
            switch (placement)
            {
                case NeckAnchorPlacement.Aligned:
                    _neck.SetWorldPose(_alignment.nut, _alignment.rotation);
                    if (_head != null) _neck.Viewer = _head;
                    break;
                case NeckAnchorPlacement.World:
                    _neck.PlaceFloating(_head, false);
                    break;
                default:
                    _neck.PlaceFloating(_head, true);
                    break;
            }
            _placement = placement;
            if (previous != placement)
            {
                UGALog.Event("neck_anchor", "placement", placement.ToString(), "from", previous.ToString(), "reason", reason);
                Action<NeckAnchorPlacement> handler = PlacementChanged;
                if (handler != null)
                {
                    try { handler(placement); }
                    catch (Exception e) { UGALog.Error("neck_anchor", "a PlacementChanged listener threw: " + e.Message); }
                }
            }
        }

        void LateUpdate()
        {
            if (!_simulatedHeadSet && Application.isEditor && Time.unscaledTime >= _nextSimCheck)
            {
                _nextSimCheck = Time.unscaledTime + SimCheckIntervalSeconds;
                _simDetected = _head != null && _head.GetComponent(EditorSimRigTypeName) != null;
            }
            Refresh();
        }
    }
}
