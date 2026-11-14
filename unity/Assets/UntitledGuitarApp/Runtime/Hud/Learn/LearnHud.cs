using UnityEngine;

namespace UntitledGuitarApp.Runtime.Hud.Learn
{
    /// <summary>
    /// The whole Learn HUD in one object, so an adapter needs one line per event: the tuner (under the head), the
    /// per-string marks (on the neck) and ONE message at a time on the neck card spot: a tip, a strum verdict or a
    /// finger prompt. A beginner cannot read three things at once, so showing one message removes the other two, and
    /// the spot changes at most 3 times a second however fast the requests come.
    ///
    /// Presentation only: it takes plain values and decides nothing about pitch or verdicts (that is Core).
    ///
    /// Typical use from Bootstrap:
    ///   var hud = LearnHud.Create(null, stereoRig.Head, neckSocket);
    ///   hud.SetNeck(neckSocket, nutEndsInNeckFrame, nutToFret5Metres);
    ///   hud.ShowPluck(2, StringMark.Muted, "Arch finger 3, B string muted", TipIcon.Arch);
    ///
    /// The card and the marks follow the neck socket in world space and face the head; they are not parented under the
    /// socket (a mirrored or scaled socket must not mirror text). Keep this object under an unscaled parent. Without a
    /// neck socket the message floats under the head instead (the headLocked fallback). Runs late in the frame so it
    /// uses this frame's head and neck poses. Main thread only.
    /// </summary>
    [DefaultExecutionOrder(20000)]
    [DisallowMultipleComponent]
    public sealed class LearnHud : MonoBehaviour
    {
        enum Slot
        {
            None = 0,
            Tip = 1,
            FingerStep = 2,
            Strum = 3,
        }

        const float ResizeFraction = 0.08f;

        static readonly Vector3[] s_NutCentre = { Vector3.zero };   // used when no string positions are given

        bool _built;
        Transform _head;
        Transform _socket;
        Transform _card;
        Vector3 _cardLocal = LearnHudLayout.NeckCardLocal(LearnHudLayout.DefaultScaleLengthM);
        float _cardDistance;

        ChangeGate _slotGate;
        Slot _pending;
        string _pTip = string.Empty;
        TipIcon _pIcon;
        int _pFinger, _pString, _pFret, _pStep, _pStepCount;
        bool _pClean;
        string _pIssue;

        /// <summary>The tuner (parented under the head).</summary>
        public TunerNeedle Tuner { get; private set; }

        /// <summary>The per-string marks at the nut.</summary>
        public StringMarks Marks { get; private set; }

        /// <summary>The tip line. Prefer <see cref="ShowTip"/>, which keeps the one-message rule.</summary>
        public TipLine Tip { get; private set; }

        /// <summary>The strum verdict. Prefer <see cref="ShowStrum(bool, string)"/>.</summary>
        public StrumVerdict Strum { get; private set; }

        /// <summary>The finger-by-finger prompt. Prefer <see cref="ShowFingerStep"/>.</summary>
        public FingerStepPrompt FingerStep { get; private set; }

        /// <summary>The object on the neck card spot that holds the message (for placing other things relative to it).</summary>
        public Transform Card { get { EnsureBuilt(); return _card; } }

        /// <summary>
        /// The wearer's head (StereoRig's Head). Everything faces it and is sized for its distance. The tuner is parented
        /// under it; setting null (do it before destroying a head) brings the tuner back under this object.
        /// </summary>
        public Transform Head
        {
            get { return _head; }
            set
            {
                EnsureBuilt();
                _head = value;
                Marks.Head = value;
                if (Tuner == null) return;
                Tuner.transform.SetParent(value != null ? value : transform, false);
                Tuner.transform.localPosition = value != null ? LearnHudLayout.TunerHeadLocal() : Vector3.zero;
                Tuner.transform.localRotation = Quaternion.identity;
                if (value != null) Tuner.SetViewingDistance(LearnHudLayout.TunerDistanceM);
            }
        }

        /// <summary>Where the message sits in the neck frame, metres. Default: above fret 5 of a 648 mm scale, 8 cm out of the fretboard.</summary>
        public Vector3 CardLocal
        {
            get { return _cardLocal; }
            set { _cardLocal = value; }
        }

        /// <summary>
        /// Creates the Learn HUD under <paramref name="parent"/> (may be null; must be unscaled). <paramref name="head"/> is
        /// StereoRig's Head; <paramref name="neckSocket"/> is the pinned neck frame. Either may be null and set later.
        /// </summary>
        public static LearnHud Create(Transform parent, Transform head, Transform neckSocket)
        {
            var go = new GameObject("LearnHud");
            if (parent != null) go.transform.SetParent(parent, false);
            LearnHud hud = go.AddComponent<LearnHud>();
            hud.EnsureBuilt();
            hud.Head = head;
            if (neckSocket != null) hud.SetNeck(neckSocket, null, 0f);
            return hud;
        }

        void OnDestroy()
        {
            // The tuner lives under the head, outside this object, so it has to be removed by hand.
            if (Tuner != null && Application.isPlaying) Destroy(Tuner.gameObject);
        }

        void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            var card = new GameObject("NeckCard");
            card.transform.SetParent(transform, false);
            _card = card.transform;

            float d = LearnWidget.DefaultViewingDistanceM;
            Tip = TipLine.Create(_card, d);
            Strum = StrumVerdict.Create(_card, d);
            FingerStep = FingerStepPrompt.Create(_card, d);
            Marks = StringMarks.Create(transform, 0f);
            Tuner = TunerNeedle.Create(transform, LearnHudLayout.TunerDistanceM);
            _cardDistance = d;
        }

        /// <summary>
        /// Attaches the HUD to the neck: the socket (the pinned neck frame), the nut end of each string in that frame
        /// (metres; index 0 = string 1 ... 5 = string 6; null = a nut centred on the socket's origin) and the distance from
        /// the nut to fret 5 in metres (0 keeps the default for a 648 mm scale). A null socket detaches the HUD.
        /// </summary>
        public void SetNeck(Transform neckSocket, Vector3[] nutEndLocal, float nutToCardFretM)
        {
            EnsureBuilt();
            _socket = neckSocket;
            if (nutToCardFretM > 0f) _cardLocal = LearnHudLayout.NeckCardLocal(nutToCardFretM, LearnHudLayout.CardHeightM);
            Marks.SetStringAnchors(neckSocket, nutEndLocal != null && nutEndLocal.Length > 0 ? nutEndLocal : s_NutCentre);
            PlaceCard();
        }

        // ------------------------------------------------------------------ one message at a time

        /// <summary>Shows a tip (icon first, 7 words or fewer) and removes any other message. Safe to call every frame.</summary>
        public void ShowTip(string tip, TipIcon icon)
        {
            EnsureBuilt();
            string t = tip ?? string.Empty;
            if (_pending == Slot.Tip && icon == _pIcon && !HudText.NeedsRebuild(_pTip, t)) return;
            _pending = Slot.Tip;
            _pTip = t;
            _pIcon = icon;
            Request();
        }

        /// <summary>Shows one step of the finger-by-finger build and removes any other message. Safe to call every frame.</summary>
        public void ShowFingerStep(int finger, int stringNumber, int fret, int step, int stepCount)
        {
            EnsureBuilt();
            if (_pending == Slot.FingerStep && finger == _pFinger && stringNumber == _pString && fret == _pFret
                && step == _pStep && stepCount == _pStepCount) return;
            _pending = Slot.FingerStep;
            _pFinger = finger;
            _pString = stringNumber;
            _pFret = fret;
            _pStep = step;
            _pStepCount = stepCount;
            Request();
        }

        /// <summary>Shows the verdict on one strum (a new strum every call) and removes any other message.</summary>
        public void ShowStrum(bool clean, string issue)
        {
            ShowStrum(clean, issue, clean ? TipIcon.Good : TipIcon.Mismatch);
        }

        /// <summary>As <see cref="ShowStrum(bool, string)"/>, choosing the icon drawn before the issue.</summary>
        public void ShowStrum(bool clean, string issue, TipIcon icon)
        {
            EnsureBuilt();
            _pending = Slot.Strum;
            _pClean = clean;
            _pIssue = issue;
            _pIcon = icon;
            Request();
        }

        /// <summary>
        /// The result of one plucked string: the mark on that string (where), plus one tip (what to do). A null or empty
        /// tip leaves the current message as it is, so a passed string does not wipe the tip for another string.
        /// </summary>
        public void ShowPluck(int stringNumber, StringMark mark, string tip, TipIcon icon)
        {
            EnsureBuilt();
            Marks.Show(stringNumber, mark);
            if (!string.IsNullOrEmpty(tip)) ShowTip(tip, icon);
        }

        /// <summary>Fades the message out (the marks and the tuner are left alone).</summary>
        public void ClearMessage()
        {
            EnsureBuilt();
            if (_pending == Slot.None) return;
            _pending = Slot.None;
            Request();
        }

        /// <summary>Fades everything out: message, marks and tuner.</summary>
        public void ClearAll()
        {
            ClearMessage();
            Marks.ClearAll();
            if (Tuner != null) Tuner.Clear();   // the tuner lives under the head and goes if the head is destroyed first
        }

        void Request()
        {
            _slotGate.Request();
            if (_slotGate.Tick(0f)) Apply();   // passes at once unless the spot changed less than 0.34 s ago
        }

        void Apply()
        {
            switch (_pending)
            {
                case Slot.Tip:
                    Strum.HideNow();
                    FingerStep.HideNow();
                    Tip.Show(_pTip, _pIcon);
                    break;
                case Slot.FingerStep:
                    Tip.HideNow();
                    Strum.HideNow();
                    FingerStep.Show(_pFinger, _pString, _pFret, _pStep, _pStepCount);
                    break;
                case Slot.Strum:
                    Tip.HideNow();
                    FingerStep.HideNow();
                    Strum.Show(_pClean, _pIssue, _pIcon);
                    break;
                default:
                    Tip.Hide();
                    FingerStep.Hide();
                    Strum.Hide();
                    break;
            }
        }

        void Update()
        {
            if (!_built) return;
            if (_slotGate.Tick(Time.unscaledDeltaTime)) Apply();
        }

        void LateUpdate()
        {
            if (_built) PlaceCard();
        }

        void SetAlign(float alignX)
        {
            Tip.AlignX = alignX;
            Strum.AlignX = alignX;
            FingerStep.AlignX = alignX;
        }

        /// <summary>Moves the message onto the neck card spot and turns it to face the eyes. Called every frame; also usable by previews.</summary>
        public void PlaceCard()
        {
            EnsureBuilt();
            Transform head = _head;
            float distance;
            if (_socket != null)
            {
                Vector3 p = _socket.TransformPoint(_cardLocal);
                Vector3 eye = head != null ? head.position : _socket.TransformPoint(LearnHudLayout.TypicalEyeInNeckFrame);
                Vector3 upHint = head != null ? head.up : _socket.TransformVector(LearnHudLayout.TypicalUpInNeckFrame);
                distance = (p - eye).magnitude;
                Vector3 forward, up;
                if (LearnHudLayout.TryFacing(eye, p, upHint, out forward, out up))
                {
                    // The message starts a fixed lead before the card spot and grows away from the neck.
                    Vector3 right = Vector3.Cross(up, forward);
                    float away = LearnHudLayout.AwayFromNeckSign(right, _socket.TransformVector(Vector3.up));
                    float lead = HudText.SizeForAngularHeight(LearnHudLayout.MessageLeadDegrees, distance);
                    _card.SetPositionAndRotation(LearnHudLayout.MessageOrigin(p, right, away, lead), Quaternion.LookRotation(forward, up));
                    SetAlign(away);
                }
                else
                {
                    _card.position = p;
                    SetAlign(0f);
                }
            }
            else if (head != null)
            {
                _card.SetPositionAndRotation(head.TransformPoint(LearnHudLayout.FloatingMessageHeadLocal()), head.rotation);
                distance = LearnHudLayout.FloatingMessageDistanceM;
                SetAlign(0f);
            }
            else
            {
                return;
            }

            if (LearnHudLayout.DistanceChanged(_cardDistance, distance, ResizeFraction))
            {
                _cardDistance = distance;
                Tip.SetViewingDistance(distance);
                Strum.SetViewingDistance(distance);
                FingerStep.SetViewingDistance(distance);
            }
        }
    }
}
