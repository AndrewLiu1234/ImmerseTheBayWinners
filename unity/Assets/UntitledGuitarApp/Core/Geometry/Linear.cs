using System;
using System.Numerics;

namespace UntitledGuitarApp.Core.Geometry
{
    // Double-precision 3-vector / 3×3 matrix for the solver (System.Numerics is float-only). Internal on purpose.
    internal readonly struct V3
    {
        public readonly double X, Y, Z;
        public V3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static V3 From(Vector3 v) => new V3(v.X, v.Y, v.Z);
        public Vector3 ToVector3() => new Vector3((float)X, (float)Y, (float)Z);

        public double LengthSq => X * X + Y * Y + Z * Z;
        public double Length => Math.Sqrt(LengthSq);
        public bool IsFinite => !(double.IsNaN(X) || double.IsNaN(Y) || double.IsNaN(Z) || double.IsInfinity(X) || double.IsInfinity(Y) || double.IsInfinity(Z));

        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator *(V3 a, double s) => new V3(a.X * s, a.Y * s, a.Z * s);
        public static V3 operator /(V3 a, double s) => new V3(a.X / s, a.Y / s, a.Z / s);
        public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static V3 Cross(V3 a, V3 b) => new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }

    internal readonly struct M3
    {
        // row-major
        readonly double _00, _01, _02, _10, _11, _12, _20, _21, _22;

        public M3(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
        { _00 = m00; _01 = m01; _02 = m02; _10 = m10; _11 = m11; _12 = m12; _20 = m20; _21 = m21; _22 = m22; }

        public static readonly M3 Identity = new M3(1, 0, 0, 0, 1, 0, 0, 0, 1);

        public double this[int r, int c]
        {
            get
            {
                switch (r * 3 + c)
                {
                    case 0: return _00; case 1: return _01; case 2: return _02;
                    case 3: return _10; case 4: return _11; case 5: return _12;
                    case 6: return _20; case 7: return _21; default: return _22;
                }
            }
        }

        public static M3 FromColumns(V3 a, V3 b, V3 c) => new M3(a.X, b.X, c.X, a.Y, b.Y, c.Y, a.Z, b.Z, c.Z);
        public static M3 Outer(V3 a, V3 b) => new M3(a.X * b.X, a.X * b.Y, a.X * b.Z, a.Y * b.X, a.Y * b.Y, a.Y * b.Z, a.Z * b.X, a.Z * b.Y, a.Z * b.Z);
        public static M3 Skew(V3 v) => new M3(0, -v.Z, v.Y, v.Z, 0, -v.X, -v.Y, v.X, 0);

        public M3 Transpose() => new M3(_00, _10, _20, _01, _11, _21, _02, _12, _22);

        public static V3 operator *(M3 m, V3 v) => new V3(
            m._00 * v.X + m._01 * v.Y + m._02 * v.Z,
            m._10 * v.X + m._11 * v.Y + m._12 * v.Z,
            m._20 * v.X + m._21 * v.Y + m._22 * v.Z);

        public static M3 operator *(M3 a, M3 b)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i * 3 + j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
            return new M3(r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8]);
        }

        public static M3 operator *(M3 a, double s) => new M3(a._00 * s, a._01 * s, a._02 * s, a._10 * s, a._11 * s, a._12 * s, a._20 * s, a._21 * s, a._22 * s);

        public static M3 operator -(M3 a, M3 b) => new M3(a._00 - b._00, a._01 - b._01, a._02 - b._02, a._10 - b._10, a._11 - b._11, a._12 - b._12, a._20 - b._20, a._21 - b._21, a._22 - b._22);

        /// <summary>exp([w]×): rotation by |w| radians about w.</summary>
        public static M3 Rodrigues(V3 w)
        {
            double th = w.Length;
            if (th < 1e-12) return Identity + Skew(w);
            var k = Skew(w / th);
            return Identity + k * Math.Sin(th) + (k * k) * (1 - Math.Cos(th));
        }

        public static M3 operator +(M3 a, M3 b) => new M3(a._00 + b._00, a._01 + b._01, a._02 + b._02, a._10 + b._10, a._11 + b._11, a._12 + b._12, a._20 + b._20, a._21 + b._21, a._22 + b._22);

        /// <summary>Gram–Schmidt on the columns, keeping a proper rotation (removes LM drift).</summary>
        public M3 Orthonormalized()
        {
            var c0 = new V3(_00, _10, _20);
            var c1 = new V3(_01, _11, _21);
            c0 = c0 / c0.Length;
            c1 = c1 - c0 * V3.Dot(c1, c0);
            c1 = c1 / c1.Length;
            return FromColumns(c0, c1, V3.Cross(c0, c1));
        }

        public Quaternion ToQuaternion()
        {
            double tr = _00 + _11 + _22, w, x, y, z;
            if (tr > 0)
            {
                double s = Math.Sqrt(tr + 1.0) * 2;
                w = 0.25 * s; x = (_21 - _12) / s; y = (_02 - _20) / s; z = (_10 - _01) / s;
            }
            else if (_00 > _11 && _00 > _22)
            {
                double s = Math.Sqrt(1.0 + _00 - _11 - _22) * 2;
                w = (_21 - _12) / s; x = 0.25 * s; y = (_01 + _10) / s; z = (_02 + _20) / s;
            }
            else if (_11 > _22)
            {
                double s = Math.Sqrt(1.0 + _11 - _00 - _22) * 2;
                w = (_02 - _20) / s; x = (_01 + _10) / s; y = 0.25 * s; z = (_12 + _21) / s;
            }
            else
            {
                double s = Math.Sqrt(1.0 + _22 - _00 - _11) * 2;
                w = (_10 - _01) / s; x = (_02 + _20) / s; y = (_12 + _21) / s; z = 0.25 * s;
            }
            return Quaternion.Normalize(new Quaternion((float)x, (float)y, (float)z, (float)w));
        }
    }
}
