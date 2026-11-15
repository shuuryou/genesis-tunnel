// Reusable geometry for the tunnel builder. Everything is authored straight in
// corridor coordinates: X across, Y down, Z along the flight. Tunnel.cs bends the
// finished triangles later, which keeps placement and collision tests simple.
//
// Kit extracts the useful parts of scene 2 from the generated Project Genesis
// meshes. Shapes supplies the pieces that must be rebuilt, chiefly swept pipes and
// sliced walls, because rigid original triangles cannot follow a curved corridor.
// Soup keeps the two metal materials separate all the way to the GPU upload.

using System.Numerics;

namespace GenesisTunnel;

// CPU vertex used until a section is bent and packed into a ChunkMesh.
struct Vtx
{
    public Vector3 P, N;     // position and the direction the surface faces
    public Vector2 UV;       // where on the texture
    public Vector3 Tint;     // surface color (1,1,1 = plain)
}

// A small affine frame. Unlike a general matrix it makes the intended scale and
// mirroring explicit, and transforming normals remains inexpensive.
struct Place
{
    public Vector3 O, Ex, Ey, Ez;

    public static Place Shift(Vector3 by)
    {
        return new() { O = by, Ex = Vector3.UnitX, Ey = Vector3.UnitY, Ez = Vector3.UnitZ };
    }

    // Used both for ordinary boxes and for normalizing an original object to unit size.
    public static Place Box(Vector3 center, Vector3 size)
    {
        return new()
        { O = center, Ex = Vector3.UnitX * size.X, Ey = Vector3.UnitY * size.Y, Ez = Vector3.UnitZ * size.Z };
    }

    public static Place Along(Vector3 center, Vector3 tangent, float scale)
    {
        // Flanges and collars were modeled along Z. Build a stable frame around
        // the pipe tangent, choosing another up axis near the vertical singularity.
        Vector3 t = Vector3.Normalize(tangent);
        Vector3 side = MathF.Abs(t.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 ex = Vector3.Normalize(Vector3.Cross(side, t));
        Vector3 ey = Vector3.Cross(t, ex);
        return new Place { O = center, Ex = ex * scale, Ey = ey * scale, Ez = t * scale };
    }

    public readonly Vtx Apply(Vtx v)
    {
        // Positions use the frame directly. For these orthogonal axes, the normal's
        // inverse-transpose reduces to dividing each component by axis length squared.
        Vector3 p = v.P, n = v.N;
        v.P = O + (Ex * p.X) + (Ey * p.Y) + (Ez * p.Z);

        // Facing directions must be un-stretched, hence the division by the axis lengths squared.
        Vector3 m = (Ex * (n.X / Ex.LengthSquared())) + (Ey * (n.Y / Ey.LengthSquared())) + (Ez * (n.Z / Ez.LengthSquared()));
        float len = m.Length();
        v.N = len > 0 ? m / len : m;
        return v;
    }
}

// Unindexed triangles grouped by material. The original objects are small enough
// that preserving their triangle order is simpler and more faithful than optimizing
// them into a new mesh representation.
sealed class Soup
{
    public readonly List<Vtx> Paint = []; // the pipe/wall metal (Project Genesis material 1)
    public readonly List<Vtx> Plate = []; // the fittings metal  (Project Genesis material 7)

    public List<Vtx> Of(int material)
    {
        return material == 7 ? Plate : Paint;
    }

    public int Count => Paint.Count + Plate.Count;

    public void Tri(int material, Vtx a, Vtx b, Vtx c)
    {
        List<Vtx> l = Of(material);
        l.Add(a);
        l.Add(b);
        l.Add(c);
    }

    // Most generated surfaces are quads; keeping the diagonal consistent matters
    // once Subdivide starts splitting long edges.
    public void Quad(int material, Vtx a, Vtx b, Vtx c, Vtx d)
    {
        Tri(material, a, b, c);
        Tri(material, a, c, d);
    }

    public void Append(Soup other)
    {
        Paint.AddRange(other.Paint);
        Plate.AddRange(other.Plate);
    }

    public void Append(Soup other, Place place)
    {
        foreach (Vtx v in other.Paint) Paint.Add(place.Apply(v));
        foreach (Vtx v in other.Plate) Plate.Add(place.Apply(v));
    }

    // Placing into a new soup lets collision checks and retries reuse the original.
    public Soup Placed(Place place)
    {
        var s = new Soup();
        s.Append(this, place);
        return s;
    }

    public void Bounds(out Vector3 min, out Vector3 max)
    {
        min = new Vector3(1e9f);
        max = new Vector3(-1e9f);
        foreach (Vtx v in Paint)
        {
            min = Vector3.Min(min, v.P);
            max = Vector3.Max(max, v.P);
        }
        foreach (Vtx v in Plate)
        {
            min = Vector3.Min(min, v.P);
            max = Vector3.Max(max, v.P);
        }
    }

    // Original fittings are easier to place once their local center is the origin.
    public Soup Centerd()
    {
        Bounds(out Vector3 min, out Vector3 max);
        return Placed(Place.Shift(-(min + max) / 2));
    }

    public void Subdivide(float maxLength)
    {
        // Only Z length matters: Tunnel.Bend samples the center line using Z. Long
        // wall or copied-scene triangles would otherwise cut across a bend.
        Subdivide(Paint, maxLength);
        Subdivide(Plate, maxLength);
    }

    static void Subdivide(List<Vtx> list, float maxLength)
    {
        // Always split the longest Z edge at its midpoint. Shared edges therefore
        // get the same new vertex instead of opening cracks between neighbors.
        var todo = new Stack<(Vtx, Vtx, Vtx)>();
        var done = new List<Vtx>(list.Count);
        for (int i = 0; i + 2 < list.Count; i += 3)
        {
            todo.Push((list[i], list[i + 1], list[i + 2]));
            while (todo.Count > 0)
            {
                var (a, b, c) = todo.Pop();
                float ab = MathF.Abs(a.P.Z - b.P.Z), bc = MathF.Abs(b.P.Z - c.P.Z), ca = MathF.Abs(c.P.Z - a.P.Z);
                float longest = MathF.Max(ab, MathF.Max(bc, ca));
                if (longest <= maxLength)
                {
                    done.Add(a);
                    done.Add(b);
                    done.Add(c);
                    continue;
                }
                // Halve the longest edge. Neighboring triangles halve the shared edge at the
                // same point, so no cracks appear.
                if (longest == ab)
                {
                    Vtx m = Mid(a, b);
                    todo.Push((a, m, c));
                    todo.Push((m, b, c));
                }
                else if (longest == bc)
                {
                    Vtx m = Mid(b, c);
                    todo.Push((a, b, m));
                    todo.Push((a, m, c));
                }
                else
                {
                    Vtx m = Mid(c, a);
                    todo.Push((a, b, m));
                    todo.Push((m, b, c));
                }
            }
        }
        list.Clear();
        list.AddRange(done);
    }

    static Vtx Mid(Vtx a, Vtx b)
    {
        // Interpolate all surface attributes; averaging normals without normalizing
        // would visibly darken the middle of a curved or bevelled surface.
        Vector3 n = a.N + b.N;
        float len = n.Length();
        return new Vtx { P = (a.P + b.P) / 2, N = len > 0 ? n / len : n, UV = (a.UV + b.UV) / 2, Tint = (a.Tint + b.Tint) / 2 };
    }
}

// Scene 2 after generation and extraction. Obj retains the original object numbers
// because Tunnel's layout groups refer to those numbers directly.
sealed class Kit
{
    // A section spans -19..19 in the original scene.
    public const float HalfLength = 19f;
    public const float HalfWidth = 4.16f, HalfHeight = 1.8f;

    // Null entries are objects that are not drawable corridor geometry.
    public readonly Soup[] Obj = new Soup[36];

    // The wall is a square tube. Its four corners and which way each corner "faces".
    // (The corners face diagonally into the room, which is what gives the walls
    // their soft light-to-dark gradient.)
    public readonly Vector2[] WallCorner = new Vector2[4];
    public readonly Vector3[] WallNormal = new Vector3[4];
    public readonly Vector3 WallTint;

    // Centerd fittings. Flange and Collar fit a radius-0.5 pipe along Z and are
    // scaled with the pipe they decorate.
    public readonly Soup Flange, Collar, Box, Tank;

    public Kit(Project project)
    {
        // Mesh generation happens once per project. The transformed copies below
        // are in corridor space, so later builders never need scene matrices.
        SceneDef scene = project.FindScene(2);          // scene 2 is the pipe corridor
        MeshGen.Generate(project, scene);

        foreach (SceneObject o in scene.Objects)
        {
            if (o.Number >= Obj.Length || o.Polygons.Count == 0) continue;
            Obj[o.Number] = Lift(o);
        }

        SceneObject wall = scene.Find(1);
        Mat normalMatrix = Mat.Invert(wall.Xform);
        for (int i = 0; i < 4; i++)
        {
            Vector3 p = wall.Xform.Point(wall.Vertices[i].D);
            WallCorner[i] = new Vector2(p.X, p.Y);
            WallNormal[i] = TransformNormal(normalMatrix, wall.Vertices[i].Normal);
        }
        WallTint = new Vector3(wall.Color[0], wall.Color[1], wall.Color[2]);

        Flange = Obj[12].Centerd();
        Collar = Obj[4].Centerd();
        Tank = Obj[18].Centerd();
        // The junction box, squeezed into a 1x1x1 cube so it can be made any size.
        Obj[15].Bounds(out Vector3 min, out Vector3 max);
        Vector3 size = max - min;
        Box = Obj[15].Centerd().Placed(Place.Box(Vector3.Zero, new Vector3(1 / size.X, 1 / size.Y, 1 / size.Z)));
    }

    static Soup Lift(SceneObject o)
    {
        // Bake positions into world/scene space but retain the per-object tint and
        // flat/smooth choice. Normals need the inverse transpose, not Xform itself.
        var soup = new Soup();
        Mat normalMatrix = Mat.Invert(o.Xform);
        var tint = new Vector3(o.Color[0], o.Color[1], o.Color[2]);
        foreach (GenPolygon p in o.Polygons)
        {
            int material = p.TextureMaterial == 7 ? 7 : 1;
            Vtx Corner(int index, Vector2 uv)
            {
                return new()
                {
                    P = o.Xform.Point(o.Vertices[index].D),
                    N = TransformNormal(normalMatrix, p.Flat ? p.Normal : o.Vertices[index].Normal),
                    UV = uv,
                    Tint = tint,
                };
            }

            soup.Tri(material, Corner(p.V1, p.T1), Corner(p.V2, p.T2), Corner(p.V3, p.T3));
        }
        return soup;
    }

    // Mat stores row/column explicitly, so write out the transpose multiply rather
    // than relying on a Matrix4x4 multiplication convention.
    static Vector3 TransformNormal(Mat inverse, Vector3 n)
    {
        var r = new Vector3(
            (inverse.M[0, 0] * n.X) + (inverse.M[1, 0] * n.Y) + (inverse.M[2, 0] * n.Z),
            (inverse.M[0, 1] * n.X) + (inverse.M[1, 1] * n.Y) + (inverse.M[2, 1] * n.Z),
            (inverse.M[0, 2] * n.X) + (inverse.M[1, 2] * n.Y) + (inverse.M[2, 2] * n.Z));
        float len = r.Length();
        return len > 0 ? r / len : r;
    }
}

// Center-line sample used by the pipe sweeper. Marks identify elbow endpoints,
// which are convenient places for the original flanges.
struct PathPt
{
    public Vector3 P, T;     // position and direction of travel
    public float Length;     // distance from the start of the pipe
    public int Mark;         // 1 = an elbow starts here, 2 = an elbow ends here
}

// Procedural replacements for parts that cannot simply be copied from scene 2.
static class Shapes
{
    static readonly Vector3 White = Vector3.One;

    public static List<PathPt> Route(IList<Vector3> corners, float bend, float step = 2f, int elbowSegments = 8)
    {
        // Replace each corner by two tangent points and a circular arc. If adjacent
        // segments are short, reduce the radius rather than letting elbows overlap.
        var path = new List<PathPt>();
        void Add(Vector3 p, Vector3 t, int mark)
        {
            float length = path.Count == 0 ? 0 : path[^1].Length + Vector3.Distance(path[^1].P, p);
            path.Add(new PathPt { P = p, T = t, Length = length, Mark = mark });
        }
        void Straight(Vector3 from, Vector3 to, Vector3 dir, bool includeStart, int endMark)
        {
            float len = Vector3.Distance(from, to);
            int n = Math.Max(1, (int)MathF.Ceiling(len / step));
            for (int i = includeStart ? 0 : 1; i <= n; i++) Add(Vector3.Lerp(from, to, (float)i / n), dir, i == n ? endMark : 0);
        }

        Vector3 start = corners[0];
        bool first = true;
        for (int i = 1; i < corners.Count; i++)
        {
            Vector3 din = Vector3.Normalize(corners[i] - corners[i - 1]);
            if (i == corners.Count - 1)
            {
                Straight(start, corners[i], din, first, 0);
                break;
            }

            Vector3 dout = Vector3.Normalize(corners[i + 1] - corners[i]);
            float cos = Math.Clamp(Vector3.Dot(din, dout), -1f, 1f);
            float angle = MathF.Acos(cos);
            if (angle < 0.01f) continue;               // no real corner here

            // How far before the corner the elbow starts ("tangent length").
            float room = MathF.Min(Vector3.Distance(start, corners[i]), Vector3.Distance(corners[i], corners[i + 1]) * (i + 1 == corners.Count - 1 ? 1f : 0.5f)) - 0.05f;
            float tangent = MathF.Min(bend * MathF.Tan(angle / 2), room);
            float radius = tangent / MathF.Tan(angle / 2);
            Vector3 a = corners[i] - (din * tangent);
            Straight(start, a, din, first, 1);
            first = false;

            Vector3 inward = Vector3.Normalize(dout - din);                    // towards the elbow's center
            Vector3 center = corners[i] + (inward * (radius / MathF.Cos(angle / 2)));
            Vector3 u = (center - a) / radius;
            for (int j = 1; j <= elbowSegments; j++)
            {
                float t = angle * j / elbowSegments;
                Add(center + (radius * ((-u * MathF.Cos(t)) + (din * MathF.Sin(t)))),
                    Vector3.Normalize((din * MathF.Cos(t)) + (u * MathF.Sin(t))), j == elbowSegments ? 2 : 0);
            }
            start = path[^1].P;
        }
        return path;
    }

    public static void Tube(Soup soup, int material, List<PathPt> path, float radius, int sides = 16,
                            float around = 3f, float perLength = 9.4f, bool inward = false, Vector3? tint = null)
    {
        // Rings share vertices in concept, but the soup is unindexed so each quad
        // emits four corners. The side-by-side tangent frame is carried forward to
        // prevent the texture from spinning as an elbow changes direction.
        if (path.Count < 2) return;
        Vector3 color = tint ?? White;
        var prev = new Vtx[sides + 1];
        var ring = new Vtx[sides + 1];
        Vector3 e1 = Vector3.Zero;
        for (int i = 0; i < path.Count; i++)
        {
            Vector3 t = path[i].T;
            // Carry the "sideways" direction along the pipe so the texture does not twist.
            if (i == 0) e1 = Vector3.Cross(MathF.Abs(t.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, t);
            e1 = Vector3.Normalize(e1 - (t * Vector3.Dot(e1, t)));
            Vector3 e2 = Vector3.Cross(t, e1);
            for (int j = 0; j <= sides; j++)
            {
                float a = MathF.Tau * j / sides;
                Vector3 outward = (e1 * MathF.Cos(a)) + (e2 * MathF.Sin(a));
                ring[j] = new Vtx
                {
                    P = path[i].P + (outward * radius),
                    N = inward ? -outward : outward,
                    UV = new Vector2(around * j / sides, path[i].Length / perLength),
                    Tint = color,
                };
            }
            if (i > 0)
                for (int j = 0; j < sides; j++)
                    soup.Quad(material, prev[j], prev[j + 1], ring[j + 1], ring[j]);
            (prev, ring) = (ring, prev);
        }
    }

    // Route also supplies the cumulative lengths needed for longitudinal UVs.
    public static List<PathPt> Line(Vector3 from, Vector3 to, float step = 2f)
    {
        return Route([from, to], 1f, step);
    }

    public static void Washer(Soup soup, int material, Vector3 center, Vector3 axis, float inner, float outer, int sides = 16)
    {
        // Washers close the exposed wall thickness at duct ends and ribs.
        Place frame = Place.Along(center, axis, 1f);
        for (int j = 0; j < sides; j++)
        {
            float a0 = MathF.Tau * j / sides, a1 = MathF.Tau * (j + 1) / sides;
            Vtx V(float a, float r)
            {
                return frame.Apply(new Vtx
                {
                    P = new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0),
                    N = Vector3.UnitZ,
                    UV = (new Vector2(MathF.Cos(a) * r, MathF.Sin(a) * r) * 0.3f) + new Vector2(0.5f),
                    Tint = White,
                });
            }

            soup.Quad(material, V(a0, inner), V(a0, outer), V(a1, outer), V(a1, inner));
        }
    }

    public static void Duct(Soup soup, float x, float y, float z0, float z1, float radius)
    {
        // The inward-facing inner tube is important: the route planner leaves the
        // large interior open, and the camera can actually see its inside wall.
        const float wall = 0.1f;
        const int sides = 24;
        var a = new Vector3(x, y, z0);
        var b = new Vector3(x, y, z1);
        List<PathPt> line = Line(a, b);
        Tube(soup, 1, line, radius, sides, 6f);
        Tube(soup, 1, line, radius - wall, sides, 6f, inward: true);
        Washer(soup, 7, a, -Vector3.UnitZ, radius - wall, radius, sides);
        Washer(soup, 7, b, Vector3.UnitZ, radius - wall, radius, sides);

        // Rings: one at each end and roughly every seven units in between.
        int rings = Math.Max(2, (int)MathF.Round((z1 - z0) / 7f) + 1);
        for (int i = 0; i < rings; i++)
        {
            float z = z0 + 0.4f + ((z1 - z0 - 0.8f) * i / (rings - 1));
            var r0 = new Vector3(x, y, z - 0.25f);
            var r1 = new Vector3(x, y, z + 0.25f);
            Tube(soup, 7, Line(r0, r1), radius + 0.16f, sides, 4f, 2f);
            Washer(soup, 7, r0, -Vector3.UnitZ, radius, radius + 0.16f, sides);
            Washer(soup, 7, r1, Vector3.UnitZ, radius, radius + 0.16f, sides);
        }
    }

    public static void Walls(Soup soup, Kit kit)
    {
        // Reuse the original corner positions, diagonal normals and UV layout. The
        // slices are new; a single 38-unit quad cannot follow the center line.
        const int slices = 19;
        for (int face = 0; face < 4; face++)
        {
            int c0 = face, c1 = (face + 1) % 4;
            for (int i = 0; i < slices; i++)
            {
                float t0 = (float)i / slices, t1 = (float)(i + 1) / slices;
                // Texture as in the original: twice along the section, twice around.
                Vtx V(int corner, float t, float v)
                {
                    return new()
                    {
                        P = new Vector3(kit.WallCorner[corner].X, kit.WallCorner[corner].Y, -Kit.HalfLength + (2 * Kit.HalfLength * t)),
                        N = kit.WallNormal[corner],
                        UV = new Vector2(2f * t, v),
                        Tint = kit.WallTint,
                    };
                }

                soup.Quad(1, V(c0, t0, face * 0.5f), V(c1, t0, (face + 1) * 0.5f), V(c1, t1, (face + 1) * 0.5f), V(c0, t1, face * 0.5f));
            }
        }
    }
}
