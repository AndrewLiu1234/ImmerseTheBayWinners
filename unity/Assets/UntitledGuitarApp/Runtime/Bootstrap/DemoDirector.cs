// DemoDirector: the operator's three modes over the rig Bootstrap composed.
//   Idle  = the walking skeleton: floating neck with one chord's dots and X/O, the fretting ghost hand on it, the head-locked
//           chord label; the strumming hand rests.
//   Learn = one chord: tune, finger by finger, pluck check, strum check (LearnPresenter + Core's live tuner + the TEMPORARY
//           SIMULATED learn script until Core's pluck/strum checks land). The learn presenter owns the fretting hand.
//   Play  = play along with a song through Person 2's Core (CorePlayAlong -> PracticeFrame), shown on the play HUD
//           (PlayAlongPresenter) and by both ghost hands (GhostHandsDirector: fretting morphs one beat early, strumming plays
//           the song's pattern).
// Driven by DemoKeys on the Mac keyboard (the operator sits at the Mac while a judge wears the glasses) and, for smoke runs, by
// command-line arguments (all optional; see the constants). Main thread only. Per frame it polls keys, ticks the active mode and
// rebuilds its status text only when the mode, item or phase changes.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UntitledGuitarApp.Core.Songs;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.GhostHand;
using UntitledGuitarApp.Runtime.Logging;
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Practice;
using UntitledGuitarApp.Runtime.Practice.Learn;
using UntitledGuitarApp.Runtime.Practice.Learn.Dev;

namespace UntitledGuitarApp.Runtime
{
    [DisallowMultipleComponent]
    public sealed class DemoDirector : MonoBehaviour
    {
        /// <summary>-ugaDemoMode idle|learn|play: the mode to start in (smoke runs; default idle).</summary>
        public const string ModeArgument = "-ugaDemoMode";
        /// <summary>-ugaSong &lt;id&gt;: a file name in StreamingAssets/songs without ".song.json".</summary>
        public const string SongArgument = "-ugaSong";
        /// <summary>-ugaChord &lt;name&gt;: G, C, D, Em or Am (idle and learn).</summary>
        public const string ChordArgument = "-ugaChord";
        /// <summary>-ugaTempo &lt;preset&gt;: e.g. 0.75; the nearest of the flags' tempoPresets is used.</summary>
        public const string TempoArgument = "-ugaTempo";
        public const float DefaultTempoPreset = 0.75f;

        Bootstrap _boot;
        readonly List<string> _songIds = new List<string>();
        float[] _tempos = { DefaultTempoPreset };
        int _appliedGeneration = -1;
        PracticeFrame _practice = PracticeFrame.Idle;
        readonly FingertipDot[] _dots = new FingertipDot[FloatingNeck.MaxDots];
        readonly StringMark[] _marks = new StringMark[NeckLayout.StringCount];
        readonly Vector3[] _targets = new Vector3[4];
        string _idleChord = LearnChords.DefaultChord;
        string _lastPlayChord = LearnChords.DefaultChord;
        string _statusText = "";
        int _statusKey = int.MinValue;
        bool _pendingAlign;
        bool _resultsDimmed;
        bool _scriptedChecks;
        Hud.HudText _checksLabel;

        /// <summary>The disclosure shown while learn/play verdicts are scripted and the replay badge is not showing.</summary>
        public const string ScriptedChecksText = "SIMULATED CHECKS";

        /// <summary>Whether the "SIMULATED CHECKS" label shows: scripted verdicts, and only when the replay badge is hidden
        /// ("SIMULATED INPUT" is the stronger disclosure and takes the same spot). Pure.</summary>
        public static bool ShowScriptedChecks(bool scriptedChecks, bool replayBadgeShowing)
        {
            return scriptedChecks && !replayBadgeShowing;
        }

        public DemoState State { get; private set; }
        /// <summary>The Blender-posed strumming hand on the neck's strum zone, or null when its FBX/poses are missing.</summary>
        public PosedGhostHand StrummingHand { get; private set; }
        public GhostHandsDirector Hands { get; private set; }
        public CorePlayAlong PlayAlong { get; private set; }
        public PlayAlongPresenter Play { get; private set; }
        public Hud.Learn.LearnHud LearnView { get; private set; }
        public LearnPresenter Learn { get; private set; }
        public LearnMicTuner LearnTuner { get; private set; }
        public DevLearnScript LearnScript { get; private set; }

        /// <summary>One line for the Mac-screen spectator: mode, item, phase and the operator's keys. Rebuilt only on change.</summary>
        public string StatusText { get { return _statusText; } }

        public static DemoDirector Create(Bootstrap boot, FlagSet flags)
        {
            var go = new GameObject("DemoDirector");
            go.transform.SetParent(boot.transform, false);
            DemoDirector director = go.AddComponent<DemoDirector>();
            director.Compose(boot, flags);
            return director;
        }

        void Compose(Bootstrap boot, FlagSet flags)
        {
            _boot = boot;
            FloatingNeck neck = boot.Neck;
            if (neck != null && boot.Poses != null)
            {
                PosedGhostHand strum = GhostHandsDirector.CreateStrummingHand(neck, boot.Poses);   // under the strum zone
                if (strum != null && strum.IsAvailable) StrummingHand = strum;
                else if (strum != null) Destroy(strum.gameObject);
            }
            Hands = GhostHandsDirector.Create(boot.PosedFrettingHand, StrummingHand);   // a null hand is skipped
            UGALog.Event("ghost_strum", "posed", StrummingHand != null);

            if (neck != null && boot.Head != null)
            {
                Play = PlayAlongPresenter.Create(boot.transform, boot.Head, neck);
                PlayAlong = new CorePlayAlong();
                ComposeLearn(boot, neck);
            }

            ListSongs();
            ReadFlags(flags);
            State = new DemoState(_songIds.Count, ChordCount(flags), _tempos.Length, DemoState.NearestIndex(_tempos, DefaultTempoPreset));
            ApplyArguments(Environment.GetCommandLineArgs(), flags);
            FlagStore.Changed += OnFlagsChanged;
            UGALog.Event("demo_ready", "songs", _songIds.Count, "chords", State.ChordCount, "learn", LearnScript != null);
        }

        // The learn worker's wiring (person1/sl2-learn-flow): the learn card hangs off the neck socket, its marks dock in the
        // neck's X/O column at the nut, and the card sits at fret LearnHudLayout.CardFret.
        void ComposeLearn(Bootstrap boot, FloatingNeck neck)
        {
            NeckLayout layout = neck.Layout;
            if (layout == null || layout.fretXMm == null || layout.fretXMm.Length <= Hud.Learn.LearnHudLayout.CardFret) return;
            var nut = new Vector3[NeckLayout.StringCount];
            for (int s = 1; s <= NeckLayout.StringCount; s++)
                nut[s - 1] = NeckMath.LocalMetres(0f, layout.stringHeightMm, layout.stringZAtNutMm[s - 1]);
            LearnView = Hud.Learn.LearnHud.Create(boot.transform, boot.Head, neck.Socket);
            LearnView.SetNeck(neck.Socket, nut, layout.fretXMm[Hud.Learn.LearnHudLayout.CardFret] * NeckMath.MetresPerMm);
            Learn = LearnPresenter.Create(boot.transform, LearnView, neck, boot.PosedFrettingHand, boot.FrettingHand);
            LearnTuner = LearnMicTuner.Create(boot.transform, boot.Mic, Learn);
            LearnScript = DevLearnScript.Create(boot.transform, Learn);
            LearnScript.LiveTuner = LearnTuner;
        }

        void OnDestroy()
        {
            FlagStore.Changed -= OnFlagsChanged;
        }

        void Update()
        {
            if (State == null) return;
            DemoCommand command = DemoKeys.Poll();
            if (command != DemoCommand.None) Handle(command);
            if (State.Generation != _appliedGeneration) Enter();
            if (_pendingAlign)
            {
                _pendingAlign = false;   // started only now, after Enter() has put the rig in idle
                if (_boot.Aligner == null || !_boot.Aligner.Begin()) UGALog.Warn("demo_align", "the alignment could not start");
            }

            float dt = Time.deltaTime;
            if (State.Mode == DemoMode.Play) TickPlay(dt);
            else if (State.Mode == DemoMode.Idle && Hands != null) Hands.Idle(_idleChord, dt);
            // Learn: LearnPresenter, DevLearnScript and LearnMicTuner tick themselves; the presenter owns the fretting hand.
            UpdateChecksLabel();
            RefreshStatus();
        }

        void UpdateChecksLabel()
        {
            Hud.SimulatedBadge badge = _boot.Badge;
            if (badge == null) return;
            bool show = ShowScriptedChecks(_scriptedChecks, badge.IsShowing);
            if (_checksLabel == null)
            {
                if (!show) return;
                // Same spot, size and colour as the badge (a sibling under the head at the badge's local pose).
                _checksLabel = Hud.HudText.Create(badge.transform.parent, "SimulatedChecksLabel");
                _checksLabel.transform.localPosition = badge.transform.localPosition;
                _checksLabel.transform.localRotation = badge.transform.localRotation;
                _checksLabel.Color = new Color(1f, 0.757f, 0.027f, 1f);   // SimulatedBadge's amber (#FFC107)
                _checksLabel.SetText(ScriptedChecksText);
                float distance = badge.transform.localPosition.magnitude;
                FlagSet flags = FlagStore.Current;
                _checksLabel.SetAngularHeight(Hud.SimulatedBadge.Degrees * Hud.HudText.TextScale(flags != null && flags.largeText),
                                              distance > 0.01f ? distance : 1f);
            }
            if (_checksLabel.Visible != show) _checksLabel.Visible = show;
        }

        void Handle(DemoCommand command)
        {
            NeckAligner aligner = _boot.Aligner;
            if (command == DemoCommand.Idle && aligner != null && aligner.IsRunning) aligner.Cancel();   // Esc/I stops the glances
            DemoChange change = State.Apply(command);
            if (change == DemoChange.Align)
            {
                _pendingAlign = true;
            }
            else if (change == DemoChange.Reanchor)
            {
                if (aligner == null || !aligner.BeginReanchor())
                    UGALog.Warn("demo_align", "re-anchor did not start (align first with A, while the head is tracked)");
            }
            UGALog.Event("demo_key", "command", command.ToString(), "change", change.ToString(), "why", State.LastRefusal ?? "");
            if (change == DemoChange.Skip)
            {
                if (State.Mode == DemoMode.Learn && LearnScript != null) LearnScript.Skip();
                else if (State.Mode == DemoMode.Play && PlayAlong != null) PlayAlong.Skip();
            }
            else if (change == DemoChange.Recenter)
            {
                _boot.RecenterView();
            }
        }

        void Enter()
        {
            _appliedGeneration = State.Generation;
            FlagSet flags = FlagStore.Current;
            // Leave every mode first, then enter the one State asks for.
            if (LearnScript != null && LearnScript.Playing) LearnScript.Stop();
            if (PlayAlong != null && PlayAlong.Phase != PlayAlongPhase.Idle) PlayAlong.Stop();
            if (Play != null)
            {
                Play.HideResults();
                Play.Present(PracticeFrame.Idle, 0f);   // an inactive frame hides the neck HUD card and the rings
            }
            _practice = PracticeFrame.Idle;
            // The head-locked mic meter sits top right, exactly where the learn card and the play card go (12:35 player frame:
            // "MIC 48 kHz" on "next", its bars on the chord row). Learn has its own tuner needle and the spectator shows the
            // mic state, so the meter shows only while idle.
            if (_boot.Meter != null && _boot.Meter.gameObject.activeSelf != (State.Mode == DemoMode.Idle))
                _boot.Meter.gameObject.SetActive(State.Mode == DemoMode.Idle);
            if (_resultsDimmed) DimForResults(false);
            // Honesty: learn's pluck/strum verdicts and play's strums are scripted until Core's checks land (SL2/SL3), so a
            // "SIMULATED CHECKS" label shows even on a live mic. It is a separate label: SimulatedBadge stays a function of the
            // audioSource flag alone (its HudTests guard forbids any public way to change it), and its "SIMULATED INPUT" wins.
            _scriptedChecks = State.Mode == DemoMode.Learn ||
                              (State.Mode == DemoMode.Play && PlayAlong != null && PlayAlong.SimulateStrums);

            if (State.Mode == DemoMode.Learn) EnterLearn(flags);
            else if (State.Mode == DemoMode.Play) EnterPlay(flags);
            else EnterIdle(flags);
            UGALog.Event("demo_mode", "mode", State.Mode.ToString(), "item", ItemName(flags), "tempo", CurrentTempo());
        }

        void EnterIdle(FlagSet flags)
        {
            _idleChord = LearnChords.NameAt(flags, State.ChordIndex);
            ShowChordOnNeck(_idleChord);
            SetChordLabel(true, _idleChord);
        }

        void EnterLearn(FlagSet flags)
        {
            SetChordLabel(false, null);
            string name = LearnChords.NameAt(flags, State.ChordIndex);
            if (Hands != null) Hands.Idle(name, 0f);   // pose the chord and rest the strumming hand once; then learn owns the hand
            if (LearnScript == null)
            {
                UGALog.Warn("demo_learn", "learn mode is not available (no neck layout)");
                return;
            }
            LearnChord chord = LearnChords.Get(name);
            if (chord == null || !LearnScript.Play(chord)) UGALog.Warn("demo_learn", "could not start learn mode for " + name);
        }

        void EnterPlay(FlagSet flags)
        {
            SetChordLabel(false, null);   // the neck HUD card shows the chord in play mode
            if (PlayAlong == null || Play == null || _songIds.Count == 0)
            {
                UGALog.Warn("demo_play", "play mode is not available (no neck, or no songs in StreamingAssets/songs)");
                return;
            }
            string id = _songIds[State.SongIndex];
            Song song;
            string error;
            if (!CorePlayAlong.TryLoadSong(id, out song, out error))
            {
                UGALog.Warn("demo_play", id + ": " + error);
                return;
            }
            bool wait = flags == null || flags.waitModeDefault;
            if (!PlayAlong.Start(song, CurrentTempo(), wait, null))
            {
                UGALog.Warn("demo_play", id + ": Core's session refused the song");
                return;
            }
            Play.SetSong(song);
        }

        void TickPlay(float dt)
        {
            if (PlayAlong == null || Play == null) return;
            PlayAlong.Tick(dt);
            PlayAlong.FillFrame(ref _practice);
            Play.PresentPlayAlong(PlayAlong, dt);
            if (_practice.Chord != null) _lastPlayChord = _practice.Chord;
            if (PlayAlong.Phase == PlayAlongPhase.Ended)
            {
                // The results card sits above the neck where the hands are: fade both hands out and clear the dots until the
                // next mode change (hud-play-preview round 2: the card overlaps them otherwise).
                if (!_resultsDimmed) DimForResults(true);
                return;
            }
            if (Hands == null) return;
            if (_practice.Active) Hands.Apply(_practice, dt);
            else Hands.Idle(_lastPlayChord, dt);
        }

        void DimForResults(bool on)
        {
            _resultsDimmed = on;
            float fade = on ? 0f : 1f;
            if (_boot.PosedFrettingHand != null) _boot.PosedFrettingHand.SetFade(fade);
            if (StrummingHand != null) StrummingHand.SetFade(fade);   // Hands.Idle restores only the fretting hand's fade
            if (on && _boot.Neck != null) _boot.Neck.SetDots(_dots, 0);
        }

        void ShowChordOnNeck(string chord)
        {
            FloatingNeck neck = _boot.Neck;
            if (neck == null) return;
            int count = CoreNeckLayout.ChordMarks(CoreNeckLayout.DefaultGeometry, chord, _dots, _marks);
            if (count < 0) return;
            neck.SetDots(_dots, count);
            neck.SetStringMarks(_marks);
            if (_boot.FrettingHand != null)
                _boot.FrettingHand.SetFingertipTargets(Bootstrap.FingertipTargets(_dots, count, _targets), false);
        }

        void SetChordLabel(bool visible, string chord)
        {
            if (_boot.Chords == null) return;
            if (_boot.Chords.gameObject.activeSelf != visible) _boot.Chords.gameObject.SetActive(visible);
            if (visible) _boot.Chords.SetChords(chord, null);
        }

        void OnFlagsChanged(FlagSet flags)
        {
            ReadFlags(flags);
            State.SetCounts(_songIds.Count, ChordCount(flags), _tempos.Length);
            if (State.Mode == DemoMode.Idle) _appliedGeneration = -1;   // re-enter idle: the flags may name another chord
        }

        void ListSongs()
        {
            _songIds.Clear();
            string folder = Path.Combine(Application.streamingAssetsPath, CorePlayAlong.SongsFolderName);
            try
            {
                string[] files = Directory.GetFiles(folder, "*" + CorePlayAlong.SongFileSuffix);
                Array.Sort(files, StringComparer.Ordinal);
                foreach (string file in files)
                {
                    string name = Path.GetFileName(file);
                    if (name.EndsWith(CorePlayAlong.SongFileSuffix, StringComparison.Ordinal))
                        _songIds.Add(name.Substring(0, name.Length - CorePlayAlong.SongFileSuffix.Length));
                }
            }
            catch (Exception e)
            {
                UGALog.Warn("demo_songs", "could not list " + folder + ": " + e.Message);
            }
            int i = _songIds.IndexOf(CorePlayAlong.DefaultSongId);   // the default song first, so P plays it
            if (i > 0)
            {
                _songIds.RemoveAt(i);
                _songIds.Insert(0, CorePlayAlong.DefaultSongId);
            }
        }

        void ReadFlags(FlagSet flags)
        {
            float[] presets = flags != null ? flags.tempoPresets : null;
            int valid = 0;
            if (presets != null) foreach (float p in presets) if (p > 0f && !float.IsInfinity(p)) valid++;
            if (valid == 0)
            {
                _tempos = new[] { DefaultTempoPreset };
                return;
            }
            _tempos = new float[valid];
            int k = 0;
            foreach (float p in presets) if (p > 0f && !float.IsInfinity(p)) _tempos[k++] = p;
        }

        static int ChordCount(FlagSet flags)
        {
            return flags != null && flags.chords != null && flags.chords.Length > 0 ? flags.chords.Length : LearnChords.Names.Length;
        }

        float CurrentTempo()
        {
            return _tempos[Mathf.Clamp(State.TempoIndex, 0, _tempos.Length - 1)];
        }

        string ItemName(FlagSet flags)
        {
            if (State.Mode == DemoMode.Play) return _songIds.Count > 0 ? _songIds[State.SongIndex] : "";
            return LearnChords.NameAt(flags, State.ChordIndex);
        }

        void ApplyArguments(string[] args, FlagSet flags)
        {
            string mode = ArgumentValue(args, ModeArgument);
            string song = ArgumentValue(args, SongArgument);
            string chord = ArgumentValue(args, ChordArgument);
            string tempo = ArgumentValue(args, TempoArgument);
            if (mode == null && song == null && chord == null && tempo == null) return;

            int songIndex = State.SongIndex, chordIndex = State.ChordIndex, tempoIndex = State.TempoIndex;
            if (song != null)
            {
                int i = _songIds.IndexOf(song);
                if (i >= 0) songIndex = i;
                else UGALog.Warn("demo_args", "unknown song " + song);
            }
            if (chord != null)
            {
                for (int i = 0; i < State.ChordCount; i++)
                {
                    if (!string.Equals(LearnChords.NameAt(flags, i), chord, StringComparison.OrdinalIgnoreCase)) continue;
                    chordIndex = i;
                    break;
                }
            }
            float t;
            if (tempo != null && float.TryParse(tempo, NumberStyles.Float, CultureInfo.InvariantCulture, out t))
                tempoIndex = DemoState.NearestIndex(_tempos, t);
            State.SetIndices(songIndex, chordIndex, tempoIndex);

            DemoCommand command = DemoCommand.None;
            if (string.Equals(mode, "learn", StringComparison.OrdinalIgnoreCase)) command = DemoCommand.Learn;
            else if (string.Equals(mode, "play", StringComparison.OrdinalIgnoreCase)) command = DemoCommand.Play;
            else if (string.Equals(mode, "idle", StringComparison.OrdinalIgnoreCase)) command = DemoCommand.Idle;
            else if (mode != null) UGALog.Warn("demo_args", "unknown mode " + mode + " (idle, learn or play)");
            if (command != DemoCommand.None) State.Apply(command);
            UGALog.Event("demo_args", "mode", mode ?? "", "song", song ?? "", "chord", chord ?? "");
        }

        /// <summary>The value after <paramref name="name"/> in <paramref name="args"/>, or null. Pure.</summary>
        public static string ArgumentValue(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
            }
            return null;
        }

        void RefreshStatus()
        {
            int phase = 0;
            if (State.Mode == DemoMode.Play && PlayAlong != null) phase = (int)PlayAlong.Phase;
            else if (State.Mode == DemoMode.Learn && Learn != null) phase = (int)Learn.Stage;
            bool aligning = _boot.Aligner != null && _boot.Aligner.IsRunning;
            int key = ((_appliedGeneration * 31 + (int)State.Mode) * 31 + phase) * 2 + (aligning ? 1 : 0);
            if (key == _statusKey) return;
            _statusKey = key;
            FlagSet flags = FlagStore.Current;
            string what;
            if (aligning)
                what = "ALIGNING: the wearer glances at each point and holds still (Esc cancels)";
            else if (State.Mode == DemoMode.Play)
                what = "PLAY " + ItemName(flags) + " x" + CurrentTempo().ToString("0.##", CultureInfo.InvariantCulture) +
                       (PlayAlong != null ? " · " + PlayAlong.Phase : "");
            else if (State.Mode == DemoMode.Learn)
                what = "LEARN " + ItemName(flags) + (Learn != null ? " · " + Learn.Stage : "");
            else
                what = "IDLE " + _idleChord;
            _statusText = what + "    " + DemoKeys.Legend;
        }
    }
}
