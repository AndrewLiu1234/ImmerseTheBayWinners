// LearnFlow: the learn-mode state machine for one chord. Pure C# (no engine objects), unit-tested in LearnFlowTests.
//
//   Begin(chord) -> Tune (strings 6 .. 1) -> FingerByFinger (finger 1 first) -> PluckCheck (every sounding string, until one
//   pass is all correct) -> StrumCheck (until a clean strum) -> Done.
//
// It takes verdict events (ILearnVerdictSink) and tells the screen what to show (ILearnView). Skip / SkipTo move on by hand
// (a live demo must be skippable); Stop ends learn mode. Tick(dt) runs the one timed step: an all-correct pluck pass stays
// on screen for AllCorrectHoldSeconds before the strum check starts. Allocation-free after Begin (the prompts are built
// once per chord).
using UntitledGuitarApp.Runtime.Hud.Learn;

namespace UntitledGuitarApp.Runtime.Practice.Learn
{
    public sealed class LearnFlow : ILearnVerdictSink
    {
        /// <summary>An all-correct pluck pass (one check per string) stays this long before the strum check starts, seconds.</summary>
        public const float AllCorrectHoldSeconds = 1.5f;

        /// <summary>The prompt when the strum check starts (5 words).</summary>
        public const string StrumPrompt = "Now strum the whole chord";

        /// <summary>Shown when a pluck pass is all correct (3 words).</summary>
        public const string AllCorrectTip = "Every string rings!";

        readonly ILearnView _view;
        readonly StringMark[] _pass = new StringMark[LearnChord.StringCount];
        readonly bool[] _passHas = new bool[LearnChord.StringCount];

        LearnChord _chord;
        LearnStage _stage = LearnStage.Idle;
        int _tuneString;
        int _step;
        int _passes;
        float _hold;
        string _pluckPrompt = string.Empty;

        public LearnFlow(ILearnView view)
        {
            _view = view;
        }

        public LearnStage Stage { get { return _stage; } }

        public LearnChord Chord { get { return _chord; } }

        /// <summary>The string being tuned (6 .. 1) during Tune, else 0.</summary>
        public int TuneString { get { return _stage == LearnStage.Tune ? _tuneString : 0; } }

        /// <summary>The finger-by-finger step on screen (0-based) during FingerByFinger.</summary>
        public int StepIndex { get { return _step; } }

        /// <summary>Completed pluck passes in this pluck check.</summary>
        public int PluckPasses { get { return _passes; } }

        /// <summary>True while an all-correct pass is being held on screen before the strum check.</summary>
        public bool Holding { get { return _hold > 0f; } }

        /// <summary>The prompt when the pluck check starts, e.g. "Pluck each string, 6 to 1".</summary>
        public string PluckPrompt { get { return _pluckPrompt; } }

        /// <summary>Starts learn mode for <paramref name="chord"/> at the tuner (restarts if already running). False for a null chord.</summary>
        public bool Begin(LearnChord chord)
        {
            if (chord == null) return false;
            if (_stage != LearnStage.Idle) Stop();
            _chord = chord;
            _pluckPrompt = PluckPromptFor(chord);
            Enter(LearnStage.Tune);
            return true;
        }

        /// <summary>Ends learn mode: the screen goes back to the plain chord.</summary>
        public void Stop()
        {
            if (_stage == LearnStage.Idle) return;
            _hold = 0f;
            _view.LeaveStage(_stage, _chord);
            _stage = LearnStage.Idle;
            _view.EnterStage(LearnStage.Idle, _chord);
        }

        /// <summary>Moves to the next stage now (Done stays Done; Idle stays Idle).</summary>
        public void Skip()
        {
            if (_stage == LearnStage.Idle || _stage == LearnStage.Done) return;
            Advance(_stage + 1);
        }

        /// <summary>Jumps forward to <paramref name="stage"/> (a dev script's skip). Ignored when it is not ahead of the current stage.</summary>
        public void SkipTo(LearnStage stage)
        {
            if (_stage == LearnStage.Idle || stage <= _stage || stage > LearnStage.Done) return;
            Advance(stage);
        }

        /// <summary>Advances the timed hold by <paramref name="dt"/> seconds.</summary>
        public void Tick(float dt)
        {
            if (!(_hold > 0f)) return;
            _hold -= dt > 0f ? dt : 0f;
            if (_hold <= 0f)
            {
                _hold = 0f;
                if (_stage == LearnStage.PluckCheck) Advance(LearnStage.StrumCheck);
            }
        }

        // ------------------------------------------------------------------ ILearnVerdictSink

        public void OnTunerReading(float cents, float confidence)
        {
            if (_stage != LearnStage.Tune) return;
            _view.ShowTunerReading(cents, confidence);
        }

        public void OnStringInTune(int stringNumber)
        {
            if (_stage != LearnStage.Tune || stringNumber != _tuneString) return;
            if (_tuneString > 1)
            {
                _tuneString--;
                _view.ShowTunerTarget(_tuneString, LearnChord.TuningNote(_tuneString));
            }
            else
            {
                Advance(LearnStage.FingerByFinger);
            }
        }

        public void OnFingerPlaced(int finger)
        {
            if (_stage != LearnStage.FingerByFinger || _step >= _chord.StepCount) return;
            if (finger != _chord.Step(_step).finger) return;
            _step++;
            if (_step >= _chord.StepCount) Advance(LearnStage.PluckCheck);
            else ShowStep();
        }

        public void OnPluckResult(int stringNumber, StringMark mark, string tip, TipIcon icon)
        {
            if (_stage != LearnStage.PluckCheck || _hold > 0f) return;
            if (stringNumber < 1 || stringNumber > LearnChord.StringCount) return;
            int i = stringNumber - 1;
            if (_passHas[i]) NewPass();   // the same string again: the player started over
            _passHas[i] = true;
            _pass[i] = mark;
            _view.ShowStringMark(stringNumber, mark);
            if (!string.IsNullOrEmpty(tip)) _view.ShowTip(tip, icon);

            if (!PassComplete()) return;
            _passes++;
            bool allCorrect = PassAllCorrect();
            NewPass();
            if (!allCorrect) return;   // the tip for the fault stays; the next pass starts with the next pluck
            _view.ShowTip(AllCorrectTip, TipIcon.Good);
            _hold = AllCorrectHoldSeconds;
        }

        public void OnStrumResult(bool clean, string issue, TipIcon icon)
        {
            if (_stage != LearnStage.StrumCheck) return;
            _view.ShowStrum(clean, issue, icon);
            if (clean) Advance(LearnStage.Done);
        }

        // ------------------------------------------------------------------ stages

        void Advance(LearnStage next)
        {
            _hold = 0f;
            _view.LeaveStage(_stage, _chord);
            Enter(next);
        }

        void Enter(LearnStage stage)
        {
            _stage = stage;
            _view.EnterStage(stage, _chord);
            switch (stage)
            {
                case LearnStage.Tune:
                    _tuneString = LearnChord.StringCount;
                    _view.ShowTunerTarget(_tuneString, LearnChord.TuningNote(_tuneString));
                    break;
                case LearnStage.FingerByFinger:
                    _step = 0;
                    if (_chord.StepCount == 0) Advance(LearnStage.PluckCheck);   // an all-open chord has nothing to place
                    else ShowStep();
                    break;
                case LearnStage.PluckCheck:
                    _passes = 0;
                    NewPass();
                    _view.ShowTip(_pluckPrompt, TipIcon.None);
                    break;
                case LearnStage.StrumCheck:
                    _view.ShowTip(StrumPrompt, TipIcon.None);
                    break;
            }
        }

        void ShowStep()
        {
            LearnChordStep s = _chord.Step(_step);
            _view.ShowFingerStep(s.finger, s.stringNumber, s.fret, _step + 1, _chord.StepCount);
        }

        void NewPass()
        {
            for (int i = 0; i < _passHas.Length; i++)
            {
                _passHas[i] = false;
                _pass[i] = StringMark.None;
            }
        }

        bool PassComplete()
        {
            for (int s = 1; s <= LearnChord.StringCount; s++)
                if (_chord.IsSounding(s) && !_passHas[s - 1]) return false;
            return true;
        }

        bool PassAllCorrect()
        {
            for (int s = 1; s <= LearnChord.StringCount; s++)
                if (_chord.IsSounding(s) && _pass[s - 1] != StringMark.Correct) return false;
            return true;
        }

        /// <summary>"Pluck each string, 6 to 1" (from the chord's lowest sounding string). Pure.</summary>
        public static string PluckPromptFor(LearnChord chord)
        {
            int from = chord != null ? chord.LowestSoundingString : LearnChord.StringCount;
            return "Pluck each string, " + from + " to 1";
        }
    }
}
