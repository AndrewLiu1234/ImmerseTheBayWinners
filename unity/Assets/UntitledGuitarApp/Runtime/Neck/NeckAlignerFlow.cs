// NeckAlignerFlow: the plain-C# half of the 4-glance neck alignment (docs/MASTER_PLAN.md section 20), with no MonoBehaviour
// and no engine calls, so all of it is unit-tested offline (Tests/EditMode/NeckAlignerTests.cs):
//   GazeSteadiness  "has the head's forward held still?": the longest recent run of samples that all lie within ThresholdDeg
//                   of their own mean direction; steady once that run spans WindowSeconds. The capture is the run's mean
//                   direction (averaging the run cuts the jitter of a single frame).
//   NeckAlignFlow   the step machine: Begin (4 targets, AlignTarget order) or BeginReanchor (1 target, the nut on low E);
//                   Tick once per frame with the head pivot, the head's forward and "Space pressed"; after the last capture
//                   it calls Core's NeckSolver (Solve, or Reanchor) and turns the result into an AlignOutcome.
//   AlignOutcome    what happened: success or the reason, the placed pose, residuals, nut distance, dwell jitter.
// NeckAligner (the MonoBehaviour) only feeds it from the head transform and shows the HUD.
//
// Quality gates on a full alignment, in order: Core's own (Solve fails beyond 50 mm RMS or outside 30-90 cm), then
//   ResidualRmsMm <= MaxResidualRmsMm (15 mm, gate G1),
//   ViewerElevationDeg >= MinViewerElevationDeg: the fitted fretboard must not face away from the player. A mirrored fit (the
//   two nut glances swapped) puts the viewer far behind the fretboard; a real guitar seen from the player's eyes is at worst
//   nearly edge-on (AlignMath.ViewerElevationDeg).
// Person 2's G1 caution (docs/STATUS.md): the residual understates the nut error about 5x (depth along the gaze), so a
// passing residual is not proof; the outcome says so, and the human checks the nut against a physical marker.
using System;
using UnityEngine;
using UntitledGuitarApp.Core.Geometry;
using UntitledGuitarApp.Core.Theory;
using CoreNeckPose = UntitledGuitarApp.Core.Geometry.NeckPose;

namespace UntitledGuitarApp.Runtime.Neck
{
    /// <summary>Detects a steady gaze: every sample of the recent run within <see cref="ThresholdDeg"/> of the run's mean.</summary>
    public sealed class GazeSteadiness
    {
        /// <summary>Most samples kept (about 4 s at 120 frames per second); the run is trimmed to the window anyway.</summary>
        public const int Capacity = 512;

        readonly Vector3[] _directions = new Vector3[Capacity];
        readonly float[] _times = new float[Capacity];
        int _start;
        int _count;
        Vector3 _sum;
        float _jitterDeg;

        public GazeSteadiness(float thresholdDeg, float windowSeconds)
        {
            ThresholdDeg = thresholdDeg;
            WindowSeconds = windowSeconds;
            MaxGapSeconds = 0.25f;
        }

        /// <summary>The largest allowed angle between any sample of the run and the run's mean direction, degrees.</summary>
        public float ThresholdDeg { get; set; }

        /// <summary>How long the run must last to count as steady, seconds.</summary>
        public float WindowSeconds { get; set; }

        /// <summary>A gap between samples longer than this (a stalled frame, a paused editor) starts a new run.</summary>
        public float MaxGapSeconds { get; set; }

        /// <summary>Samples in the current run.</summary>
        public int Count
        {
            get { return _count; }
        }

        /// <summary>Time from the run's first to its last sample, seconds.</summary>
        public float SteadySeconds
        {
            get { return _count < 2 ? 0f : _times[Index(_count - 1)] - _times[_start]; }
        }

        /// <summary>0..1: how much of the window the current run already covers.</summary>
        public float Progress
        {
            get { return WindowSeconds <= 0f ? (_count > 0 ? 1f : 0f) : Mathf.Clamp01(SteadySeconds / WindowSeconds); }
        }

        /// <summary>True once the run covers the window.</summary>
        public bool IsSteady
        {
            get { return _count >= 2 && SteadySeconds >= WindowSeconds; }
        }

        /// <summary>The run's mean direction (unit), or zero with no samples.</summary>
        public Vector3 MeanDirection
        {
            get { return _count == 0 ? Vector3.zero : _sum.normalized; }
        }

        /// <summary>The largest angle of a run sample from the run's mean, degrees (0 with fewer than 2 samples).</summary>
        public float JitterDeg
        {
            get { return _jitterDeg; }
        }

        public void Reset()
        {
            _start = 0;
            _count = 0;
            _sum = Vector3.zero;
            _jitterDeg = 0f;
        }

        /// <summary>Adds one sample (any length; zero or non-finite directions are ignored). No allocation.</summary>
        public void Add(float timeSec, Vector3 direction)
        {
            float length = direction.magnitude;
            if (!(length > 1e-6f) || float.IsInfinity(length) || float.IsNaN(timeSec)) return;
            Vector3 d = direction / length;

            if (_count > 0)
            {
                float newest = _times[Index(_count - 1)];
                if (timeSec < newest || timeSec - newest > MaxGapSeconds) Reset();
                // A jump of more than twice the threshold from the run's mean cannot share a run with the old samples.
                else if (AlignMath.AngleDeg(d, _sum) > 2f * ThresholdDeg) Reset();
            }
            if (_count == Capacity) DropOldest();
            int slot = Index(_count);
            _directions[slot] = d;
            _times[slot] = timeSec;
            _count++;
            _sum += d;

            // Keep only as much history as the window needs (the run stays the shortest one that still covers the window)...
            while (_count > 2 && timeSec - _times[Index(1)] >= WindowSeconds) DropOldest();
            // ...and shrink it from the old end until every sample is within the threshold of the mean.
            while (true)
            {
                _jitterDeg = MaxDeviationDeg();
                if (_jitterDeg <= ThresholdDeg || _count <= 1) break;
                DropOldest();
            }
            if (_count <= 1) _jitterDeg = 0f;
        }

        float MaxDeviationDeg()
        {
            if (_count < 2) return 0f;
            Vector3 mean = _sum.normalized;
            float max = 0f;
            for (int i = 0; i < _count; i++)
            {
                float a = AlignMath.AngleDeg(_directions[Index(i)], mean);
                if (a > max) max = a;
            }
            return max;
        }

        void DropOldest()
        {
            if (_count == 0) return;
            _sum -= _directions[_start];
            _start = (_start + 1) % Capacity;
            _count--;
            if (_count == 0) _sum = Vector3.zero;
        }

        int Index(int i)
        {
            return (_start + i) % Capacity;
        }
    }

    public enum AlignPhase
    {
        Idle = 0,
        Capturing = 1,
        Succeeded = 2,
        Failed = 3,
        Cancelled = 4,
    }

    /// <summary>The result of one alignment or re-anchor. Built once at the end (not per frame).</summary>
    public sealed class AlignOutcome
    {
        public bool Success;
        public bool Reanchor;
        /// <summary>Why it failed, in words for the HUD and the log; null on success.</summary>
        public string Failure;
        /// <summary>The neck frame's world pose (valid when <see cref="Success"/>; also filled for a fit that failed a gate).</summary>
        public AlignedPose Pose;
        public bool HasPose;
        /// <summary>Core's point-to-ray residuals, mm (NaN for a re-anchor, which fits nothing). NOT the nut error: see the file header.</summary>
        public double ResidualRmsMm = double.NaN;
        public double ResidualMaxMm = double.NaN;
        /// <summary>Per target, indexed by AlignTarget (empty for a re-anchor or a failed solve).</summary>
        public double[] TargetResidualMm = new double[0];
        /// <summary>Head pivot to the nut, metres (0 without a pose).</summary>
        public float NutDistanceM;
        /// <summary>The viewer's angle above the fitted fretboard plane, degrees (AlignMath.ViewerElevationDeg).</summary>
        public float ViewerElevationDeg;
        /// <summary>A re-anchor: how far the nut moved, mm.</summary>
        public float NutShiftMm;
        /// <summary>The largest dwell jitter among the captures, degrees (Person 2's advice: aim for 0.5 or less).</summary>
        public float MaxJitterDeg;
        /// <summary>How many captures were taken with Space instead of a steady dwell.</summary>
        public int ConfirmedCount;
        /// <summary>The head pivot and the captured gaze directions (world), in capture order.</summary>
        public Vector3 HeadPivot;
        public Vector3[] Directions = new Vector3[0];
    }

    /// <summary>The alignment step machine. One instance per aligner; Begin / BeginReanchor restart it. No allocation in Tick
    /// except at the final capture (the solve and the outcome).</summary>
    public sealed class NeckAlignFlow
    {
        /// <summary>Gate G1 (MASTER_PLAN section 20): the fit residual at alignment must be at most this, mm.</summary>
        public const float MaxResidualRmsMm = 15f;

        /// <summary>A fit whose fretboard faces further away from the viewer than this is rejected as mirrored, degrees.</summary>
        public const float MinViewerElevationDeg = -25f;

        /// <summary>The next capture waits until the gaze is at least this far from the previous one (no double capture).</summary>
        public const float DefaultMinSeparationDeg = 1.5f;

        readonly NeckSolver _solver;
        readonly GazeSteadiness _steady;
        readonly GazeRay[] _rays = new GazeRay[NeckSolver.TargetCount];
        readonly Vector3[] _directions = new Vector3[NeckSolver.TargetCount];

        AlignPhase _phase = AlignPhase.Idle;
        bool _reanchor;
        int _step;
        int _steps;
        bool _leftHanded;
        float _stringHeightMm;
        AlignedPose _current;
        Vector3 _pivot;
        bool _hasPivot;
        bool _hasLast;
        Vector3 _lastDirection;
        bool _awayFromLast;
        float _maxJitterDeg;
        int _confirmed;
        AlignOutcome _outcome;

        public NeckAlignFlow(FretboardGeometry geometry, NeckSolverConfig config, float steadyThresholdDeg, float steadyWindowSeconds)
        {
            _solver = new NeckSolver(geometry ?? CoreNeckLayout.DefaultGeometry, config ?? NeckSolverConfig.Default);
            _steady = new GazeSteadiness(steadyThresholdDeg, steadyWindowSeconds);
            MinSeparationDeg = DefaultMinSeparationDeg;
        }

        public NeckSolver Solver
        {
            get { return _solver; }
        }

        public GazeSteadiness Steadiness
        {
            get { return _steady; }
        }

        public float MinSeparationDeg { get; set; }

        public AlignPhase Phase
        {
            get { return _phase; }
        }

        public bool IsRunning
        {
            get { return _phase == AlignPhase.Capturing; }
        }

        public bool IsReanchor
        {
            get { return _reanchor; }
        }

        /// <summary>The capture being waited for, 0-based (StepCount when done).</summary>
        public int Step
        {
            get { return _step; }
        }

        public int StepCount
        {
            get { return _steps; }
        }

        /// <summary>The target the player should look at now.</summary>
        public AlignTarget CurrentTarget
        {
            get { return _reanchor ? AlignTarget.NutLowE : (AlignTarget)Mathf.Clamp(_step, 0, NeckSolver.TargetCount - 1); }
        }

        /// <summary>0..1 for the reticle's arc: how long the gaze has held still (0 while it is still on the previous target).</summary>
        public float Progress
        {
            get { return IsRunning && (_awayFromLast || !_hasLast) ? _steady.Progress : 0f; }
        }

        /// <summary>The last outcome (null before the first finish).</summary>
        public AlignOutcome Outcome
        {
            get { return _outcome; }
        }

        /// <summary>Starts the full 4-glance alignment (AlignTarget order).</summary>
        public void Begin(bool leftHanded, float stringHeightMm)
        {
            Start(false, NeckSolver.TargetCount, leftHanded, stringHeightMm);
        }

        /// <summary>Starts a one-glance re-anchor of <paramref name="current"/> (the nut on low E).</summary>
        public void BeginReanchor(AlignedPose current, bool leftHanded, float stringHeightMm)
        {
            Start(true, 1, leftHanded, stringHeightMm);
            _current = current;
        }

        void Start(bool reanchor, int steps, bool leftHanded, float stringHeightMm)
        {
            _reanchor = reanchor;
            _steps = steps;
            _step = 0;
            _leftHanded = leftHanded;
            _stringHeightMm = stringHeightMm;
            _hasPivot = false;
            _hasLast = false;
            _awayFromLast = false;
            _maxJitterDeg = 0f;
            _confirmed = 0;
            _steady.Reset();
            _phase = AlignPhase.Capturing;
        }

        /// <summary>Stops a running flow; the outcome says why. Returns false when nothing was running.</summary>
        public bool Cancel(string reason)
        {
            if (_phase != AlignPhase.Capturing) return false;
            _phase = AlignPhase.Cancelled;
            _outcome = new AlignOutcome { Reanchor = _reanchor, Failure = string.IsNullOrEmpty(reason) ? "cancelled" : reason };
            return true;
        }

        /// <summary>
        /// One frame: the head pivot (world metres), the head's forward, and whether Space (or Confirm) was pressed this frame.
        /// Returns true when this call captured a target or finished the flow (the HUD then changes).
        /// </summary>
        public bool Tick(float timeSec, Vector3 headPivotM, Vector3 forward, bool confirm)
        {
            if (_phase != AlignPhase.Capturing) return false;
            if (!_hasPivot)
            {
                _pivot = headPivotM;
                _hasPivot = true;
            }

            if (_hasLast && !_awayFromLast && AlignMath.AngleDeg(forward, _lastDirection) >= MinSeparationDeg) _awayFromLast = true;
            bool mayDwell = !_hasLast || _awayFromLast;
            if (mayDwell) _steady.Add(timeSec, forward);
            else _steady.Reset();

            Vector3 direction;
            float jitter;
            if (confirm)
            {
                direction = _steady.Count >= 2 ? _steady.MeanDirection : forward.normalized;
                jitter = _steady.JitterDeg;
                _confirmed++;
            }
            else if (mayDwell && _steady.IsSteady)
            {
                direction = _steady.MeanDirection;
                jitter = _steady.JitterDeg;
            }
            else
            {
                return false;
            }
            if (!(direction.sqrMagnitude > 0.5f)) return false;   // no usable forward yet (zero vector)

            int index = _reanchor ? (int)AlignTarget.NutLowE : _step;
            _directions[_step] = direction;
            _rays[index] = AlignMath.ToCoreRay(headPivotM, direction, _pivot);
            _maxJitterDeg = Mathf.Max(_maxJitterDeg, jitter);
            _lastDirection = direction;
            _hasLast = true;
            _awayFromLast = false;
            _steady.Reset();
            _step++;
            if (_step >= _steps) Finish(headPivotM);
            return true;
        }

        void Finish(Vector3 headPivotM)
        {
            var outcome = new AlignOutcome
            {
                Reanchor = _reanchor,
                MaxJitterDeg = _maxJitterDeg,
                ConfirmedCount = _confirmed,
                HeadPivot = _pivot,
                Directions = new Vector3[_steps],
            };
            Array.Copy(_directions, outcome.Directions, _steps);

            if (_reanchor) FinishReanchor(outcome, headPivotM);
            else FinishFull(outcome);
            _outcome = outcome;
            _phase = outcome.Success ? AlignPhase.Succeeded : AlignPhase.Failed;
        }

        void FinishFull(AlignOutcome outcome)
        {
            NeckSolveResult result = _solver.Solve(_rays);
            if (!result.Success)
            {
                outcome.Failure = result.Failure;
                return;
            }
            outcome.ResidualRmsMm = result.ResidualRmsMm;
            outcome.ResidualMaxMm = result.ResidualMaxMm;
            outcome.TargetResidualMm = (double[])result.TargetResidualMm.Clone();
            FillPose(outcome, AlignMath.FromCore(result.Pose, _pivot, _leftHanded, _stringHeightMm));

            if (!(result.ResidualRmsMm <= MaxResidualRmsMm))
                outcome.Failure = "fit error " + result.ResidualRmsMm.ToString("0") + " mm is above " + MaxResidualRmsMm.ToString("0") +
                                  " mm: hold still on each point and redo the 4 glances";
            else if (outcome.ViewerElevationDeg < MinViewerElevationDeg)
                outcome.Failure = "the fit faces away from you: were the two nut glances swapped? Low E is the thick string";
            else
                outcome.Success = true;
        }

        void FinishReanchor(AlignOutcome outcome, Vector3 headPivotM)
        {
            CoreNeckPose before = AlignMath.ToCore(_current, _pivot, _leftHanded, _stringHeightMm);
            CoreNeckPose after = _solver.Reanchor(before, _rays[(int)AlignTarget.NutLowE]);
            AlignedPose pose = AlignMath.FromCore(after, _pivot, _leftHanded, _stringHeightMm);
            if (!pose.IsFinite)
            {
                outcome.Failure = "the re-anchor glance gave no usable direction";
                return;
            }
            FillPose(outcome, pose);
            outcome.NutShiftMm = (pose.nut - _current.nut).magnitude * AlignMath.MmPerMetre;
            outcome.Success = true;
        }

        void FillPose(AlignOutcome outcome, AlignedPose pose)
        {
            outcome.Pose = pose;
            outcome.HasPose = true;
            outcome.NutDistanceM = (pose.nut - _pivot).magnitude;
            outcome.ViewerElevationDeg = AlignMath.ViewerElevationDeg(pose, _pivot);
        }
    }
}
