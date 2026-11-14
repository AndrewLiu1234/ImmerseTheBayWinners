// LearnNeckColumn: the one-mark-per-string rule between the floating neck's X / O column and the Learn HUD's per-string
// marks. Pure maths and data (no scene access), unit-tested.
//
// THE COLLISION IT REMOVES. The neck draws its X / O marks in a row beyond the nut (NeckMeshBuilder: one slot per string, in
// string order, facing the eye) and the Learn HUD's pluck-check marks were drawn in their own row 30 mm beyond the nut: two
// rows of six symbols on the same spot, so a string could show an O and a check at once, overlapping. During the pluck
// check the learn marks now stand IN the neck's column, in the very slots the X / O use (computed here with the neck's own
// NeckMeshBuilder.MarkFrame and MarkSlotOffset, so they follow any change to the neck's row), and the neck shows only the
// X marks that no verdict has replaced. After the check the chord's X / O come back.
using UnityEngine;
using UntitledGuitarApp.Runtime.Neck;
using LearnMark = UntitledGuitarApp.Runtime.Hud.Learn.StringMark;
using NeckMark = UntitledGuitarApp.Runtime.Neck.StringMark;

namespace UntitledGuitarApp.Runtime.Practice.Learn
{
    public static class LearnNeckColumn
    {
        /// <summary>
        /// The centre of each string's X / O slot, in the neck transform's local metres (index 0 = string 1), plus the two
        /// in-plane directions the neck draws a mark on (<paramref name="along"/> the neck as seen, <paramref name="across"/>
        /// toward string 1 as seen; unit vectors in the same local space). <paramref name="sizes"/> are the neck's
        /// (FloatingNeck.Sizes), <paramref name="stringHeightMm"/> its layout's string height, <paramref name="viewerMm"/>
        /// the eye in neck-local millimetres (as FloatingNeck computes it for its own marks). Returns false when
        /// <paramref name="result"/> is too short.
        /// </summary>
        public static bool SlotCentres(NeckSizes sizes, float stringHeightMm, bool hasViewer, Vector3 viewerMm,
            Vector3[] result, out Vector3 along, out Vector3 across)
        {
            along = Vector3.right;
            across = Vector3.forward;
            if (result == null || result.Length < LearnChord.StringCount) return false;

            Vector3 row = new Vector3(sizes.markRowXMm, stringHeightMm, 0f);
            NeckBuildOptions options = default(NeckBuildOptions);
            options.hasViewer = hasViewer;
            options.viewerMm = viewerMm;
            Vector3 view;
            NeckMeshBuilder.MarkFrame(options, row, out view, out along, out across);
            for (int s = 1; s <= LearnChord.StringCount; s++)
            {
                Vector3 mm = row + across * (NeckMeshBuilder.MarkSlotOffset(s) * sizes.markPitchMm);
                result[s - 1] = mm * NeckMath.MetresPerMm;
            }
            return true;
        }

        /// <summary>The chord's own X / O marks (index = string number - 1): O for an open string, X for one not played.</summary>
        public static void ChordMarks(LearnChord chord, NeckMark[] result)
        {
            for (int i = 0; i < result.Length; i++)
            {
                int s = i + 1;
                if (chord == null || s > LearnChord.StringCount) { result[i] = NeckMark.None; continue; }
                int fret = chord.Fret(s);
                result[i] = fret < 0 ? NeckMark.Muted : fret == 0 ? NeckMark.Open : NeckMark.None;
            }
        }

        /// <summary>
        /// What the neck's column shows while the learn marks stand in it: only the chord's X marks on strings that have no
        /// verdict (a string that is not played keeps its "do not play" X until a verdict, for example "wrong: you played
        /// it", takes its slot). O marks give their slots to the verdicts. So every string shows at most one mark.
        /// </summary>
        public static void ColumnMarksDuringPluck(LearnChord chord, LearnMark[] verdicts, NeckMark[] result)
        {
            ChordMarks(chord, result);
            for (int i = 0; i < result.Length; i++)
            {
                bool verdict = verdicts != null && i < verdicts.Length && verdicts[i] != LearnMark.None;
                if (result[i] != NeckMark.Muted || verdict) result[i] = NeckMark.None;
            }
        }

        /// <summary>
        /// Fingertip targets for the capsule hand from the neck's dots: finger 1-4 to the dot's (x, z) in neck-local metres
        /// on the fretboard plane; a finger with no dot gets a NaN x ("not used"), as Bootstrap.FingertipTargets does.
        /// </summary>
        public static Vector3[] FingertipTargets(FingertipDot[] dots, int count, Vector3[] result)
        {
            for (int f = 0; f < result.Length; f++) result[f] = new Vector3(float.NaN, 0f, 0f);
            for (int i = 0; dots != null && i < count && i < dots.Length; i++)
            {
                int finger = dots[i].finger;
                if (finger < 1 || finger > result.Length) continue;
                result[finger - 1] = new Vector3(dots[i].xMm * NeckMath.MetresPerMm, 0f, dots[i].zMm * NeckMath.MetresPerMm);
            }
            return result;
        }
    }
}
