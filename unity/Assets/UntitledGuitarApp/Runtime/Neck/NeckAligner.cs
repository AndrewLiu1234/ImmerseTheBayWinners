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
// dwell target fires while the player stares at the guitar). For each target a head-locked prompt says where to look; a
// capture happens when the head's forward has held still (all samples within a 1 degree wide patch, i.e. about 0.5 degree
// radius) for 1.2 s (GazeSteadiness; MASTER_PLAN's 1.2 s dwell, Person 2's 0.5 degree advice), or at once on Space. The
// reticle's arc fills meanwhile. After the 4th capture Core's NeckSolver fits the neck (NeckAlignFlow). Success: the anchor
// gets the pose (FloatingNeck.SetWorldPose, NeckMode.Aligned), the HUD shows the fit honestly and asks the player to check
// the real nut, `neck_aligned` is logged. Failure: the HUD says why in a few words (the full reason is logged), the neck goes
// back to what it was (the floating neck, or the previous alignment), and Space tries again while the message is up. The
// player always has a neck. Without real head rotation (no IMU tracking, no editor mouse-look) it refuses and says so.
//
// HUD (round 2, after the 12:38 preview showed a big centred block covering the nut it asked about): a small block of up to
// 4 head-locked lines at 1.2 m in the UPPER band of the view, on the side AWAY from the neck: a right-handed player sees the
// neck run from the headstock at the upper left of the view to the body at the lower right whichever part is looked at
// (the reticle, the point being looked at, is the view centre), so the block starts HudInnerAzimuthDeg right of centre and
// runs right; for a left-handed player it is mirrored. Line A has 1.6 degree caps, lines B-D 1.2 (the minimum); HudText
// draws exactly the asked cap height (fixed 12:30). Line lengths are capped (LineAMaxChars, LineMaxChars) from the measured
// ~1.0 degree per character at 1.6 degree caps (align_prompt_step1.png, 12:38), so the block stays inside the 46 degree wide
// view. A result fades out after SuccessShowSeconds (a failure after FailureShowSeconds), over FadeSeconds (no flashing).
// Every prompt string is a constant; the per-frame path (Step) builds no strings and allocates nothing (the final capture
// allocates the outcome, and the result text is built once).
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

        /// <summary>The block's inner edge, degrees from the view centre toward the side away from the neck.</summary>
        public const float HudInnerAzimuthDeg = 5f;

        /// <summary>Cap height of line A (what to look at / the result), degrees.</summary>
        public const float LineACapDeg = 1.6f;

        /// <summary>Cap height of lines B-D, degrees (HudText's minimum).</summary>
        public const float LineCapDeg = 1.2f;

        /// <summary>Line centres above the view centre, degrees: A, B, C, D (the view is 27 degrees tall: +-13.5).</summary>
        public static readonly float[] LineElevationDeg = { 12.0f, 9.9f, 8.1f, 6.3f };

        /// <summary>Longest line A: about 1.0 degree per character at 1.6 degree caps, so 14 characters are about 14 degrees.</summary>
        public const int LineAMaxChars = 14;

        /// <summary>Longest line B-D: about 0.77 degree per character at 1.2 degree caps, so 20 characters are about 15 degrees
        /// (the block then ends about 21 degrees right of centre, inside the 23 degree half-width).</summary>
        public const int LineMaxChars = 20;

        public const int HudLineCount = 4;

        /// <summary>Layout estimate (10% above the 12:38 measurement): one character is this many cap heights wide.</summary>
        public const float EstimatedCharWidthPerCap = 0.70f;

        /// <summary>Layout estimate: a line's box (ascenders and descenders included) is this many cap heights tall.</summary>
        public const float EstimatedLineHeightPerCap = 1.5f;

        /// <summary>
        /// Where HUD line <paramref name="line"/> with <paramref name="chars"/> characters lies in the head's view, as a rectangle
        /// in tangent coordinates (x = tan of the angle right of centre, y = tan of the angle above it), from the layout constants
        /// and the estimates above. Pure; used by the layout test. The preview checks the real rendered bounds instead.
        /// </summary>
        public static Rect EstimatedLineRect(int line, int chars, bool leftSide)
        {
            float cap = line == 0 ? LineACapDeg : LineCapDeg;
            float inner = Mathf.Tan(HudInnerAzimuthDeg * Mathf.Deg2Rad);
            float width = Mathf.Tan(cap * Mathf.Deg2Rad) * EstimatedCharWidthPerCap * Mathf.Max(0, chars);
            float height = Mathf.Tan(cap * Mathf.Deg2Rad) * EstimatedLineHeightPerCap;
            float centreY = Mathf.Tan(LineElevationDeg[Mathf.Clamp(line, 0, HudLineCount - 1)] * Mathf.Deg2Rad);
            float xMin = leftSide ? -inner - width : inner;
            return new Rect(xMin, centreY - 0.5f * height, width, height);
        }

        /// <summary>A capture needs the gaze to hold still within about this radius (Person 2: aim for dwell jitter 0.5 or less).</summary>
        public const float SteadyThresholdDeg = 0.5f;

        /// <summary>... for this long (MASTER_PLAN section 20: hold each for 1.2 s).</summary>
        public const float SteadyWindowSeconds = 1.2f;

        /// <summary>How long a success stays up, fade included.</summary>
        public const float SuccessShowSeconds = 4f;

        /// <summary>How long a failure stays up, fade included (Space retries meanwhile).</summary>
        public const float FailureShowSeconds = 8f;

        public const float FadeSeconds = 1f;

        static readonly Color StepColor = new Color(0.55f, 0.90f, 1.00f, 1f);
        static readonly Color PromptColor = new Color(1.00f, 1.00f, 1.00f, 1f);
        static readonly Color HintColor = new Color(0.75f, 0.88f, 1.00f, 1f);
        static readonly Color GoodColor = new Color(0.50f, 1.00f, 0.62f, 1f);
        static readonly Color BadColor = new Color(1.00f, 0.78f, 0.35f, 1f);

        /// <summary>Line A of each step prompt (at most LineAMaxChars).</summary>
        public static readonly string[] StepLineA = { "1/4: the nut", "2/4: the nut", "3/4: fret 5", "4/4: fret 12" };
        /// <summary>Line B of each step prompt (at most LineMaxChars).</summary>
        public static readonly string[] StepLineB = { "on the thick low E", "on the thin high E", "on the thick low E", "on the thick low E" };
        public const string ReanchorLineA = "Re-anchor: nut";
        public const string HoldHint = "Hold still, or Space";

        Transform _head;
        FloatingNeck _neck;
        ImuHeadPose _headPose;
        NeckAnchor _anchor;
        NeckAlignFlow _flow;
        FretboardGeometry _geometry;
        readonly HudText[] _lines = new HudText[HudLineCount];
        readonly Color[] _lineColors = new Color[HudLineCount];
        bool _hudVisible;
        bool _hudLeftSide;
        float _resultShownAt = -1f;
        float _resultSeconds;
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
            for (int i = 0; i < HudLineCount; i++)
            {
                HudText text = HudText.Create(hudParent, "AlignHud" + (char)('A' + i));
                text.SetAngularHeight(i == 0 ? LineACapDeg : LineCapDeg, HudDistanceM);
                text.transform.localRotation = Quaternion.identity;
                _lines[i] = text;
            }
            PlaceHud(false);
            SetHudVisible(false);
        }

        // The block on the side away from the neck: right of centre for a right-handed player, left for a left-handed one.
        void PlaceHud(bool leftSide)
        {
            _hudLeftSide = leftSide;
            float x = HudDistanceM * Mathf.Tan(HudInnerAzimuthDeg * Mathf.Deg2Rad) * (leftSide ? -1f : 1f);
            for (int i = 0; i < HudLineCount; i++)
            {
                HudText text = _lines[i];
                text.Anchor = leftSide ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
                text.Alignment = leftSide ? TextAlignment.Right : TextAlignment.Left;
                text.transform.localPosition = new Vector3(x, HudDistanceM * Mathf.Tan(LineElevationDeg[i] * Mathf.Deg2Rad), HudDistanceM);
            }
        }

        // ------------------------------------------------------------------ public surface

        /// <summary>Optional: the reticle whose arc shows the hold-still progress.</summary>
        public Reticle Reticle { get; set; }

        /// <summary>The key that captures at once / retries after a failure (KeyCode.None = off).</summary>
        public KeyCode ConfirmKey = KeyCode.Space;

        /// <summary>The operator key that starts a re-anchor, named in the success message ("Off the nut? Press Z").</summary>
        public string ReanchorKeyLabel = "Z";

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

        /// <summary>True while the prompt or a result is on screen.</summary>
        public bool HudVisible
        {
            get { return _hudVisible; }
        }

        /// <summary>One of the HUD lines (0 = A .. 3 = D), for previews and layout checks. Empty text = not shown.</summary>
        public HudText HudLine(int index)
        {
            Init();
            return index >= 0 && index < HudLineCount ? _lines[index] : null;
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
                if (_hudVisible && _resultShownAt >= 0f) FadeResult(nowSec);
                if (confirm && _retryOffered && _hudVisible) Retry();
                return;
            }
            if (!HeadTrackedNow())
            {
                if (_flow.Cancel("tracking stopped:\nalignment needs IMU")) Ended(_flow.Outcome);
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
            if (_head == null) return "no head transform\nalignment cannot run";
            if (RespectAnchorModeFlag && (_anchor != null ? _anchor.HeadLockedMode : HeadLockedFlag())) return "anchorMode flag is\nheadLocked";
            if (!HeadTrackedNow()) return "no head tracking:\nstart the IMU bridge";
            return null;
        }

        bool Refuse(string why, bool reanchor)
        {
            var outcome = new AlignOutcome { Reanchor = reanchor, Failure = why.Replace('\n', ' '), ShortReason = why };
            UGALog.Warn("neck_align_refused", outcome.Failure);
            _last = outcome;
            ShowResult(outcome);
            RaiseFinished(outcome);
            return false;
        }

        void Started(bool reanchor)
        {
            _retryOffered = false;
            _resultShownAt = -1f;
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
            PlaceHud(LeftHanded());
            string lineA = _flow.IsReanchor ? ReanchorLineA : StepLineA[Mathf.Clamp(step, 0, StepLineA.Length - 1)];
            string lineB = _flow.IsReanchor ? StepLineB[0] : StepLineB[Mathf.Clamp(step, 0, StepLineB.Length - 1)];
            SetLines(lineA, StepColor, lineB, PromptColor, HoldHint, HintColor, string.Empty, HintColor);
            _resultShownAt = -1f;
            SetHudVisible(true);
        }

        void ShowResult(AlignOutcome outcome)
        {
            if (outcome == null) return;
            PlaceHud(LeftHanded());
            string offTheNut = "Off the nut? Press " + ReanchorKeyLabel;
            if (outcome.Success && !outcome.Reanchor)
            {
                // Honest: the fit error is NOT the nut error (Person 2's G1 caution), so the player is asked to check the nut.
                SetLines("Aligned", GoodColor,
                         "fit " + outcome.ResidualRmsMm.ToString("0") + "mm · nut " + (outcome.NutDistanceM * 100f).ToString("0") + "cm", PromptColor,
                         offTheNut, HintColor, string.Empty, HintColor);
                _retryOffered = false;
                _resultSeconds = SuccessShowSeconds;
            }
            else if (outcome.Success)
            {
                SetLines("Re-anchored", GoodColor, "nut moved " + outcome.NutShiftMm.ToString("0") + " mm", PromptColor,
                         offTheNut, HintColor, string.Empty, HintColor);
                _retryOffered = false;
                _resultSeconds = SuccessShowSeconds;
            }
            else
            {
                string first, second;
                SplitReason(outcome.ShortReason ?? outcome.Failure ?? "unknown error", out first, out second);
                SetLines("Not aligned", BadColor, first, BadColor, second, BadColor,
                         ConfirmKey != KeyCode.None ? ConfirmKey + ": try again" : string.Empty, HintColor);
                _retryOffered = true;
                _resultSeconds = FailureShowSeconds;
            }
            _resultShownAt = _now;
            SetHudVisible(true);
        }

        // Holds the result, then fades it out over FadeSeconds and hides it. Only colour values change: no allocation.
        void FadeResult(float nowSec)
        {
            float age = nowSec - _resultShownAt;
            if (age >= _resultSeconds)
            {
                SetHudVisible(false);
                _resultShownAt = -1f;
                _retryOffered = false;
                return;
            }
            float alpha = Mathf.Clamp01((_resultSeconds - age) / FadeSeconds);
            for (int i = 0; i < HudLineCount; i++)
            {
                Color c = _lineColors[i];
                c.a *= alpha;
                _lines[i].Color = c;
            }
        }

        void SetLines(string a, Color aColor, string b, Color bColor, string c, Color cColor, string d, Color dColor)
        {
            SetLine(0, a, aColor);
            SetLine(1, b, bColor);
            SetLine(2, c, cColor);
            SetLine(3, d, dColor);
        }

        void SetLine(int index, string text, Color color)
        {
            _lines[index].SetText(text ?? string.Empty);
            _lineColors[index] = color;
            _lines[index].Color = color;
            _lines[index].Visible = _hudVisible && !string.IsNullOrEmpty(text);
        }

        void SetHudVisible(bool visible)
        {
            _hudVisible = visible;
            for (int i = 0; i < HudLineCount; i++)
            {
                if (_lines[i] == null) continue;
                _lines[i].Visible = visible && !string.IsNullOrEmpty(_lines[i].Text);
            }
        }

        /// <summary>
        /// The two short lines of a failure: the reason's own line break when it has one (ShortReason is written that way),
        /// else split at the space nearest the middle when longer than <see cref="LineMaxChars"/>.
        /// </summary>
        public static void SplitReason(string reason, out string first, out string second)
        {
            reason = reason ?? string.Empty;
            int newline = reason.IndexOf('\n');
            if (newline >= 0)
            {
                first = reason.Substring(0, newline);
                second = reason.Substring(newline + 1);
                return;
            }
            SplitInTwo(reason, LineMaxChars, out first, out second);
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
