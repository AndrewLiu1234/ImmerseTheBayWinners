using System;
using System.Collections.Generic;

namespace UntitledGuitarApp.Core.Theory
{
    /// <summary>Measured neck dimensions. Replace the defaults with the demo guitar's measurements (Q-REG-06).</summary>
    public sealed class FretboardConfig
    {
        /// <summary>Nut → saddle, mm. 648 mm (25.5") is a common steel-string scale (Appendix B4).</summary>
        public double ScaleLengthMm { get; }
        /// <summary>Centre of string 6 → centre of string 1 at the nut, mm. ≈35 mm on a 43 mm nut (Appendix B4).</summary>
        public double NutStringSpanMm { get; }
        /// <summary>Same span at the saddle, mm. Appendix B4 gives ≈52–55 mm; 53.5 mm is the midpoint.</summary>
        public double SaddleStringSpanMm { get; }
        /// <summary>Fingertip lands this fraction of the way from fret n−1 to fret n (Appendix B4: ≈75%).</summary>
        public double FingertipFraction { get; }

        public FretboardConfig(double scaleLengthMm, double nutStringSpanMm, double saddleStringSpanMm, double fingertipFraction = 0.75)
        {
            if (!(scaleLengthMm > 0)) throw new ArgumentOutOfRangeException(nameof(scaleLengthMm));
            if (!(nutStringSpanMm > 0)) throw new ArgumentOutOfRangeException(nameof(nutStringSpanMm));
            if (!(saddleStringSpanMm > 0)) throw new ArgumentOutOfRangeException(nameof(saddleStringSpanMm));
            if (!(fingertipFraction > 0 && fingertipFraction <= 1)) throw new ArgumentOutOfRangeException(nameof(fingertipFraction));
            ScaleLengthMm = scaleLengthMm;
            NutStringSpanMm = nutStringSpanMm;
            SaddleStringSpanMm = saddleStringSpanMm;
            FingertipFraction = fingertipFraction;
        }

        public static readonly FretboardConfig Default = new FretboardConfig(648.0, 35.0, 53.5);
    }

    /// <summary>A point on the fretboard plane in neck-local millimetres.</summary>
    public readonly struct NeckPoint
    {
        /// <summary>Distance from the nut toward the bridge, mm.</summary>
        public readonly double AlongMm;
        /// <summary>Offset from the neck centreline, mm; positive toward string 6 (low E).</summary>
        public readonly double AcrossMm;

        public NeckPoint(double alongMm, double acrossMm) { AlongMm = alongMm; AcrossMm = acrossMm; }
    }

    /// <summary>A fingertip dot: where finger <see cref="Finger"/> presses string <see cref="String"/> at <see cref="Fret"/>.</summary>
    public readonly struct FingertipDot
    {
        public readonly int String;
        public readonly int Fret;
        public readonly int Finger;
        public readonly NeckPoint Position;

        public FingertipDot(int stringNumber, int fret, int finger, NeckPoint position)
        { String = stringNumber; Fret = fret; Finger = finger; Position = position; }
    }

    /// <summary>
    /// Fretboard positions in neck-local mm (MASTER_PLAN Appendix B4). The fingertip dots are the source of truth for the
    /// ghost hand's finger positions (GHOST_HANDS.md). Frame: origin at the nut on the centreline; +Along toward the bridge;
    /// +Across toward string 6. Runtime maps this to Unity space (left-handed mirroring is Runtime's job).
    /// </summary>
    public sealed class FretboardGeometry
    {
        public FretboardConfig Config { get; }

        public FretboardGeometry(FretboardConfig config) { Config = config ?? throw new ArgumentNullException(nameof(config)); }

        /// <summary>Nut → fret n: d(n) = L·(1 − 2^(−n/12)). d(0) = 0 (the nut).</summary>
        public double FretDistanceMm(int fret)
        {
            if (fret < 0) throw new ArgumentOutOfRangeException(nameof(fret), fret, "fret must be ≥ 0");
            return Config.ScaleLengthMm * (1.0 - Math.Pow(2.0, -fret / 12.0));
        }

        /// <summary>Where a fingertip presses for fret n (≥ 1): FingertipFraction of the way from fret n−1 to fret n.</summary>
        public double FingertipAlongMm(int fret)
        {
            if (fret < 1) throw new ArgumentOutOfRangeException(nameof(fret), fret, "fingertip fret must be ≥ 1");
            double a = FretDistanceMm(fret - 1), b = FretDistanceMm(fret);
            return a + Config.FingertipFraction * (b - a);
        }

        /// <summary>String spacing (6 → 1 span) at a distance along the neck, interpolated nut → saddle.</summary>
        public double StringSpanMm(double alongMm)
        {
            double t = alongMm / Config.ScaleLengthMm;
            return Config.NutStringSpanMm + (Config.SaddleStringSpanMm - Config.NutStringSpanMm) * t;
        }

        /// <summary>Across-neck offset of a string at a distance along the neck (+ toward string 6).</summary>
        public double StringAcrossMm(int stringNumber, double alongMm)
        {
            Tuning.CheckString(stringNumber);
            return StringSpanMm(alongMm) * ((stringNumber - 1) / (double)(Tuning.StringCount - 1) - 0.5);
        }

        public NeckPoint FingertipTarget(int stringNumber, int fret)
        {
            double along = FingertipAlongMm(fret);
            return new NeckPoint(along, StringAcrossMm(stringNumber, along));
        }

        /// <summary>One dot per fretted note of the shape (open and muted strings have none). Allocates; call at setup.</summary>
        public IEnumerable<FingertipDot> FingertipDots(ChordShape shape)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            foreach (var n in shape.FrettedNotes())
                yield return new FingertipDot(n.String, n.Fret, n.Finger, FingertipTarget(n.String, n.Fret));
        }
    }
}
