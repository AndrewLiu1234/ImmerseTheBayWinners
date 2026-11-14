using System;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Runtime.Hud.Play
{
    /// <summary>What one slot of a strum or picking pattern asks for.</summary>
    public enum PatternSlotKind
    {
        /// <summary>"-": let the hand pass without touching the strings.</summary>
        Rest,
        /// <summary>"D": strum down (string 6 to string 1).</summary>
        Down,
        /// <summary>"U": strum up.</summary>
        Up,
        /// <summary>"B": pick the chord's bass string.</summary>
        Bass,
        /// <summary>"1".."6": pick that string (1 = high E).</summary>
        String,
        /// <summary>Anything else: shown as written.</summary>
        Other,
    }

    /// <summary>
    /// The strum or picking pattern for one bar: one cell per subdivision slot, with a cursor that moves in time.
    /// A fixed requirement of the product (docs/PRD.md requirement 3): it is never hidden while a pattern is set.
    ///
    /// Strum cells are arrows: down strokes point down, up strokes point up, a rest is a small dot (the hand keeps moving
    /// but misses the strings). Picking cells are string numbers; B, the chord's bass string, becomes that string's number
    /// with a short bar under it once <see cref="SetBassString"/> has been called. Direction is carried by the arrow's
    /// shape, never by colour.
    ///
    /// IN TIME WITH THE STRUMMING GHOST HAND. Both are driven by the same number: the slot position (0 = the first slot
    /// is due exactly now, 3.5 = half-way between the 4th and 5th). A stroke sounds at the START of its slot, so the cursor
    /// is centred on cell i exactly when the slot position equals i, which is the moment the hand hits the strings. There
    /// is no smoothing and no clock in here: the picture is a pure function of the number given to <see cref="SetCursor"/>
    /// and is drawn in the same frame, so it cannot lag the hand. Feed both from the same value in the same Update.
    ///
    /// The cell under the cursor swells and glows as the cursor arrives and relaxes after it (eased, peak exactly on the
    /// slot). Each cell lights once a bar, so nothing here flashes faster than 3 Hz. When the cursor leaves the last cell
    /// it fades out to the right while it fades in from the left, landing on the first cell exactly on the bar line.
    ///
    /// The object's origin is the left end of the strip, at the middle of the cell row (or, with <see cref="AnchorBottom"/>,
    /// at the strip's lowest ink, so the strip grows upward from a line it must not cross); cells run to the right, the
    /// cursor lane is underneath and clears the cells' lowest ink (arrows, digits and the bass bar, swollen). Main thread
    /// only. Faces +Z.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PatternStrip : MonoBehaviour
    {
        /// <summary>Most slots a pattern may have (16 = sixteenth notes in 4/4). Extra slots are ignored.</summary>
        public const int MaxSlots = 16;

        /// <summary>How much a cell grows when the cursor is exactly on it.</summary>
        public const float HitGain = 0.24f;

        /// <summary>Slots before a slot's moment in which its cell starts to glow.</summary>
        public const float GlowRise = 0.5f;

        /// <summary>Slots after a slot's moment in which its cell relaxes.</summary>
        public const float GlowFall = 1f;

        // The bar that marks the bass string sits this many digit cap heights below the digit's middle; its bottom is at
        // drop + half its height (0.08).
        const float BassBarDropOfCap = 0.82f;
        const float BassBarBottomOfCap = 0.9f;

        const float RestDotOfGlyph = 0.2f;
        const float TickOfGlyph = 0.1f;
        const float HaloOfGlyph = 1.7f;

        static readonly Color GlyphColor = new Color(0.702f, 0.898f, 0.988f, 1f);     // #B3E5FC
        static readonly Color HotColor = Color.white;
        static readonly Color HaloColor = new Color(0.310f, 0.765f, 0.969f, 1f);      // #4FC3F7, the ghost-hand blue
        static readonly Color CaretColor = Color.white;

        static readonly string[] s_Digits = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };

        sealed class Cell
        {
            public Transform Root;
            public HudShape Arrow;
            public HudShape Rest;
            public HudShape Halo;
            public HudShape Tick;
            public HudShape BassBar;
            public HudText Label;
            public PatternSlotKind Kind;
            public string Raw;
            public float ShownGlow = -1f;
        }

        readonly Cell[] _cells = new Cell[MaxSlots];
        HudShape _caret;
        HudShape _caretWrap;

        float _distanceM = PlayHudLayout.DesignDistanceM;
        bool _large;
        int _count;
        bool _picking;
        int _slotsPerBeat = 2;
        int _bassString;
        float _pitchM;
        float _glyphM;
        float _laneY;
        float _rowY;
        float _topM;
        float _bottomM;
        float _maxWidthDeg = PlayHudLayout.ColumnMaxWidthDeg;
        bool _anchorBottom;
        float _slotPosition;
        bool _hasCursor;

        /// <summary>Slots per beat (2 for the eighth-note patterns of all three songs). On-beat cells get a small mark in the cursor lane.</summary>
        public int SlotsPerBeat
        {
            get { return _slotsPerBeat; }
            set
            {
                int v = Mathf.Max(1, value);
                if (v == _slotsPerBeat) return;
                _slotsPerBeat = v;
                Rebuild();
            }
        }

        /// <summary>Number of slots in the pattern being shown.</summary>
        public int SlotCount { get { return _count; } }

        /// <summary>True when the pattern is a picking pattern.</summary>
        public bool Picking { get { return _picking; } }

        /// <summary>Width of the strip in metres (local): slots * pitch.</summary>
        public float WidthMeters { get { return _count * _pitchM; } }

        /// <summary>Widest the strip may be, degrees; the cell pitch is squeezed to fit (never below the minimum pitch).</summary>
        public float MaxWidthDeg
        {
            get { return _maxWidthDeg; }
            set
            {
                float v = value > 0f && !float.IsNaN(value) ? value : PlayHudLayout.ColumnMaxWidthDeg;
                if (Mathf.Abs(v - _maxWidthDeg) < 1e-4f) return;
                _maxWidthDeg = v;
                Rebuild();
            }
        }

        /// <summary>True: the origin is the strip's lowest ink (the bottom of the cursor lane), so it never reaches below it.</summary>
        public bool AnchorBottom
        {
            get { return _anchorBottom; }
            set
            {
                if (value == _anchorBottom) return;
                _anchorBottom = value;
                Rebuild();
            }
        }

        /// <summary>Highest ink of the strip (a cell at the peak of its swell), metres above the origin.</summary>
        public float TopMeters { get { return _topM; } }

        /// <summary>Lowest ink of the strip (the bottom of the cursor lane), metres relative to the origin (0 with AnchorBottom).</summary>
        public float BottomMeters { get { return _bottomM; } }

        /// <summary>Creates the strip under <paramref name="parent"/> (may be null) for a viewer <paramref name="viewingDistanceM"/> metres away.</summary>
        public static PatternStrip Create(Transform parent, float viewingDistanceM)
        {
            var go = new GameObject("PatternStrip");
            if (parent != null) go.transform.SetParent(parent, false);
            PatternStrip strip = go.AddComponent<PatternStrip>();
            strip.Init(viewingDistanceM);
            return strip;
        }

        void Init(float viewingDistanceM)
        {
            _distanceM = viewingDistanceM > 0f ? viewingDistanceM : PlayHudLayout.DesignDistanceM;
            _large = FlagStore.Current.largeText;

            for (int i = 0; i < MaxSlots; i++)
            {
                var cell = new Cell();
                var root = new GameObject("Slot" + i);
                root.transform.SetParent(transform, false);
                cell.Root = root.transform;
                cell.Halo = HudShape.Create(cell.Root, "Halo", HudShape.Halo);
                cell.Halo.Color = new Color(HaloColor.r, HaloColor.g, HaloColor.b, 0f);
                cell.Arrow = HudShape.Create(cell.Root, "Arrow", HudShape.Arrow);
                cell.Rest = HudShape.Create(cell.Root, "Rest", HudShape.Disc);
                cell.Tick = HudShape.Create(transform, "Beat" + i, HudShape.Disc);
                cell.Tick.SetColor(GlyphColor, 0.45f);
                root.SetActive(false);
                cell.Tick.Visible = false;
                _cells[i] = cell;
            }

            _caretWrap = HudShape.Create(transform, "CursorWrap", HudShape.Caret);
            _caret = HudShape.Create(transform, "Cursor", HudShape.Caret);
            _caret.Visible = false;
            _caretWrap.Visible = false;
        }

        // ------------------------------------------------------------------ the pattern

        /// <summary>
        /// Sets the pattern: one string per subdivision slot, straight from the song file. Strumming: "D", "U", "-".
        /// Picking (<paramref name="picking"/> true): "B" and "1".."6". Calling it again with the same pattern does
        /// nothing, so it may be called every frame. Null or empty hides the strip.
        /// </summary>
        public void SetPattern(string[] slots, bool picking)
        {
            int count = slots != null ? Mathf.Min(slots.Length, MaxSlots) : 0;
            bool same = count == _count && picking == _picking;
            for (int i = 0; same && i < count; i++)
                same = string.Equals(_cells[i].Raw ?? string.Empty, slots[i] ?? string.Empty, StringComparison.Ordinal);
            if (same) return;

            _count = count;
            _picking = picking;
            for (int i = 0; i < MaxSlots; i++)
            {
                Cell cell = _cells[i];
                cell.Raw = i < count ? (slots[i] ?? string.Empty) : null;
                cell.Kind = i < count ? Classify(cell.Raw, picking) : PatternSlotKind.Rest;
            }
            Rebuild();
        }

        /// <summary>
        /// The string number (4, 5 or 6) of the current chord's bass string, so "B" cells can show it; 0 shows the letter B.
        /// Call it when the chord changes.
        /// </summary>
        public void SetBassString(int stringNumber)
        {
            int v = stringNumber >= 1 && stringNumber <= 6 ? stringNumber : 0;
            if (v == _bassString) return;
            _bassString = v;
            for (int i = 0; i < _count; i++)
            {
                Cell cell = _cells[i];
                if (cell.Kind == PatternSlotKind.Bass && cell.Label != null) cell.Label.SetText(LabelFor(cell, _bassString));
            }
        }

        /// <summary>
        /// Moves the cursor. <paramref name="slotPosition"/> counts slots from the start of the pattern: whole numbers are
        /// the moments the slots are due, the fraction is the way to the next one. Any value is accepted and wrapped into
        /// the pattern. Applied at once (no smoothing), nothing allocated. Give the strumming ghost hand the same number in
        /// the same frame.
        /// </summary>
        public void SetCursor(float slotPosition)
        {
            if (float.IsNaN(slotPosition) || float.IsInfinity(slotPosition)) slotPosition = 0f;
            _slotPosition = slotPosition;
            _hasCursor = true;

            bool large = FlagStore.Current.largeText;
            if (large != _large)
            {
                _large = large;
                Rebuild();
                return;
            }
            ApplyCursor();
        }

        void Rebuild()
        {
            float scale = HudText.TextScale(_large);
            float pitchDeg = PlayHudLayout.PatternPitch(_count, PlayHudLayout.PatternPitchDeg * scale,
                _maxWidthDeg, PlayHudLayout.PatternMinPitchDeg);
            float squeeze = pitchDeg / (PlayHudLayout.PatternPitchDeg * scale);
            _pitchM = PlayHudLayout.Size(pitchDeg, _distanceM);
            _glyphM = PlayHudLayout.Size(PlayHudLayout.PatternGlyphDeg * scale * squeeze, _distanceM);
            float caretM = PlayHudLayout.Size(PlayHudLayout.PatternCaretDeg * scale * squeeze, _distanceM);
            float digitDeg = Mathf.Max(PlayHudLayout.PatternDigitCapDeg * scale * squeeze, HudText.MinTextDegrees);
            float digitM = PlayHudLayout.Size(digitDeg, _distanceM);

            // The lane clears the cells' lowest ink at the peak of their swell: arrows, digits and the bass bar under a digit.
            bool anyText = false, anyBass = false;
            for (int i = 0; i < _count; i++)
            {
                PatternSlotKind k = _cells[i].Kind;
                if (k == PatternSlotKind.Bass) anyBass = true;
                if (k != PatternSlotKind.Down && k != PatternSlotKind.Up && k != PatternSlotKind.Rest) anyText = true;
            }
            float laneDropDeg, rowAboveBottomDeg, topAboveBottomDeg;
            ExtentDeg(_count, anyText, anyBass, scale, _maxWidthDeg, out laneDropDeg, out rowAboveBottomDeg, out topAboveBottomDeg);
            _laneY = -PlayHudLayout.Size(laneDropDeg, _distanceM);
            float rowAboveBottom = PlayHudLayout.Size(rowAboveBottomDeg, _distanceM);
            _rowY = _anchorBottom ? rowAboveBottom : 0f;
            _bottomM = _rowY - rowAboveBottom;
            _topM = _bottomM + PlayHudLayout.Size(topAboveBottomDeg, _distanceM);

            for (int i = 0; i < MaxSlots; i++)
            {
                Cell cell = _cells[i];
                bool used = i < _count;
                if (cell.Root.gameObject.activeSelf != used) cell.Root.gameObject.SetActive(used);
                cell.ShownGlow = -1f;
                if (!used)
                {
                    cell.Tick.Visible = false;
                    continue;
                }

                float x = CellCenter(i, _pitchM);
                cell.Root.localPosition = new Vector3(x, _rowY, 0f);
                cell.Root.localScale = Vector3.one;

                bool arrow = cell.Kind == PatternSlotKind.Down || cell.Kind == PatternSlotKind.Up;
                bool rest = cell.Kind == PatternSlotKind.Rest;
                bool text = !arrow && !rest;

                cell.Arrow.Visible = arrow;
                if (arrow)
                {
                    cell.Arrow.SetSize(_glyphM);
                    cell.Arrow.SetRoll(cell.Kind == PatternSlotKind.Down ? 180f : 0f);
                }

                cell.Rest.Visible = rest;
                if (rest)
                {
                    cell.Rest.SetSize(_glyphM * RestDotOfGlyph);
                    cell.Rest.SetColor(GlyphColor, 0.5f);
                }

                cell.Halo.SetSize(_glyphM * HaloOfGlyph);

                if (text)
                {
                    if (cell.Label == null)
                    {
                        cell.Label = HudText.Create(cell.Root, "Label");
                        cell.Label.Color = GlyphColor;
                    }
                    cell.Label.Visible = true;
                    cell.Label.SetText(LabelFor(cell, _bassString));
                    cell.Label.SetAngularHeight(digitDeg, WorldDistance());
                }
                else if (cell.Label != null)
                {
                    cell.Label.Visible = false;
                }

                bool bass = cell.Kind == PatternSlotKind.Bass;
                if (bass && cell.BassBar == null)
                {
                    cell.BassBar = HudShape.CreateBar(cell.Root, "BassBar", digitM * 0.8f, digitM * 0.16f);
                    cell.BassBar.SetPosition(0f, -digitM * BassBarDropOfCap);
                    cell.BassBar.SetColor(GlyphColor, 0.9f);
                }
                if (cell.BassBar != null) cell.BassBar.Visible = bass;

                bool onBeat = i % _slotsPerBeat == 0;
                cell.Tick.Visible = onBeat;
                if (onBeat)
                {
                    cell.Tick.SetPosition(x, _rowY + _laneY);
                    cell.Tick.SetSize(_glyphM * TickOfGlyph * (i == 0 ? 1.6f : 1f));
                }
            }

            _caret.SetSize(caretM * 1.1f, caretM);
            _caretWrap.SetSize(caretM * 1.1f, caretM);
            ApplyCursor();
        }

        void ApplyCursor()
        {
            bool show = _hasCursor && _count > 0;
            if (!show)
            {
                _caret.Visible = false;
                _caretWrap.Visible = false;
                return;
            }

            float u = WrapSlot(_slotPosition, _count);
            float wrap = WrapBlend(u, _count);

            _caret.Visible = true;
            _caret.SetPosition(CursorX(u, _pitchM), _rowY + _laneY);
            _caret.SetColor(CaretColor, 1f - wrap);

            bool wrapping = wrap > 0f;
            _caretWrap.Visible = wrapping;
            if (wrapping)
            {
                _caretWrap.SetPosition(CursorX(u - _count, _pitchM), _rowY + _laneY);
                _caretWrap.SetColor(CaretColor, wrap);
            }

            for (int i = 0; i < _count; i++)
            {
                Cell cell = _cells[i];
                float glow = Glow(u, i, _count);
                if (Mathf.Abs(glow - cell.ShownGlow) < 0.004f) continue;
                cell.ShownGlow = glow;

                if (cell.Kind == PatternSlotKind.Rest)
                {
                    // A rest is not played: the cursor passes, the dot only brightens a little.
                    cell.Rest.SetColor(GlyphColor, 0.5f + 0.3f * glow);
                    cell.Halo.Visible = false;
                    continue;
                }

                float s = 1f + HitGain * glow;
                cell.Root.localScale = new Vector3(s, s, 1f);
                cell.Halo.Visible = glow > 0.01f;
                cell.Halo.SetColor(HaloColor, 0.6f * glow);
                if (cell.Kind == PatternSlotKind.Down || cell.Kind == PatternSlotKind.Up)
                    cell.Arrow.SetColor(Color.Lerp(GlyphColor, HotColor, glow), 0.82f + 0.18f * glow);
            }
        }

        float WorldDistance()
        {
            float s = transform.lossyScale.y;
            return _distanceM * (s > 1e-6f ? s : 1f);
        }

        static string LabelFor(Cell cell, int bassString)
        {
            if (cell.Kind == PatternSlotKind.Bass) return bassString > 0 ? s_Digits[bassString] : "B";
            if (cell.Kind == PatternSlotKind.String) return s_Digits[StringNumber(cell.Raw)];
            return cell.Raw ?? string.Empty;
        }

        // ------------------------------------------------------------------ pure helpers (unit-tested)

        /// <summary>
        /// What a slot string means. "D"/"U"/"-" (any case, spaces ignored) are a down stroke, an up stroke and a rest in
        /// both kinds of pattern; an empty slot is a rest; "B" and "1".."6" are picking slots.
        /// </summary>
        public static PatternSlotKind Classify(string slot, bool picking)
        {
            if (slot == null) return PatternSlotKind.Rest;
            string s = slot.Trim();
            if (s.Length == 0 || s == "-" || s == "." || s == "x" || s == "X") return PatternSlotKind.Rest;
            if (s.Length == 1)
            {
                char c = char.ToUpperInvariant(s[0]);
                if (c == 'B' && picking) return PatternSlotKind.Bass;
                if (c >= '1' && c <= '6') return PatternSlotKind.String;
                if (c == 'D') return PatternSlotKind.Down;
                if (c == 'U') return PatternSlotKind.Up;
            }
            return PatternSlotKind.Other;
        }

        /// <summary>
        /// The strip's vertical extent in degrees, for <paramref name="slots"/> cells at a text scale, squeezed into
        /// <paramref name="maxWidthDeg"/>: how far the cursor lane's centre is below the cell row
        /// (<paramref name="laneDropDeg"/>), how far the cell row is above the strip's lowest ink (the bottom of the lane,
        /// <paramref name="rowAboveBottomDeg"/>) and how far its highest ink (a cell at the peak of its swell) is above that
        /// (<paramref name="topAboveBottomDeg"/>). Text cells (digits) and the bar under a bass digit reach lower than arrows.
        /// </summary>
        public static void ExtentDeg(int slots, bool anyText, bool anyBass, float textScale, float maxWidthDeg,
            out float laneDropDeg, out float rowAboveBottomDeg, out float topAboveBottomDeg)
        {
            float scale = textScale > 0f && !float.IsNaN(textScale) ? textScale : 1f;
            float pitchDeg = PlayHudLayout.PatternPitch(slots, PlayHudLayout.PatternPitchDeg * scale, maxWidthDeg, PlayHudLayout.PatternMinPitchDeg);
            float squeeze = pitchDeg / (PlayHudLayout.PatternPitchDeg * scale);
            float glyph = PlayHudLayout.PatternGlyphDeg * scale * squeeze;
            float caret = PlayHudLayout.PatternCaretDeg * scale * squeeze;
            float digit = Mathf.Max(PlayHudLayout.PatternDigitCapDeg * scale * squeeze, HudText.MinTextDegrees);
            float swell = 1f + HitGain;
            float cellTop = glyph * 0.5f * swell;
            float cellBottom = cellTop;
            if (anyText)
            {
                cellTop = Mathf.Max(cellTop, digit * 0.5f * swell);
                cellBottom = Mathf.Max(cellBottom, digit * (anyBass ? BassBarBottomOfCap : 0.5f) * swell);
            }
            laneDropDeg = Mathf.Max(PlayHudLayout.PatternLaneDropDeg * scale * squeeze,
                cellBottom + PlayHudLayout.PatternLaneGapDeg * scale * squeeze + caret * 0.5f);
            rowAboveBottomDeg = laneDropDeg + caret * 0.5f;
            topAboveBottomDeg = rowAboveBottomDeg + cellTop;
        }

        /// <summary>The string number 1..6 of a picking slot, or 0 if the slot is not one.</summary>
        public static int StringNumber(string slot)
        {
            if (slot == null) return 0;
            string s = slot.Trim();
            if (s.Length != 1) return 0;
            char c = s[0];
            return c >= '1' && c <= '6' ? c - '0' : 0;
        }

        /// <summary>Wraps a slot position into the pattern: a value in [0, slotCount).</summary>
        public static float WrapSlot(float slotPosition, int slotCount)
        {
            return slotCount > 0 ? PlayHudLayout.PositiveMod(slotPosition, slotCount) : 0f;
        }

        /// <summary>X of the centre of cell <paramref name="index"/>: (index + 0.5) * pitch from the strip's left end.</summary>
        public static float CellCenter(int index, float pitch)
        {
            return (index + 0.5f) * pitch;
        }

        /// <summary>
        /// X of the cursor for a (wrapped) slot position: linear in time, and exactly on the centre of cell i when the
        /// position is i, the moment that slot's stroke is due.
        /// </summary>
        public static float CursorX(float wrappedSlot, float pitch)
        {
            return (wrappedSlot + 0.5f) * pitch;
        }

        /// <summary>
        /// 0 while the cursor is inside the pattern; during the last slot it eases from 0 to 1, the share of the cursor
        /// that is already drawn coming in from the left (one pattern-length earlier).
        /// </summary>
        public static float WrapBlend(float wrappedSlot, int slotCount)
        {
            if (slotCount <= 0) return 0f;
            return PlayHudLayout.Ease01(wrappedSlot - (slotCount - 1));
        }

        /// <summary>
        /// How strongly cell <paramref name="index"/> is lit, 0..1, for a slot position: it rises over the half slot before
        /// the slot's moment, is exactly 1 at that moment, and relaxes over the slot after it. Wraps round the pattern, so
        /// the first cell already rises at the end of the bar.
        /// </summary>
        public static float Glow(float slotPosition, int index, int slotCount)
        {
            if (slotCount <= 0) return 0f;
            float d = PlayHudLayout.PositiveMod(slotPosition - index, slotCount);   // slots since this cell's moment
            if (d > slotCount * 0.5f) d -= slotCount;                               // ...or (negative) until the next one
            if (d < 0f)
            {
                return d <= -GlowRise ? 0f : PlayHudLayout.Ease01(1f + d / GlowRise);
            }
            if (d >= GlowFall) return 0f;
            float t = 1f - d / GlowFall;
            return t * t;
        }

        /// <summary>Index of the slot that is due or was last due at a slot position (the floor, wrapped).</summary>
        public static int SlotIndex(float slotPosition, int slotCount)
        {
            if (slotCount <= 0) return 0;
            return Mathf.Min(slotCount - 1, (int)WrapSlot(slotPosition, slotCount));
        }
    }
}
