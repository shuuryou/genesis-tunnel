// Hermite keyframe tracks, generated-mesh records and the intro's small 4x4 matrix.
// Ported from Track.cpp and vectors.cpp. The screensaver does not fly the saved
// cameras, but the loader and object generators still use these types; keeping their
// interpolation and layout conventions avoids subtle differences in the data.

using System.Numerics;

namespace GenesisTunnel;

// A scalar animation channel. Slopes are calculated once after all delta-encoded
// keys have been read, then Get can interpolate at arbitrary frame times.
sealed class Track(int keyCount)
{
    struct Key { public float Data, An, Bn; public int Frame; }

    readonly Key[] keys = new Key[keyCount];
    public int Count => keys.Length;

    public void Set(int index, int frame, float value)
    {
        keys[index].Frame = frame;
        keys[index].Data = value;
    }

    public int FirstFrame => keys.Length > 0 ? keys[0].Frame : 0;
    public int LastFrame => keys.Length > 0 ? keys[^1].Frame : 0;

    public void InitVectors()
    {
        // Both tangent fields follow the original implementation, including its
        // treatment of the ends as part of a looping track.
        if (keys.Length <= 1) return;
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i].An = Slope(i);
            keys[i].Bn = Slope(i);
        }
    }

    // The slope at a key is the average of the steps to its two neighbors.
    // The first and last key borrow a neighbor from the other end (the original
    // treats every track as a loop).
    float Slope(int n)
    {
        int count = keys.Length;
        float here = keys[n].Data, before, after;
        if (n == 0)
        {
            after = keys[1].Data;
            if (count == 2) return after - here;
            before = keys[count - 2].Data;
        }
        else if (n == count - 1)
        {
            before = keys[n - 1].Data;
            if (count == 2) return here - before;
            after = keys[1].Data;
        }
        else
        {
            before = keys[n - 1].Data;
            after = keys[n + 1].Data;
        }
        float g1 = here - before, g2 = after - here;
        return g1 + ((g2 - g1) * 0.5f);
    }

    static float Hermite(float p1, float p2, float r1, float r2, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return (p1 * ((2f * t3) - (3f * t2) + 1f)) + (r1 * (t3 - (2f * t2) + t))
             + (p2 * ((-2f * t3) + (3f * t2))) + (r2 * (t3 - t2));
    }

    public float Get(float frame)
    {
        // Frames outside the stored range clamp to the nearest key. Empty tracks are
        // common in optional camera channels and evaluate to zero.
        int count = keys.Length;
        if (count == 0) return 0f;
        if (count == 1 || keys[0].Frame >= frame) return keys[0].Data;

        int y = -2;
        for (int x = 0; x < count; x++)
            if (frame <= keys[x].Frame)
            {
                y = x - 1;
                break;
            }
        if (y == -2) return keys[count - 1].Data;

        float t = (frame - keys[y].Frame) / (keys[y + 1].Frame - keys[y].Frame);
        return Hermite(keys[y].Data, keys[y + 1].Data, keys[y].An, keys[y + 1].Bn, t);
    }
}

// Explicit M[row,column] storage makes the C++ port unambiguous. Conversion to
// System.Numerics happens only at the rendering/inversion boundary.
sealed class Mat
{
    public float[,] M = new float[4, 4];

    // Most generated matrices begin with an uninitialized Mat, so factories are
    // responsible for setting every diagonal element they rely on.
    public static Mat Identity()
    {
        var m = new Mat();
        for (int i = 0; i < 4; i++) m.M[i, i] = 1;
        return m;
    }

    // M is a reference-type array; copying the Mat object alone would not be enough.
    public Mat Clone()
    {
        var m = new Mat();
        Array.Copy(M, m.M, 16);
        return m;
    }

    // Conventional row-by-column product: in Mul(a,b), b acts on a point first.
    public static Mat Mul(Mat a, Mat b)
    {
        var t = new Mat();
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                float r = 0;
                for (int k = 0; k < 4; k++) r += a.M[i, k] * b.M[k, j];
                t.M[i, j] = r;
            }
        return t;
    }

    public static Mat Translate(float x, float y, float z)
    {
        var m = Identity();
        m.M[0, 3] = x; m.M[1, 3] = y; m.M[2, 3] = z;
        return m;
    }

    public static Mat Scale(float x, float y, float z)
    {
        var m = Identity();
        m.M[0, 0] = x; m.M[1, 1] = y; m.M[2, 2] = z;
        return m;
    }

    public static Mat Rotate(float ax, float ay, float az, float phi)
    {
        // Rodrigues' formula, with a zero-axis guard for harmless map-modifier data.
        var a = new Vector3(ax, ay, az);
        float len = a.Length();
        a = len != 0 ? a / len : Vector3.Zero;
        float c = MathF.Cos(phi), s = MathF.Sin(phi);
        float[] v = [a.X, a.Y, a.Z];
        var m = new Mat();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                m.M[i, j] = (i == j ? c : 0) + (v[i] * v[j] * (1 - c));

        m.M[0, 1] += -a.Z * s; m.M[0, 2] += a.Y * s;
        m.M[1, 0] += a.Z * s; m.M[1, 2] += -a.X * s;
        m.M[2, 0] += -a.Y * s; m.M[2, 1] += a.X * s;
        m.M[3, 3] = 1;
        return m;
    }

    public Vector3 Point(Vector3 v)
    {
        return new(
        (M[0, 0] * v.X) + (M[0, 1] * v.Y) + (M[0, 2] * v.Z) + M[0, 3],
        (M[1, 0] * v.X) + (M[1, 1] * v.Y) + (M[1, 2] * v.Z) + M[1, 3],
        (M[2, 0] * v.X) + (M[2, 1] * v.Y) + (M[2, 2] * v.Z) + M[2, 3]);
    }

    // Directions must not receive the translation in column three.
    public Vector3 Direction(Vector3 v)
    {
        return new(
        (M[0, 0] * v.X) + (M[0, 1] * v.Y) + (M[0, 2] * v.Z),
        (M[1, 0] * v.X) + (M[1, 1] * v.Y) + (M[1, 2] * v.Z),
        (M[2, 0] * v.X) + (M[2, 1] * v.Y) + (M[2, 2] * v.Z));
    }

    public Matrix4x4 ToRaylib()
    {
        return new(
        M[0, 0], M[0, 1], M[0, 2], M[0, 3],
        M[1, 0], M[1, 1], M[1, 2], M[1, 3],
        M[2, 0], M[2, 1], M[2, 2], M[2, 3],
        M[3, 0], M[3, 1], M[3, 2], M[3, 3]);
    }

    public static Mat Invert(Mat m)
    {
        // System.Numerics supplies the inverse; the explicit assignments avoid any
        // doubt about how its fields map back to M[row,column].
        var n = new Matrix4x4(
            m.M[0, 0], m.M[0, 1], m.M[0, 2], m.M[0, 3],
            m.M[1, 0], m.M[1, 1], m.M[1, 2], m.M[1, 3],
            m.M[2, 0], m.M[2, 1], m.M[2, 2], m.M[2, 3],
            m.M[3, 0], m.M[3, 1], m.M[3, 2], m.M[3, 3]);
        var r = new Mat();
        if (!Matrix4x4.Invert(n, out var inv))
        {
            r.M = new float[4, 4];
            return r;
        }
        r.M[0, 0] = inv.M11; r.M[0, 1] = inv.M12; r.M[0, 2] = inv.M13; r.M[0, 3] = inv.M14;
        r.M[1, 0] = inv.M21; r.M[1, 1] = inv.M22; r.M[1, 2] = inv.M23; r.M[1, 3] = inv.M24;
        r.M[2, 0] = inv.M31; r.M[2, 1] = inv.M32; r.M[2, 2] = inv.M33; r.M[2, 3] = inv.M34;
        r.M[3, 0] = inv.M41; r.M[3, 1] = inv.M42; r.M[3, 2] = inv.M43; r.M[3, 3] = inv.M44;
        return r;
    }
}

// Working vertex. D is the generated position, Base the pre-map position, and
// Position scratch space for loft tangents. Dt/T are raw/final texture coordinates.
sealed class GenVertex
{
    public Vector3 D;          // position in the object's own space
    public Vector3 Base;       // position before a "map" modifier moved it
    public Vector3 Position;   // scratch copy used by the loft generator
    public Vector3 Normal;
    public Vector2 Dt, T;      // texture coordinate: as generated / after scaling
    // All fields are values, so MemberwiseClone is sufficient for Clone.
    public GenVertex Copy()
    {
        return (GenVertex)MemberwiseClone();
    }
}

// Triangle plus both raw and transformed per-corner UVs. Material IDs are filled
// after primitive generation, once the object's flags and texture are known.
sealed class GenPolygon
{
    public int V1, V2, V3;
    public Vector3 Normal;
    public Vector2 T1, T2, T3, D1, D2, D3;
    public bool Flat;              // flat shaded (one normal) or smooth (vertex normals)
    public bool NormalInverted;
    public float Alpha = 1;
    public int TextureMaterial = -1, EnvMaterial = -1;
    // Clone rebases V1/V2/V3 after copying the polygon.
    public GenPolygon Copy()
    {
        return (GenPolygon)MemberwiseClone();
    }
}
