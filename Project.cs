// Reader for the embedded Project Genesis project file, ported from introinput.cpp
// in Conspiracy's 2012 source pack. This is a data loader, not a converter to a new
// intermediate format: primitive IDs, flag bits, generator commands, object numbers
// and timeline operations are retained because later ports depend on them.
//
// The file contains no triangle soup or texture bitmaps. Loading is comparatively
// cheap; MeshGen and TexGen execute the stored recipes only when a scene/material
// actually needs them. See Data/CONSPIRACY-LICENSE.txt for the upstream license.

using System.Numerics;

namespace GenesisTunnel;

// Sequential little-endian reader. No bounds hiding or alignment fixups are wanted:
// landing anywhere other than the exact final byte indicates a format mismatch.
sealed class ByteReader(byte[] data)
{
    readonly byte[] data = data;
    public int Pos;

    public bool AtEnd => Pos >= data.Length;

    // The project format is explicitly little-endian.
    public byte Byte()
    {
        return data[Pos++];
    }

    // Signed words are used by coordinates, IDs and packed counts alike.
    public short Word()
    {
        short v = (short)(data[Pos] | (data[Pos + 1] << 8));
        Pos += 2;
        return v;
    }

    public ushort UWord()
    {
        return (ushort)Word();
    }

    public float Float(int floatBytes)
    {
        // Only the high bytes of each IEEE float survived compression. Read the
        // little-endian remainder and shift it back; missing low bytes become zero.
        uint w = 0;
        for (int i = 0; i < floatBytes; i++) w |= (uint)data[Pos + i] << (8 * i);
        Pos += floatBytes;
        w <<= (4 - floatBytes) * 8;
        return BitConverter.UInt32BitsToSingle(w);
    }

    // Texture commands and other fixed-size records use raw byte copies.
    public byte[] Bytes(int n)
    {
        var b = new byte[n];
        Array.Copy(data, Pos, b, 0, n);
        Pos += n;
        return b;
    }

    public int[] Selection()
    {
        // Selection order affects clone output. Reversing on read matches the way
        // the original writer constructed these lists.
        int n = Word();
        var list = new int[n];
        for (int i = 0; i < n; i++) list[n - 1 - i] = Word();
        return list;
    }
}

// Numeric IDs stored in SceneObject.Data, not a new enum whose values could drift.
static class Prim
{
    public const int Box = 1, Sphere = 3, Hasab = 6, Cone = 7, Arc = 9, Loft = 10,
                     Line = 11, Grid = 12, Clone = 15, Map = 104;
}

sealed class TextureDef
{
    public int Id;
    public List<byte[]> Commands = []; // 9 bytes each
    public byte[][] Layers; // 4 layers of 256x256 RGBA, made on demand
}

sealed class MaterialDef
{
    public int Texture, Layer, AlphaTexture, AlphaLayer, AlphaMode, Number;
}

sealed class LightDef
{
    public Vector3 Ambient, Color;
    public Vector4 Position; // w = 0: a direction (sun), w = 1: a point in space
    public float ConstAtt, LinearAtt, QuadAtt;
    public Vector3 SpotDir;
    public float SpotExponent, SpotCutoff = 180f;
}

sealed class ObjAnim
{
    // 14 tracks: position xyz, rotation axis xyz + angle, stretch xyz, color rgba.
    public Track[] T = new Track[14];
    public bool HasKeys;
}

sealed class SceneObject
{
    public uint Data; // packed flags, see the properties below
    public int Number, Parent;
    public int Texture, Envmap;
    public float[] Color = [1, 1, 1, 1];
    public int TexXScale = 1, TexYScale = 1, TexXOffset, TexYOffset;
    public int[] Params = new int[4];
    public int[] Selection; // clone: objects to copy.  map: objects to bend.
    public int MapKind; // map modifier: 0 move, 1 scale, 2 rotate
    public Vector3 MapVec;
    public float MapAngle;
    public Mat Xform = Mat.Identity();
    public Dictionary<int, ObjAnim> Anims = [];

    // Packed flag bits
    public int Primitive => (int)(Data & 0xff);
    public bool NormalsInverted => Bit(8);
    public bool Textured => Bit(10);
    public bool HasRed => Bit(11);
    public bool HasGreen => Bit(12);
    public bool HasBlue => Bit(13);
    public bool HasAlpha => Bit(14);
    public bool AlphaMap1Set => Bit(15);
    public int AlphaChannel => (int)((Data >> 16) & 3);
    public bool Material2Set => Bit(18); // "has a reflection map"
    public bool TexXSet => Bit(19);
    public bool TexYSet => Bit(20);
    public bool TexOffXSet => Bit(21);
    public bool TexOffYSet => Bit(22);
    public bool SwapTextureXY => Bit(28);
    public bool InvertTexX => Bit(29);
    public bool InvertTexY => Bit(30);
    bool Bit(int n)
    {
        return ((Data >> n) & 1) != 0;
    }

    // Generated geometry (MeshGen.cs fills these)
    public List<GenVertex> Vertices = [];
    public List<GenPolygon> Polygons = [];
}

sealed class CameraDef
{
    public int Number;
    public int KeyCount;
    public Track EyeX, EyeY, EyeZ, TargetX, TargetY, TargetZ, Fov, Roll;
}

sealed class SceneDef
{
    public int Number;
    public bool Fog;
    public Vector3 FogColor;
    public float FogDensity;
    public List<LightDef> Lights = [];
    public List<SceneObject> Objects = [];
    public List<CameraDef> Cameras = [];
    public bool Generated;

    public SceneObject Find(int number)
    {
        return Objects.FirstOrDefault(o => o.Number == number);
    }

    public CameraDef FindCamera(int number)
    {
        return Cameras.FirstOrDefault(c => c.Number == number);
    }
}

enum EventType { Layer2D = 1, Layer3D = 2, Clear = 3, RenderToTexture = 4, Feedback = 5, GridEffect = 6 }

sealed class TimelineEvent
{
    public EventType Type;
    public int Start, End; // milliseconds
    public int Pass;
    public int[] StartRect = new int[4], EndRect = new int[4]; // x1,y1,x2,y2 on a 640x480 screen
    public float[] StartCol = new float[4], EndCol = new float[4];
    public int BlendSrc, BlendDst; // index into the OpenGL blend factor list
    public bool Textured;
    public int Texture;
    public int SceneId, CamId, AnimId, CamStart, CamEnd, AnimStart, AnimEnd;
    public bool ClearScreen, ClearZ;
    public int Effect, LayerCount;
    public float ZoomDivisor, GridStart, GridEnd;
}

sealed class Project
{
    public List<TextureDef> Textures = [];
    public List<MaterialDef> Materials = [];
    public List<SceneDef> Scenes = [];
    public List<TimelineEvent> Events = [];

    public TextureDef FindTexture(int id)
    {
        return Textures.LastOrDefault(t => t.Id == id);
    }

    public MaterialDef FindMaterial(int number)
    {
        return Materials.LastOrDefault(m => m.Number == number);
    }

    public SceneDef FindScene(int number)
    {
        return Scenes.LastOrDefault(s => s.Number == number);
    }

    public static Project Load(byte[] file)
    {
        // Records are sequential and most optional fields are controlled by flag
        // bits. Do not reorder the generation work here: clones and map modifiers
        // depend on object order, but that work belongs to MeshGen.Generate.
        var p = new Project();
        var r = new ByteReader(file);
        int fb = r.Byte();   // how many bytes each float keeps

        // Textures: lists of generator commands
        int texCount = r.Byte();
        for (int i = 0; i < texCount; i++)
        {
            var t = new TextureDef { Id = r.Byte() };
            int commands = r.Byte();
            for (int c = 0; c < commands; c++)
            {
                byte[] cmd = r.Bytes(9);
                if (cmd[0] == 7 /* text */) r.Bytes(r.Byte()); // text labels are not used by our scenes
                t.Commands.Add(cmd);
            }
            p.Textures.Add(t);
        }

        // Materials: "layer N of texture M" 
        int matCount = r.Byte();
        for (int i = 0; i < matCount; i++)
            p.Materials.Add(new MaterialDef
            {
                Texture = r.Byte(),
                Layer = r.Byte(),
                AlphaTexture = r.Byte(),
                AlphaLayer = r.Byte(),
                AlphaMode = r.Byte(),
                Number = r.Byte(),
            });

        // Scenes
        int sceneCount = r.Byte();
        for (int si = 0; si < sceneCount; si++)
        {
            var s = new SceneDef
            {
                Number = r.Byte(),
                Fog = r.Byte() != 0
            };
            if (s.Fog)
            {
                s.FogColor = new Vector3(r.Byte(), r.Byte(), r.Byte()) / 255f;
                s.FogDensity = r.Float(fb);
            }

            int lightCount = r.Byte();
            for (int li = 0; li < lightCount; li++)
            {
                r.Byte();                    // light id
                int type = r.Byte();         // 0 sun, 1 point, 2 spot
                var l = new LightDef
                {
                    Ambient = new Vector3(r.Byte(), r.Byte(), r.Byte()) / 255f,
                    Color = new Vector3(r.Byte(), r.Byte(), r.Byte()) / 255f,
                    Position = new Vector4(r.Float(fb), r.Float(fb), r.Float(fb), 0)
                };
                if (type >= 1)
                {
                    l.Position.W = 1;
                    l.ConstAtt = r.Float(fb);
                    l.LinearAtt = r.Float(fb);
                    l.QuadAtt = r.Float(fb);
                }
                if (type == 2)
                {
                    l.SpotDir = new Vector3(r.Float(fb), r.Float(fb), r.Float(fb));
                    l.SpotExponent = r.Float(fb);
                    l.SpotCutoff = r.Float(fb);
                }
                s.Lights.Add(l);
            }

            int objCount = r.Word();
            for (int oi = 0; oi < objCount; oi++)
            {
                var o = new SceneObject
                {
                    Data = (uint)(r.Byte() | (r.Byte() << 8) | (r.Byte() << 16) | (r.Byte() << 24)),
                    Number = r.Word(),
                    Parent = r.Word()
                };
                bool map = o.Primitive == Prim.Map;

                if (o.Textured || map) o.Texture = r.Byte();
                if (o.HasRed) o.Color[0] = r.Byte() / 255f;
                if (o.HasGreen) o.Color[1] = r.Byte() / 255f;
                if (o.HasBlue) o.Color[2] = r.Byte() / 255f;
                if (o.HasAlpha) o.Color[3] = r.Byte() / 255f;
                if (o.AlphaMap1Set) r.Byte();
                if (o.Material2Set) o.Envmap = r.Byte();
                if (o.TexXSet || map) o.TexXScale = r.Byte();
                if (o.TexYSet || map) o.TexYScale = r.Byte();
                if (o.TexOffXSet || map) o.TexXOffset = r.Byte();
                if (o.TexOffYSet || map) o.TexYOffset = r.Byte();

                switch (o.Primitive)
                {
                    case Prim.Grid:
                    case Prim.Hasab:
                    case Prim.Cone:
                    case Prim.Sphere:
                        o.Params[0] = r.Byte();
                        o.Params[1] = r.Byte();
                        break;
                    case Prim.Arc:
                        o.Params[0] = r.Byte();
                        o.Params[1] = r.Word();
                        break;
                    case Prim.Line:
                        o.Params[0] = r.Byte();
                        break;
                    case Prim.Loft:
                        o.Params[0] = r.Word();
                        o.Params[1] = r.Word();
                        break;
                    case Prim.Map:
                        o.MapKind = r.Byte();
                        o.Envmap = o.MapKind;
                        o.MapVec = new Vector3(r.Float(fb), r.Float(fb), r.Float(fb));
                        if (o.MapKind == 2) o.MapAngle = r.Float(fb);
                        o.Selection = r.Selection();
                        break;
                }
                if (o.Primitive == Prim.Hasab) o.Params[2] = r.Byte();

                switch (o.Primitive)
                {
                    case Prim.Box:
                    case Prim.Sphere:
                    case Prim.Hasab:
                    case Prim.Cone:
                    case Prim.Arc:
                    case Prim.Loft:
                    case Prim.Line:
                    case Prim.Grid:
                    case Prim.Clone:
                        for (int x = 0; x < 3; x++)
                            for (int y = 0; y < 4; y++)
                                o.Xform.M[x, y] = r.Float(fb);
                        if (o.Primitive == Prim.Clone) o.Selection = r.Selection();
                        break;
                }
                s.Objects.Add(o);
            }

            // Cameras: keyframes of eye, target, field of view and roll
            int camCount = r.Byte();
            for (int ci = 0; ci < camCount; ci++)
            {
                var c = new CameraDef { Number = r.Byte() };
                int keys = r.Byte();
                c.KeyCount = keys;
                var tr = new Track[8];
                for (int i = 0; i < 8; i++) tr[i] = new Track(keys);
                if (keys > 0)
                {
                    int frame = r.Word();
                    float fov = r.Byte(), roll = r.Word();
                    float ex = r.Float(fb), ey = r.Float(fb), ez = r.Float(fb);
                    float tx = r.Float(fb), ty = r.Float(fb), tz = r.Float(fb);
                    for (int k = 0; k < keys; k++)
                    {
                        if (k > 0)
                        {
                            // Later keys only store the values that changed; a bit mask says which.
                            frame = r.Word();
                            int mask = r.Byte();
                            if ((mask & 1) != 0) fov = r.Byte();
                            if ((mask & 2) != 0) roll = r.Word();
                            if ((mask & 4) != 0) ex = r.Float(fb);
                            if ((mask & 8) != 0) ey = r.Float(fb);
                            if ((mask & 16) != 0) ez = r.Float(fb);
                            if ((mask & 32) != 0) tx = r.Float(fb);
                            if ((mask & 64) != 0) ty = r.Float(fb);
                            if ((mask & 128) != 0) tz = r.Float(fb);
                        }
                        float[] v = [ex, ey, ez, tx, ty, tz, fov, roll];
                        for (int i = 0; i < 8; i++) tr[i].Set(k, frame, v[i]);
                    }
                    foreach (var t in tr) t.InitVectors();
                }
                c.EyeX = tr[0];
                c.EyeY = tr[1];
                c.EyeZ = tr[2];
                c.TargetX = tr[3];
                c.TargetY = tr[4];
                c.TargetZ = tr[5];
                c.Fov = tr[6];
                c.Roll = tr[7];
                s.Cameras.Add(c);
            }

            // Object Animations
            int animCount = r.Byte();
            for (int ai = 0; ai < animCount; ai++)
            {
                int animId = r.Byte();
                foreach (var o in s.Objects)
                {
                    var anim = new ObjAnim();
                    o.Anims[animId] = anim;
                    if (o.Primitive is Prim.Arc or Prim.Line or >= 100) continue;

                    int keys = r.Byte();
                    for (int i = 0; i < 14; i++) anim.T[i] = new Track(keys);
                    if (keys == 0) continue;
                    anim.HasKeys = true;

                    var v = new float[14];
                    int frame = r.UWord();
                    for (int i = 0; i < 6; i++) v[i] = r.Float(fb);
                    v[6] = r.Word();
                    for (int i = 7; i < 10; i++) v[i] = r.Float(fb);
                    for (int i = 10; i < 14; i++) v[i] = r.Byte() / 255f;
                    for (int k = 0; k < keys; k++)
                    {
                        if (k > 0)
                        {
                            frame = r.Word();
                            int mask = r.UWord();
                            for (int i = 0; i < 14; i++)
                            {
                                if ((mask & (1 << i)) == 0) continue;
                                if (i == 6) v[i] = r.Word();
                                else if (i >= 10) v[i] = r.Byte() / 255f;
                                else v[i] = r.Float(fb);
                            }
                        }
                        for (int i = 0; i < 14; i++) anim.T[i].Set(k, frame, v[i]);
                    }
                    foreach (var t in anim.T) t.InitVectors();
                }
            }
            p.Scenes.Add(s);
        }

        // Timeline
        int eventCount = r.Word();
        for (int ei = 0; ei < eventCount; ei++)
        {
            var e = new TimelineEvent
            {
                Type = (EventType)r.Byte(),
                Start = r.UWord() * 10,
                End = (r.UWord() * 10) + 9,
                Pass = r.Byte()
            };
            if (e.Type != EventType.Clear)
            {
                for (int i = 0; i < 4; i++) e.StartRect[i] = r.Word();
                for (int i = 0; i < 4; i++) e.EndRect[i] = r.Word();
            }
            if (e.Type is EventType.Layer2D or EventType.Feedback or EventType.GridEffect)
            {
                for (int i = 0; i < 4; i++) e.StartCol[i] = r.Byte() / 255f;
                for (int i = 0; i < 4; i++) e.EndCol[i] = r.Byte() / 255f;
                e.BlendSrc = r.Byte();
                e.BlendDst = r.Byte();
            }
            switch (e.Type)
            {
                case EventType.Layer2D:
                    e.Textured = r.Byte() != 0;
                    e.Texture = r.Byte();
                    break;
                case EventType.Layer3D:
                    e.SceneId = r.Byte();
                    e.CamId = r.Byte();
                    e.AnimId = r.Byte();
                    e.CamStart = r.Word();
                    e.CamEnd = r.Word();
                    e.AnimStart = r.Word();
                    e.AnimEnd = r.Word();
                    break;
                case EventType.Clear:
                    e.ClearScreen = r.Byte() != 0;
                    e.ClearZ = r.Byte() != 0;
                    break;
                case EventType.RenderToTexture:
                    e.Texture = r.Byte();
                    break;
                case EventType.Feedback:
                    e.Texture = r.Byte();
                    e.LayerCount = r.Byte();
                    e.ZoomDivisor = r.Float(fb);
                    break;
                case EventType.GridEffect:
                    e.Texture = r.Byte();
                    e.Effect = r.Byte();
                    e.GridStart = r.Float(fb);
                    e.GridEnd = r.Float(fb);
                    break;
            }
            p.Events.Add(e);
        }

        if (r.Pos != file.Length)
            throw new InvalidDataException($"Project data: read {r.Pos} of {file.Length} bytes.");
        return p;
    }

    // Was used to debug the loader

    /*
    public string Describe(int[] detailScenes)
    {
        var sb = new System.Text.StringBuilder();
        foreach (int sn in detailScenes)
        {
            SceneDef s = FindScene(sn);
            MeshGen.Generate(this, s);
            sb.AppendLine($"--- scene {sn} fog {s.FogColor * 255} {s.FogDensity}");
            foreach (var l in s.Lights) sb.AppendLine($"  light pos {l.Position} amb {l.Ambient * 255} col {l.Color * 255} att {l.ConstAtt},{l.LinearAtt},{l.QuadAtt} spot {l.SpotCutoff}");
            foreach (var o in s.Objects)
            {
                var m = o.Xform.M;
                string sel = o.Selection != null ? " sel[" + string.Join(",", o.Selection) + "]" : "";
                sb.AppendLine($"  obj {o.Number,3} prim {o.Primitive,3} par {o.Parent,3} flags {o.Data >> 8:x6} inv {o.NormalsInverted} tex {(o.Textured ? o.Texture : -1),3} env {(o.Material2Set ? o.Envmap : -1),3} col {o.Color[0]:0.00},{o.Color[1]:0.00},{o.Color[2]:0.00},{o.Color[3]:0.00} uv {o.TexXScale}x{o.TexYScale} p {o.Params[0]},{o.Params[1]},{o.Params[2]} v {o.Vertices.Count} t {o.Polygons.Count} pos {m[0, 3]:0.00},{m[1, 3]:0.00},{m[2, 3]:0.00} diag {m[0, 0]:0.00},{m[1, 1]:0.00},{m[2, 2]:0.00}{sel}" + (o.Primitive == 104 ? $" map kind {o.MapKind} vec {o.MapVec} ang {o.MapAngle} ch {o.AlphaChannel}" : ""));
            }
            foreach (var o in s.Objects)
            {
                if (o.Vertices.Count == 0 || o.Number > 32) continue;
                var mn = new Vector3(1e9f);
                var mx = new Vector3(-1e9f);
                foreach (var v in o.Vertices)
                {
                    var w = o.Xform.Point(v.D);
                    mn = Vector3.Min(mn, w);
                    mx = Vector3.Max(mx, w);
                }
                sb.AppendLine(FormattableString.Invariant($"  box {o.Number,3} prim {o.Primitive,3} x {mn.X:0.00}..{mx.X:0.00} y {mn.Y:0.00}..{mx.Y:0.00} z {mn.Z:0.00}..{mx.Z:0.00}"));
            }

            foreach (var c in s.Cameras)
                for (int f = 0; c.KeyCount > 0 && f <= 300; f += 20)
                    sb.AppendLine(FormattableString.Invariant($"  cam {c.Number} f {f,3} eye {c.EyeX.Get(f):0.00},{c.EyeY.Get(f):0.00},{c.EyeZ.Get(f):0.00} tgt {c.TargetX.Get(f):0.00},{c.TargetY.Get(f):0.00},{c.TargetZ.Get(f):0.00} fov {c.Fov.Get(f):0.0} roll {c.Roll.Get(f):0.0}"));
            foreach (var c in s.Cameras) sb.AppendLine($"  cam {c.Number} keys {c.KeyCount} frames {c.EyeX.FirstFrame}..{c.EyeX.LastFrame} fov0 {c.Fov.Get(c.EyeX.FirstFrame)}");
        }

        var usedMats = detailScenes.SelectMany(sn => FindScene(sn).Objects).SelectMany(o => new[] { o.Textured || o.Primitive == 104 ? o.Texture : -1, o.Material2Set ? o.Envmap : -1 }).Where(x => x >= 0).Distinct().OrderBy(x => x);
        foreach (int mn in usedMats)
        {
            MaterialDef m = FindMaterial(mn);
            if (m == null)
            {
                sb.AppendLine($"  material {mn}: MISSING");
                continue;
            }
            TextureDef t = FindTexture(m.Texture);
            sb.AppendLine($"  material {mn}: texture {m.Texture} layer {m.Layer} cmds [{(t == null ? "MISSING" : string.Join(" ", t.Commands.Select(c => c[0] + "@" + c[1])))}]");
        }

        sb.AppendLine($"textures {Textures.Count}, materials {Materials.Count}, scenes {Scenes.Count}, events {Events.Count}");
        foreach (var s in Scenes)
        {
            var prims = s.Objects.GroupBy(o => o.Primitive).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}");
            sb.AppendLine($"scene {s.Number}: fog={s.Fog} col={s.FogColor * 255} dens={s.FogDensity} lights={s.Lights.Count} objects={s.Objects.Count} [{string.Join(" ", prims)}] cams=[{string.Join(",", s.Cameras.Select(c => $"{c.Number}:{c.KeyCount}"))}] anims={s.Objects.FirstOrDefault()?.Anims.Count}");
        }

        foreach (var e in Events)
        {
            string extra = e.Type switch
            {
                EventType.Layer2D => $"col {C(e.StartCol)}->{C(e.EndCol)} blend {e.BlendSrc},{e.BlendDst} tex {(e.Textured ? e.Texture : -1)}",
                EventType.Layer3D => $"scene {e.SceneId} cam {e.CamId} anim {e.AnimId} camt {e.CamStart}..{e.CamEnd} animt {e.AnimStart}..{e.AnimEnd}",
                EventType.Clear => $"screen {e.ClearScreen} z {e.ClearZ}",
                EventType.RenderToTexture => $"shot {e.Texture}",
                EventType.Feedback => $"shot {e.Texture} layers {e.LayerCount} div {e.ZoomDivisor} col {C(e.StartCol)}->{C(e.EndCol)} blend {e.BlendSrc},{e.BlendDst}",
                EventType.GridEffect => $"shot {e.Texture} fx {e.Effect} t {e.GridStart}..{e.GridEnd} col {C(e.StartCol)}->{C(e.EndCol)} blend {e.BlendSrc},{e.BlendDst}",
                _ => "",
            };
            string rect = e.Type == EventType.Clear ? "" : $" rect {string.Join(",", e.StartRect)}" + (e.StartRect.SequenceEqual(e.EndRect) ? "" : $"->{string.Join(",", e.EndRect)}");
            sb.AppendLine($"{e.Start,7}..{e.End,7} p{e.Pass,-3} {e.Type,-15}{rect} {extra}");
        }
        return sb.ToString();
    }
    
    static string C(float[] c)
    {
        return $"({c[0] * 255:0},{c[1] * 255:0},{c[2] * 255:0},{c[3] * 255:0})";
    }
    */
}
