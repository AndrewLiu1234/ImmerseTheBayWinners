// NeckAligner: "4 glances put the virtual neck on your real neck" (slice SL8, docs/MASTER_PLAN.md section 20). Built from code:
//
//     NeckAligner aligner = NeckAligner.Create(transform, Rig.Head, Neck, HeadPose, Anchor);   // Anchor: NeckAnchor (may be null)
//     aligner.Reticle = Reticle;          // optional: the reticle's arc shows "hold still" progress
//     aligner.Begin();                    // full alignment: recenter, then nut low E, nut high E, fret 5 low E, fret 12 low E
//     aligner.BeginReanchor();            // one glance at the nut on low E (needs an earlier alignment; else runs Begin)
//     aligner.Cancel();  aligner.IsRunning;  aligner.LastResult;  aligner.Finished += outcome => ...;
//
// THE FLOW. Begin() recenters the IMU first (through NeckAnchor.Recenter, so whatever is already placed in the world turns with
// it), hides the neck and its ghost hands (the player must see the REAL guitar), and turns gaze selection off (so no other
// dwell target fires while the player stares at the guitar). For each target a head-locked prompt above the reticle says
// where to look ("Look at the nut / on the thick low E string / Hold still - or press Space"); a capture happens when the
// head's forward has stayed within 0.5 degrees of its mean for 1.2 s (GazeSteadiness; MASTER_PLAN's 1.2 s dwell, Person 2's
// 0.5 degree advice), or at once on Space. The reticle's arc fills meanwhile. After the 4th capture Core's NeckSolver fits the
// neck (NeckAlignFlow). Success: the anchor gets the pose (FloatingNeck.SetWorldPose, NeckMode.Aligned), the prompt shows the
// residual honestly, `neck_aligned` is logged. Failure: the prompt says why (Core's text or our gate), the neck goes back to
// what it was (the floating neck, or the previous alignment), and Space within 8 s tries again. The player always has a neck.
// It cannot work without real head rotation: with no IMU tracking (and no editor mouse-look) it refuses and says so.
//
// HUD: three HudText lines + a step title, head-locked at 1.2 m, centred 2.6 to 10.2 degrees above the reticle (the view is
// 27 degrees tall), cap heights 1.6 / 1.2 degrees (the minimum is 1.2). Sizes are the TRUE sizes asked for: HudText's
// current ~18% shortfall is being fixed on its own branch, so nothing here compensates for it. Every prompt string is a
// constant; the per-frame path (Step) builds no strings and allocates nothing (the final capture allocates the outcome).
using System;
using UnityEngine;
using UntitledGuitarApp.Core.Geometry;
using UntitledGuitarApp.Core.Theory;
using UntitledGuitarApp.Runtime.Gaze;
using UntitledGuitarApp.Runtime.Head;
using UntitledGuitarApp.Runtime.Hud;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Neck
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(800)]   // after ImuHeadPose (-100) has turned the head this frame, before NeckAnchor (900)
    public sealed class NeckAligner : MonoBehaviour
    {
        public const float HudDistanceM = 1.2f;
        public const float TitleCapDeg = 1.2f;
        public const float PromptCapDeg = 1.6f;
        public const float HintCapDeg = 1.2f;
        public const float TitleElevationDeg = 10.2f;
        public const float Line1ElevationDeg = 7.6f;
        public const float Line2ElevationDeg = 5.0f;
        public const float HintElevationDeg = 2.6f;

        /// <summary>A capture needs the gaze within this many degrees of its mean (Person 2: aim for dwell jitter 0.5 or less).</summary>
        public const float SteadyThresholdDeg = 0.5f;

        /// <summary>... for this long (MASTER_PLAN section 20: hold each for 1.2 s).</summary>
        public const float SteadyWindowSeconds = 1.2f;

        /// <summary>How long the result stays up (and Space retries after a failure).</summary>
        public const float ResultSeconds = 8f;

        static readonly Color TitleColor = new Color(0.55f, 0.90f, 1.00f, 1f);
        static readonly Color PromptColor = new Color(1.00f, 1.00f, 1.00f, 1f);
        static readonly Color HintColor = new Color(0.75f, 0.88f, 1.00f, 1f);
        static readonly Color GoodColor = new Color(0.50f, 1.00f, 0.62f, 1f);
        static readonly Color BadColor = new Color(1.00f, 0.78f, 0.35f, 1f);

        static readonly string[] StepTitles = { "Neck alignment 1 / 4", "Neck alignment 2 / 4", "Neck alignment 3 / 4", "Neck alignment 4 / 4" };
        static readonly string[] StepLine1 = { "Look at the nut", "Now the nut", "Now fret 5", "Now fret 12" };
        static readonly string[] StepLine2 =
        {
            "on the thick low E string", "on the thin high E string", "on the thick low E string", "on the thick low E string",
        };
        const string ReanchorTitle = "Re-anchor";
        const string ReanchorLine1 = "Look at the nut";
        const string HoldHint = "Hold still - or press Space";

        Transform _head;
        FloatingNeck _neck;
        ImuHeadPose _headPose;
        NeckAnchor _anchor;
        NeckAlignFlow _flow;
        FretboardGeometry _geometry;
        HudText _title;
        HudText _line1;
        HudText _line2;
        HudText _hint;
        bool _hudVisible;
        float _hideAt = -1f;
        bool _retryOffered;
        bool _confirmRequested;
        bool _neckHidden;
        bool _neckWasActive;
        bool _gazeSuspended;
        Transform _gazeHeadBefore;
        AlignOutcome _last;
        int _shownStep = -1;
        float _now;

        /// <summary>Creates the aligner under <paramref name="parent"/>; its HUD hangs under <paramref name="head"/>.</summary>
        public static NeckAligner Create(Transform parent, Transform head, FloatingNeck neck, ImuHeadPose headPose, NeckAnchor anchor)
        {
            var go = new GameObject("NeckAligner");
            if (parent != null) go.transform.SetParent(parent, false);
            NeckAligner aligner = go.AddComponent<NeckAligner>();
            aligner._head = head;
            aligner._neck = neck;
            aligner._headPose = headPose;
            aligner._anchor = anchor;
            aligner.Init();
            return aligner;
        }

        /// <summary>As the other Create, without a NeckAnchor (the aligner then places the neck itself).</summary>
        public static NeckAligner Create(Transform parent, Transform head, FloatingNeck neck, ImuHeadPose headPose)
        {
            return Create(parent, head, neck, headPose, null);
        }

        void Init()
        {
            if (_flow != null) return;
            _geometry = CoreNeckLayout.DefaultGeometry;
            _flow = new NeckAlignFlow(_geometry, NeckSolverConfig.Default, SteadyThresholdDeg, SteadyWindowSeconds);
            Transform hudParent = _head != null ? _head : transform;
            _title = MakeLine(hudParent, "AlignTitle", TitleCapDeg, TitleElevationDeg, TitleColor);
            _line1 = MakeLine(hudParent, "AlignLine1", PromptCapDeg, Line1ElevationDeg, PromptColor);
            _line2 = MakeLine(hudParent, "AlignLine2", PromptCapDeg, Line2ElevationDeg, PromptColor);
            _hint = MakeLine(hudParent, "AlignHint", HintCapDeg, HintElevationDeg, HintColor);
            SetHudVisible(false);
        }

        static HudText MakeLine(Transform parent, string name, float capDeg, float elevationDeg, Color color)
        {
            HudText text = HudText.Create(parent, name);
            text.Anchor = TextAnchor.MiddleCenter;
            text.Alignment = TextAlignment.Center;
            text.Color = color;
            text.SetAngularHeight(capDeg, HudDistanceM);
            text.transform.localPosition = new Vector3(0f, HudDistanceM * Mathf.Tan(elevationDeg * Mathf.Deg2Rad), HudDistanceM);
            text.transform.localRotation = Quaternion.identity;
            return text;
        }

        // ------------------------------------------------------------------ public surface

        /// <summary>Optional: the reticle whose arc shows the hold-still progress.</summary>
        public Reticle Reticle { get; set; }

        /// <summary>The key that captures at once / retries after a failure (KeyCode.None = off).</summary>
        public KeyCode ConfirmKey = KeyCode.Space;

        /// <summary>Hide the neck (and its ghost hands) while the player looks at the real guitar. Default on.</summary>
        public bool HideNeckWhileAligning = true;

        /// <summary>Turn gaze selection (GazeDwell.Head) off while aligning, so no other dwell target fires. Default on.</summary>
        public bool SuspendGazeWhileAligning = true;

        /// <summary>Refuse to run while the anchorMode flag says headLocked. Default on; previews turn it off.</summary>
        public bool RespectAnchorModeFlag = true;

        public bool IsRunning
        {
            get { return _flow != null && _flow.IsRunning; }
        }

        /// <summary>The last finished (or refused / cancelled) attempt; null before the first.</summary>
        public AlignOutcome LastResult
        {
            get { return _last; }
        }

        /// <summary>The step machine (read-only use: Phase, Step, CurrentTarget, Progress).</summary>
        public NeckAlignFlow Flow
        {
            get { Init(); return _flow; }
        }

        public NeckAnchor Anchor
        {
            get { return _anchor; }
            set { _anchor = value; }
        }

        /// <summary>The guitar the solver fits (default: CoreNeckLayout.DefaultGeometry, the same as the drawn neck). Set before Begin.</summary>
        public FretboardGeometry Geometry
        {
            get { return _geometry; }
            set
            {
                if (value == null || value == _geometry || IsRunning) return;
                _geometry = value;
                _flow = new NeckAlignFlow(_geometry, NeckSolverConfig.Default, SteadyThresholdDeg, SteadyWindowSeconds);
            }
        }

        /// <summary>Raised (main thread) when an attempt ends: success, failure, refusal or cancel.</summary>
        public event Action<AlignOutcome> Finished;

        /// <summary>Starts the full 4-glance alignment. Returns false (and says why on the HUD) when it cannot run.</summary>
        public bool Begin()
        {
            Init();
            if (IsRunning) Cancel();
            string refusal = Refusal();
            if (refusal != null) return Refuse(refusal, false);

            if (_anchor != null) _anchor.Recenter();
            else if (_headPose != null) _headPose.Recenter();

            _flow.Begin(LeftHanded(), StringHeightMm());
            Started(false);
            return true;
        }

        /// <summary>One glance at the nut on low E re-anchors an earlier alignment; without one it starts the full alignment.</summary>
        public bool BeginReanchor()
        {
            Init();
            if (IsRunning) Cancel();
            AlignedPose current;
            if (!TryCurrentAlignment(out current)) return Begin();
            string refusal = Refusal();
            if (refusal != null) return Refuse(refusal, true);
            _flow.BeginReanchor(current, LeftHanded(), StringHeightMm());
            Started(true);
            return true;
        }

        /// <summary>Stops a running alignment; the neck stays as it was before Begin.</summary>
        public void Cancel()
        {
            if (_flow == null || !_flow.Cancel("cancelled")) return;
            UGALog.Event("neck_align_cancel", "step", _flow.Step, "reanchor", _flow.IsReanchor);
            Ended(_flow.Outcome);
        }

        /// <summary>The same as pressing the confirm key this frame (capture now, or retry after a failure).</summary>
        public void Confirm()
        {
            _confirmRequested = true;
        }

        /// <summary>
        /// One frame of the flow, reading the head transform. Update calls it; previews and tests call it directly with their own
        /// clock (Edit Mode has no Update). No allocation except when the last capture finishes the attempt.
        /// </summary>
        public void Step(float nowSec, bool confirm)
        {
            _now = nowSec;
            if (_flow == null) return;
            if (!_flow.IsRunning)
            {
                if (_hudVisible && _hideAt >= 0f && nowSec >= _hideAt) SetHudVisible(false);
                if (confirm && _retryOffered && _hudVisible) Retry();
                return;
            }
            if (!HeadTrackedNow())
            {
                if (_flow.Cancel("head tracking stopped: alignment needs the glasses' IMU")) Ended(_flow.Outcome);
                return;
            }
            if (_head == null) return;

            bool changed = _flow.Tick(nowSec, _head.position, _head.forward, confirm);
            if (Reticle != null) Reticle.Progress = _flow.IsRunning ? _flow.Progress : 0f;
            if (!changed) return;
            if (_flow.IsRunning) ShowStep();
            else Ended(_flow.Outcome);
        }

        void Update()
        {
            bool confirm = _confirmRequested;
            _confirmRequested = false;
            if (ConfirmKey != KeyCode.None && Input.GetKeyDown(ConfirmKey)) confirm = true;
            Step(Time.unscaledTime, confirm);
        }

        // ------------------------------------------------------------------ placement (shared with the preview)

        /// <summary>Puts an aligned pose on the neck: through the anchor when there is one (it owns placement), else directly.</summary>
        public static void PlaceAligned(FloatingNeck neck, NeckAnchor anchor, Transform head, AlignedPose pose)
        {
            if (anchor != null)
            {
                anchor.SetAlignment(pose);
                return;
            }
            if (neck == null) return;
            neck.SetWorldPose(pose.nut, pose.rotation);
            if (head != null) neck.Viewer = head;
        }

        // ------------------------------------------------------------------ internals

        string Refusal()
        {
            if (_head == null) return "no head transform: alignment cannot run";
            if (RespectAnchorModeFlag && (_anchor != null ? _anchor.HeadLockedMode : HeadLockedFlag())) return "anchorMode is headLocked (flag): alignment is off";
            if (!HeadTrackedNow()) return "no head tracking: start the IMU bridge (alignment needs the glasses' IMU)";
            return null;
        }

        bool Refuse(string why, bool reanchor)
        {
            var outcome = new AlignOutcome { Reanchor = reanchor, Failure = why };
            UGALog.Warn("neck_align_refused", why);
            _last = outcome;
            ShowResult(outcome);
            RaiseFinished(outcome);
            return false;
        }

        void Started(bool reanchor)
        {
            _retryOffered = false;
            _hideAt = -1f;
            HideNeck();
            SuspendGaze();
            _shownStep = -1;
            ShowStep();
            UGALog.Event("neck_align_begin", "reanchor", reanchor, "leftHanded", LeftHanded(), "steps", _flow.StepCount);
        }

        void Retry()
        {
            if (_last != null && _last.Reanchor) BeginReanchor();
            else Begin();
        }

        void Ended(AlignOutcome outcome)
        {
            if (Reticle != null) Reticle.Progress = 0f;
            RestoreGaze();
            if (outcome != null && outcome.Success) PlaceAligned(_neck, _anchor, _head, outcome.Pose);
            ShowNeck();
            _last = outcome;
            if (outcome != null)
            {
                if (outcome.Success && !outcome.Reanchor)
                    UGALog.Event("neck_aligned", "rmsMm", Round1(outcome.ResidualRmsMm), "maxMm", Round1(outcome.ResidualMaxMm),
                                 "nutDistanceMm", Mathf.Round(outcome.NutDistanceM * AlignMath.MmPerMetre));
                else if (outcome.Success)
                    UGALog.Event("neck_reanchored", "nutShiftMm", Mathf.Round(outcome.NutShiftMm), "nutDistanceMm",
                                 Mathf.Round(outcome.NutDistanceM * AlignMath.MmPerMetre), "jitterDeg", Round2(outcome.MaxJitterDeg));
                else
                    UGALog.Event("neck_align_failed", "reason", outcome.Failure, "rmsMm", Round1(outcome.ResidualRmsMm),
                                 "reanchor", outcome.Reanchor);
                if (outcome.Success)
                    UGALog.Event("neck_align_detail", "viewerElevationDeg", Round1(outcome.ViewerElevationDeg),
                                 "jitterMaxDeg", Round2(outcome.MaxJitterDeg), "spaceCaptures", outcome.ConfirmedCount);
            }
            ShowResult(outcome);
            RaiseFinished(outcome);
        }

        void RaiseFinished(AlignOutcome outcome)
        {
            Action<AlignOutcome> handler = Finished;
            if (handler == null) return;
            try { handler(outcome); }
            catch (Exception e) { UGALog.Error("neck_align", "a Finished listener threw: " + e.Message); }
        }

        void ShowStep()
        {
            int step = _flow.Step;
            if (step == _shownStep) return;
            _shownStep = step;
            if (_flow.IsReanchor)
            {
                SetLines(ReanchorTitle, ReanchorLine1, StepLine2[0], HoldHint, TitleColor, PromptColor);
            }
            else
            {
                int i = Mathf.Clamp(step, 0, StepTitles.Length - 1);
                SetLines(StepTitles[i], StepLine1[i], StepLine2[i], HoldHint, TitleColor, PromptColor);
            }
            _hideAt = -1f;
            SetHudVisible(true);
        }

        void ShowResult(AlignOutcome outcome)
        {
            if (outcome == null) return;
            if (outcome.Success && !outcome.Reanchor)
            {
                SetLines("Neck aligned", "fit error " + outcome.ResidualRmsMm.ToString("0") + " mm (max " + outcome.ResidualMaxMm.ToString("0") + " mm)",
                         "nut " + (outcome.NutDistanceM * 100f).ToString("0") + " cm away: check it sits on the real nut",
                         "Look around: it should stay on your guitar", GoodColor, PromptColor);
                _retryOffered = false;
            }
            else if (outcome.Success)
            {
                SetLines("Re-anchored", "the nut moved " + outcome.NutShiftMm.ToString("0") + " mm", string.Empty,
                         "Look around: it should stay on your guitar", GoodColor, PromptColor);
                _retryOffered = false;
            }
            else
            {
                string first, second;
                SplitInTwo(outcome.Failure ?? "unknown error", 34, out first, out second);
                bool kept = _anchor != null && _anchor.HasAlignment;
                SetLines(kept ? "Alignment failed: kept the old one" : "Alignment failed: floating neck", first, second,
                         ConfirmKey != KeyCode.None ? "Press " + ConfirmKey + " to try again" : string.Empty, BadColor, BadColor);
                _retryOffered = true;
            }
            _hideAt = _now + ResultSeconds;
            SetHudVisible(true);
        }

        void SetLines(string title, string line1, string line2, string hint, Color titleColor, Color lineColor)
        {
            _title.SetText(title);
            _title.Color = titleColor;
            _line1.SetText(line1);
            _line1.Color = lineColor;
            _line2.SetText(line2);
            _line2.Color = lineColor;
            _hint.SetText(hint);
        }

        void SetHudVisible(bool visible)
        {
            _hudVisible = visible;
            if (_title == null) return;
            _title.Visible = visible;
            _line1.Visible = visible;
            _line2.Visible = visible;
            _hint.Visible = visible;
        }

        /// <summary>Splits a sentence at the space nearest its middle when it is longer than <paramref name="maxLine"/>.</summary>
        public static void SplitInTwo(string text, int maxLine, out string first, out string second)
        {
            text = text ?? string.Empty;
            if (text.Length <= maxLine)
            {
                first = text;
                second = string.Empty;
                return;
            }
            int middle = text.Length / 2;
            int best = -1;
            for (int d = 0; d < text.Length; d++)
            {
                int left = middle - d, right = middle + d;
                if (left > 0 && text[left] == ' ') { best = left; break; }
                if (right < text.Length - 1 && text[right] == ' ') { best = right; break; }
            }
            if (best < 0)
            {
                first = text;
                second = string.Empty;
                return;
            }
            first = text.Substring(0, best);
            second = text.Substring(best + 1);
        }

        void HideNeck()
        {
            if (!HideNeckWhileAligning || _neck == null || _neckHidden) return;
            _neckWasActive = _neck.gameObject.activeSelf;
            _neck.gameObject.SetActive(false);
            _neckHidden = true;
        }

        void ShowNeck()
        {
            if (!_neckHidden || _neck == null) return;
            _neck.gameObject.SetActive(_neckWasActive);
            _neckHidden = false;
        }

        void SuspendGaze()
        {
            if (!SuspendGazeWhileAligning || _gazeSuspended) return;
            GazeDwell gaze = GazeDwell.Instance;
            if (gaze == null) return;
            _gazeHeadBefore = gaze.Head;
            gaze.Head = null;
            _gazeSuspended = true;
        }

        void RestoreGaze()
        {
            if (!_gazeSuspended) return;
            GazeDwell gaze = GazeDwell.Instance;
            if (gaze != null && gaze.Head == null) gaze.Head = _gazeHeadBefore;
            _gazeSuspended = false;
            _gazeHeadBefore = null;
        }

        bool TryCurrentAlignment(out AlignedPose pose)
        {
            if (_anchor != null && _anchor.HasAlignment)
            {
                pose = _anchor.Alignment;
                return true;
            }
            if (_anchor == null && _last != null && _last.Success && _last.HasPose)
            {
                pose = _last.Pose;
                return true;
            }
            pose = default(AlignedPose);
            return false;
        }

        bool HeadTrackedNow()
        {
            if (_anchor != null) return _anchor.HeadTracked;
            return _headPose == null || _headPose.State == HeadTrackingState.Tracking;
        }

        bool HeadLockedFlag()
        {
            var flags = UntitledGuitarApp.Runtime.Flags.FlagStore.Current;
            return flags != null && NeckAnchorPolicy.IsHeadLockedMode(flags.anchorMode);
        }

        bool LeftHanded()
        {
            return _neck != null && _neck.Options.leftHanded;
        }

        float StringHeightMm()
        {
            return _neck != null && _neck.Layout != null ? _neck.Layout.stringHeightMm : CoreNeckLayout.StringHeightMm;
        }

        static double Round1(double v)
        {
            return double.IsNaN(v) || double.IsInfinity(v) || v > 1e9 ? -1.0 : Math.Round(v, 1);
        }

        static double Round2(float v)
        {
            return Math.Round(v, 2);
        }

        void OnDestroy()
        {
            RestoreGaze();
            ShowNeck();
        }
    }
}
