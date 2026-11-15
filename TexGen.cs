// Port of TexGen.cpp. A TextureDef is a tiny image-generation program operating on
// four 256x256 RGBA layers; a MaterialDef selects one finished layer. Running these
// commands is what produces the pipe metal—there are no source bitmaps to substitute.
//
// Wrapping indices, truncation, in-place blur and MSVCRT rand are all observable
// parts of the output. Changing them changes how the textures look... so don't.
namespace GenesisTunnel;

static partial class TexGen
{
    const int Size = 256;
    const double Pi = 3.141592654, Rad = 0.017453292;

    // Scratch buffers shared by all commands, like the original's globals.
    static readonly byte[] buffer = new byte[Size * Size * 4];
    static readonly float[] distance = BuildDistance();

    // Microsoft C runtime's rand(), which the noise patterns depend on
    static uint seed = 1;

    // Player also uses this generator for the original grid-effect jitter.
    public static void SRand(uint s)
    {
        seed = s;
    }

    public static int Rand()
    {
        // Unsigned overflow is part of the algorithm and must remain unchecked.
        seed = (seed * 214013u) + 2531011u;
        return (int)((seed >> 16) & 0x7fff);
    }

    static float[] BuildDistance()
    {
        // Precompute this once; Cells samples it repeatedly for every pixel/point.
        var d = new float[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                d[(y * Size) + x] = (float)(Math.Sqrt(((127 - x) * (127 - x)) + ((127 - y) * (127 - y))) * 2.0 / 256.0);
        return d;
    }

    // Index of pixel [a][b], channel c, in a flat RGBA array. Both indices wrap like a C byte.
    static int At(int a, int b, int c = 0)
    {
        return ((((a & 255) << 8) | (b & 255)) * 4) + c;
    }

    // C-style "(byte)someFloat": drop the fraction, keep the low 8 bits.
    static byte B(double v)
    {
        return unchecked((byte)(int)v);
    }

    static uint Seed(byte r4, byte r3, byte r2, byte r1)
    {
        return (uint)(r1 + (r2 * 256) + (r3 * 65536)) + (r4 * 16777216u);
    }

    public static byte[][] Generate(TextureDef t)
    {
        // Cache on the definition, not per material: several materials can select
        // different layers from one generated texture.
        if (t.Layers != null) return t.Layers;
        var layers = new byte[4][];
        for (int i = 0; i < 4; i++) layers[i] = new byte[Size * Size * 4];
        foreach (byte[] c in t.Commands) Perform(layers, c);
        t.Layers = layers;
        return layers;
    }

    static void Perform(byte[][] L, byte[] c)
    {
        // Argument meanings vary by opcode and some deliberately address layers or
        // channels with their low two bits. This is an interpreter, not a safe API.
        byte[] l = L[c[1] & 3];
        switch (c[0])
        {
            case 1: Fractal(l, c); break;
            case 2: Plasma(l, c); break;
            case 3: Cells(l, c); break;
            case 4: EnvMap(l, c); break;
            case 5: SubPlasma(l, c); break;
            case 6: Array.Clear(l); break;
            case 7: /* throw new NotImplementedException(); */ break; // needs Windows fonts; unused by the pipe scenes
            case 10: SinDist(l, c); break;
            case 11: Offset(l, c); break;
            case 12: Twirl(l, c); break;
            case 15: Blur(l, c); break;
            case 17: Map(L, l, c); break;
            case 18: DirBlur(L, l, c); break;
            case 23:
                for (int i = 0; i < Size * Size; i++)
                    l[(i * 4) + (c[2] & 3)] = L[c[3] & 3][(i * 4) + (c[4] & 3)];
                break;
            case 24: Mix(L, l, c); break;
            case 25:
                for (int i = 0; i < Size * Size; i++)
                    for (int z = 0; z < 3; z++)
                        l[(i * 4) + z] = B(l[(i * 4) + z] * (float)L[c[2] & 3][(i * 4) + z] / 255.0);
                break;
            case 26:
                for (int i = 0; i < Size * Size; i++)
                    for (int z = 0; z < 3; z++)
                        l[(i * 4) + z] = (byte)Math.Min(255, l[(i * 4) + z] + L[c[2] & 3][(i * 4) + z]);
                break;
            case 27:
                for (int i = 0; i < Size * Size; i++)
                    for (int z = 0; z < 3; z++)
                        l[(i * 4) + z] = Math.Max(l[(i * 4) + z], L[c[2] & 3][(i * 4) + z]);
                break;
            case 30: Contrast(l, c); break;
            case 31:
                for (int i = 0; i < Size * Size; i++)
                    for (int z = 0; z < 3; z++)
                        l[(i * 4) + z] = (byte)(255 - l[(i * 4) + z]);
                break;
            case 32: Shade(L, l, c); break;
            case 33: Bright(l, c); break;
            case 34:
                for (int i = 0; i < Size * Size; i++)
                {
                    int k = (i * 4) + (c[2] & 3);
                    l[k] = B(Math.Sin(l[k] * Rad * (255.0 / 360.0) * c[3]) * 255.0);
                }
                break;
            case 35: Scale(l, c); break;
            case 36: Hsv(l, c); break;
            case 37: Colorize(l, c); break;
            case 38: MixMap(L, l, c); break;
            case 39: Emboss(l, c); break;
        }
    }

    // Noise and base patterns.

    // Catmull-Rom interpolation, clamped to a byte after truncation.
    static byte Spline(float v0, float v1, float v2, float v3, float x)
    {
        float p = v3 - v2 - (v0 - v1);
        float q = v0 - v1 - p;
        float r = v2 - v0;
        int t = (int)((p * x * x * x) + (q * x * x) + (r * x) + v1);
        return (byte)Math.Clamp(t, 0, 255);
    }

    static void DoSub(int size, int col)
    {
        // Fill the coarse lattice first, interpolate one axis, then the other.
        // At() wraps the lattice so opposite texture edges join seamlessly.
        int pnum = 1 << (8 - size), psize = 1 << size;

        for (int y = 0; y < pnum; y++)
            for (int x = 0; x < pnum; x++)
                buffer[At(y << size, x << size, col)] = (byte)Rand();

        for (int x = 0; x < pnum; x++)
            for (int y = 0; y < pnum; y++)
                for (int z = 0; z < psize; z++)
                    buffer[At((y << size) + z, x << size, col)] = Spline(
                        buffer[At((y - 1) << size, x << size, col)],
                        buffer[At(y << size, x << size, col)],
                        buffer[At((y + 1) << size, x << size, col)],
                        buffer[At((y + 2) << size, x << size, col)],
                        (float)z / psize);

        for (int y = 0; y < 256; y++)
            for (int x = 0; x < pnum; x++)
                for (int z = 0; z < psize; z++)
                    buffer[At(y, (x << size) + z, col)] = Spline(
                        buffer[At(y, (x - 1) << size, col)],
                        buffer[At(y, x << size, col)],
                        buffer[At(y, (x + 1) << size, col)],
                        buffer[At(y, (x + 2) << size, col)],
                        (float)z / psize);
    }

    static void Fractal(byte[] l, byte[] c)
    {
        // Each octave has half the previous cell size and amplitude. Addition uses
        // unchecked byte wrap because that is what the original texture contains.
        int col = c[2] & 3;
        SRand(Seed(c[4], c[5], c[6], c[7]));
        for (int i = 0; i < Size * Size; i++)
        {
            l[(i * 4) + col] = 0;
            buffer[(i * 4) + col] = 0;
        }

        for (int b = 1; b < 8; b++)
        {
            DoSub(b, col);
            for (int i = 0; i < Size * Size; i++)
                l[(i * 4) + col] = unchecked((byte)(l[(i * 4) + col] + (buffer[(i * 4) + col] >> (8 - b))));
        }
    }

    static void SubPlasma(byte[] l, byte[] c)
    {
        int col = c[2] & 3;
        if (c[3] == 0) return;
        int size = 0;
        while ((c[3] & (1 << size)) == 0) size++;
        SRand(Seed(c[4], c[5], c[6], c[7]));
        DoSub(size, col);
        for (int i = 0; i < Size * Size; i++) l[(i * 4) + col] = buffer[(i * 4) + col];
    }

    static void Plasma(byte[] l, byte[] c)
    {
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                l[At(y, x, c[8] & 3)] = B(127.0
                    + (63.5 * Math.Sin((x + c[4]) / 256.0 * c[2] * 2.0 * Pi))
                    + (63.5 * Math.Sin((y + c[5]) / 256.0 * c[3] * 2.0 * Pi)));
    }

    static void Cells(byte[] l, byte[] c)
    {
        var dots = new byte[256, 2];
        SRand(Seed(c[5], c[6], c[7], c[8]));
        for (int i = 0; i < c[3]; i++)
        {
            dots[i, 0] = (byte)Rand();
            dots[i, 1] = (byte)Rand();
        }

        float s = c[4] * 5f;
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                float r = 255f;
                for (int i = 0; i < c[3]; i++)
                {
                    float r2 = distance[(((y + dots[i, 1]) & 255) * Size) + ((x + dots[i, 0]) & 255)] * s;
                    if (r2 < r) r = r2;
                }
                l[At(y, x, c[2] & 3)] = (byte)Math.Clamp(r, 0f, 255f);
            }
    }

    static void EnvMap(byte[] l, byte[] c)
    {
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                float dist = distance[(y * Size) + x];
                if (c[2] == 0)
                {
                    byte v = dist <= 1 ? B(255 - (dist * 255)) : (byte)0;
                    l[At(y, x, 0)] = v;
                    l[At(y, x, 1)] = v;
                    l[At(y, x, 2)] = v;
                }
                else
                {
                    float center = (float)(1.0 - (c[4] / 512.0));
                    l[At(y, x, c[3] & 3)] = dist <= center ? B(255 - (dist / center * 255)) : (byte)0;
                }
            }
    }

    // Spatial distortions and filters. Most render through buffer before overwriting
    // the source; Blur is the notable in-place exception.

    static void Bilinear(byte[] l, float xp, float yp, int outIndex)
    {
        float xf = xp - MathF.Floor(xp), yf = yp - MathF.Floor(yp);
        int x0 = (int)MathF.Floor(xp) & 255, y0 = (int)MathF.Floor(yp) & 255;
        int x1 = (x0 + 1) & 255, y1 = (y0 + 1) & 255;
        float a = (1 - xf) * (1 - yf), b = xf * (1 - yf), cc = (1 - xf) * yf, d = xf * yf;
        for (int i = 0; i < 3; i++)
            buffer[outIndex + i] = B((a * l[At(y0, x0, i)]) + (b * l[At(y0, x1, i)]) + (cc * l[At(y1, x0, i)]) + (d * l[At(y1, x1, i)]));
    }

    // Finish a distortion by replacing the complete layer, including untouched alpha.
    static void CopyBufferTo(byte[] l)
    {
        Array.Copy(buffer, l, buffer.Length);
    }

    static void SinDist(byte[] l, byte[] c)
    {
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                float xp = (float)(x + (Math.Cos((y + c[6]) / 256.0 * c[2] * 2.0 * Pi) * c[4]));
                float yp = (float)(y + (Math.Cos((x + c[7]) / 256.0 * c[3] * 2.0 * Pi) * c[5]));
                Bilinear(l, xp, yp, At(y, x));
            }
        CopyBufferTo(l);
    }

    static void Offset(byte[] l, byte[] c)
    {
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                for (int i = 0; i < 4; i++)
                    buffer[At(x, y, i)] = l[At(x - c[2], y - c[3], i)];
        CopyBufferTo(l);
    }

    static void Twirl(byte[] l, byte[] c)
    {
        float deg = (c[2] - 128f) * 16f;
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                float xm = x - 128f, ym = y - 128f;
                float r = (float)(Math.Sqrt((xm * xm) + (ym * ym)) / 128.0);
                if (r > 1) r = 1;
                float phi = (float)Math.Cos(r * Pi / 2);
                phi *= phi;
                float dpr = (float)(deg * phi * Rad);
                float cd = MathF.Cos(dpr), sd = MathF.Sin(dpr);
                float xn = Math.Clamp((xm * cd) + (ym * sd) + 128f, 0f, 255f);
                float yn = Math.Clamp((-xm * sd) + (ym * cd) + 128f, 0f, 255f);
                Bilinear(l, xn, yn, At(y, x));
            }
        CopyBufferTo(l);
    }

    static void Map(byte[][] L, byte[] l, byte[] c)
    {
        byte[] dx = L[c[2] & 3], dy = L[c[5] & 3];
        for (int x = 0; x < 256; x++)
            for (int y = 0; y < 256; y++)
            {
                int sx = (int)(x + (dx[At(x, y, c[3] & 3)] / 255.0 * c[4]));
                int sy = (int)(y + (dy[At(x, y, c[6] & 3)] / 255.0 * c[7]));
                for (int i = 0; i < 4; i++) buffer[At(x, y, i)] = l[At(sx, sy, i)];
            }
        CopyBufferTo(l);
    }

    static void DirBlur(byte[][] L, byte[] l, byte[] c)
    {
        byte[] dirLayer = L[c[2] & 3];
        for (int x = 0; x < 256; x++)
            for (int y = 0; y < 256; y++)
            {
                int dir = (int)(dirLayer[At(x, y, c[3] & 3)] * 360.0 / 256.0) + 270;
                float b1 = (float)Math.Cos(dir * Rad), b2 = (float)Math.Sin(dir * Rad);
                int r = 0, g = 0, b = 0, z;
                for (z = 1; z <= c[4]; z++)
                {
                    int p1 = (int)(x + (b1 * z)), p2 = (int)(y + (b2 * z));
                    r += l[At(p1, p2, 0)];
                    g += l[At(p1, p2, 1)];
                    b += l[At(p1, p2, 2)];
                }
                // The original divides by one more than the number of samples.
                buffer[At(x, y, 0)] = unchecked((byte)(int)((float)r / z));
                buffer[At(x, y, 1)] = unchecked((byte)(int)((float)g / z));
                buffer[At(x, y, 2)] = unchecked((byte)(int)((float)b / z));
            }
        CopyBufferTo(l);
    }

    static void Blur(byte[] l, byte[] c)
    {
        // Do not introduce a scratch copy: already blurred neighbors feed later
        // pixels, and that asymmetry is visible in the generated material.
        for (int pass = 1; pass <= c[2]; pass++)
            for (int x = 0; x < 256; x++)
                for (int y = 0; y < 256; y++)
                    for (int z = 0; z < 3; z++)
                        l[At(x, y, z)] = B((l[At(x, y, z)]
                            + l[At(x + 1, y, z)] + l[At(x, y + 1, z)] + l[At(x - 1, y, z)] + l[At(x, y - 1, z)]
                            + l[At(x + 1, y + 1, z)] + l[At(x - 1, y - 1, z)] + l[At(x - 1, y + 1, z)] + l[At(x + 1, y - 1, z)]) / 9.0);
    }

    static void Emboss(byte[] l, byte[] c)
    {
        for (int x = 0; x < 256; x++)
            for (int y = 0; y < 256; y++)
                for (int z = 0; z < 3; z++)
                {
                    int acc = -l[At(x - 1, y - 1, z)] - l[At(x - 1, y, z)] - l[At(x - 1, y + 1, z)]
                              + l[At(x + 1, y - 1, z)] + l[At(x + 1, y, z)] + l[At(x + 1, y + 1, z)] + 128;
                    buffer[At(x, y, z)] = (byte)Math.Clamp(acc, 0, 255);
                }
        CopyBufferTo(l);
    }

    // Layer composition and color operations.

    static void Mix(byte[][] L, byte[] l, byte[] c)
    {
        byte[] o = L[c[2] & 3];
        for (int i = 0; i < Size * Size; i++)
            for (int z = 0; z < 3; z++)
                l[(i * 4) + z] = B((l[(i * 4) + z] / 255.0 * c[3]) + (o[(i * 4) + z] / 255.0 * (255.0 - c[3])));
    }

    static void MixMap(byte[][] L, byte[] l, byte[] c)
    {
        byte[] other = L[c[2] & 3], mask = L[c[3] & 3];
        for (int i = 0; i < Size * Size; i++)
        {
            byte m = mask[(i * 4) + (c[4] & 3)];
            for (int z = 0; z < 3; z++)
                l[(i * 4) + z] = B((l[(i * 4) + z] / 255.0 * m) + (other[(i * 4) + z] / 255.0 * (255.0 - m)));
        }
    }

    static void Shade(byte[][] L, byte[] l, byte[] c)
    {
        byte[] s = L[c[2] & 3];
        for (int i = 0; i < Size * Size; i++)
        {
            byte sv = s[(i * 4) + (c[3] & 3)];
            float coll = (float)(sv / 127.0), c2 = (float)((255.0 - sv) / 127.0);
            for (int z = 0; z < 3; z++)
            {
                if (coll <= 1) l[(i * 4) + z] = B(l[(i * 4) + z] * coll);
                else l[(i * 4) + z] = unchecked((byte)(255 - B((255.0 - l[(i * 4) + z]) * c2)));
            }
        }
    }

    static void Bright(byte[] l, byte[] c)
    {
        for (int i = 0; i < Size * Size; i++)
            for (int z = 0; z < 3; z++)
            {
                if (c[2] <= 128) l[(i * 4) + z] = B(l[(i * 4) + z] / 128.0 * c[2]);
                else l[(i * 4) + z] = unchecked((byte)(255 - B((255.0 - l[(i * 4) + z]) / 128.0 * (255.0 - c[2]))));
            }
    }

    static void Contrast(byte[] l, byte[] c)
    {
        for (int i = 0; i < Size * Size; i++)
            for (int z = 0; z < 3; z++)
                l[(i * 4) + z] = (byte)Math.Clamp((int)(((l[(i * 4) + z] - 128.0) * c[2] / 32.0) + 128.0), 0, 255);
    }

    static void Scale(byte[] l, byte[] c)
    {
        float d = (c[4] - c[3]) / 255f;
        for (int i = 0; i < Size * Size; i++)
        {
            int k = (i * 4) + (c[2] & 3);
            l[k] = B(c[3] + (d * l[k]));
        }
    }

    static void Colorize(byte[] l, byte[] c)
    {
        for (int i = 0; i < Size * Size; i++)
        {
            float v = l[(i * 4) + (c[2] & 3)];
            l[(i * 4) + 0] = B(((c[4] - (float)c[3]) / 255.0 * v) + c[3]);
            l[(i * 4) + 1] = B(((c[6] - (float)c[5]) / 255.0 * v) + c[5]);
            l[(i * 4) + 2] = B(((c[8] - (float)c[7]) / 255.0 * v) + c[7]);
        }
    }

    static void Hsv(byte[] l, byte[] c)
    {
        // Keep the source generator's HSV branch order and integer output truncation;
        // switching to a framework color conversion would round many pixels differently.
        float hShift = c[2] * 360f / 255f, sScale = (byte)(255 - c[3]) / 255f;
        for (int i = 0; i < Size * Size; i++)
        {
            float r = l[i * 4], g = l[(i * 4) + 1], b = l[(i * 4) + 2];
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            float v = max, s = max != 0 ? (max - min) / max : 0, h = -1;
            if (s != 0)
            {
                float delta = max - min;
                if (r == max) h = (max - b - (max - g)) / delta;
                else if (g == max) h = 2f + ((max - r - (max - b)) / delta);
                else h = 4f + ((max - g - (max - r)) / delta);
                h *= 60f;
                while (h < 0) h += 360f;
                while (h >= 360f) h -= 360f;
            }
            h += hShift;
            s *= sScale;

            while (h < 0) h += 360f;
            while (h >= 360f) h -= 360f;
            float or = v, og = v, ob = v;
            if (s != 0)
            {
                h /= 60f;
                float fl = MathF.Floor(h), f = h - fl;
                float p = v * (1f - s), q = v * (1f - (s * f)), t = v * (1f - (s * (1f - f)));
                switch ((int)fl)
                {
                    case 0: or = v; og = t; ob = p; break;
                    case 1: or = q; og = v; ob = p; break;
                    case 2: or = p; og = v; ob = t; break;
                    case 3: or = p; og = q; ob = v; break;
                    case 4: or = t; og = p; ob = v; break;
                    default: or = v; og = p; ob = q; break;
                }
            }
            l[i * 4] = (byte)Math.Min((int)or, 255);
            l[(i * 4) + 1] = (byte)Math.Min((int)og, 255);
            l[(i * 4) + 2] = (byte)Math.Min((int)ob, 255);
            l[(i * 4) + 3] = 0;
        }
    }
}
