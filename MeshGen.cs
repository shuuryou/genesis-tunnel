// Port of introgen.cpp from Conspiracy's source pack. The project file contains
// primitive descriptions, not meshes, so the original generators are needed before
// Kit can extract any corridor geometry.
//
// Hasab cylinders supply straight pipes and fittings; elbows are lofts of one arc
// along another. Clone and map refer to earlier objects, which is why file order is
// significant and why this is not implemented as a collection of independent loaders.

using System.Numerics;

namespace GenesisTunnel;

static class MeshGen
{
    const float RadTheta = 0.017453292f;

    static readonly sbyte[] BoxVertices =
    [
        -5,-5,-5, 5,-5,-5, 5, 5,-5,-5, 5,-5, 0, 0,-5, 5,-5, 5, 5, 5, 5, 5, 0, 0,
        -5, 5, 5,-5,-5, 5, 0, 0, 5,-5, 0, 0, 0,-5, 0, 0, 5, 0,
    ];
    static readonly byte[] BoxPolygons =
    [
        0,1,4,1,2,4,2,3,4,3,0,4,1,5,7,5,6,7,6,2,7,2,1,7,5,9,10,9,8,10,8,6,10,6,5,10,
        9,0,11,0,3,11,3,8,11,8,9,11,9,5,12,5,1,12,1,0,12,0,9,12,3,2,13,2,6,13,6,8,13,8,3,13,
    ];
    static readonly byte[] BoxTexCoords = [0, 0, 2, 0, 1, 1, 2, 0, 2, 2, 1, 1, 2, 2, 0, 2, 1, 1, 0, 2, 0, 0, 1, 1];

    public static void Generate(Project project, SceneDef scene)
    {
        // Generated meshes stay on SceneDef. Texture lookups and modifier operations
        // can therefore refer to exactly the same objects as the original tool did.
        if (scene.Generated) return;
        scene.Generated = true;

        var done = new List<SceneObject>();   // objects created so far
        foreach (SceneObject o in scene.Objects)
        {
            done.Add(o);
            if (o.Primitive == Prim.Map)
            {
                ApplyMap(project, o, done);
                continue;
            }
            if (o.Primitive is not (Prim.Box or Prim.Sphere or Prim.Hasab or Prim.Cone or Prim.Arc
                or Prim.Loft or Prim.Line or Prim.Grid or Prim.Clone)) continue;

            CreatePrimitive(o, done);
            if (o.Primitive != Prim.Clone)
                CountTexCoords(o, o.TexXScale, o.TexYScale, o.TexXOffset, o.TexYOffset, o.SwapTextureXY, o.InvertTexX, o.InvertTexY);
            GenerateNormals(o);

            foreach (GenPolygon p in o.Polygons) p.Alpha = o.Color[3];
            if (o.Primitive != Prim.Clone)
            {
                if (o.Textured && project.FindMaterial(o.Texture) != null)
                    foreach (GenPolygon p in o.Polygons) p.TextureMaterial = o.Texture;
                if (o.Material2Set && project.FindMaterial(o.Envmap) != null)
                    foreach (GenPolygon p in o.Polygons) p.EnvMaterial = o.Envmap;
            }
        }
    }

    // Shared mesh-building helpers.

    static void AddVertex(SceneObject o, float x, float y, float z)
    {
        // Base is the position a map modifier starts from; Position is loft scratch.
        var p = new Vector3(x, y, z);
        o.Vertices.Add(new GenVertex { D = p, Base = p, Position = p });
    }

    // Keep raw and final coordinates aligned until CountTexCoords transforms them.
    static void SetLastVertexTex(SceneObject o, float u, float v)
    {
        GenVertex last = o.Vertices[^1];
        last.T = last.Dt = new Vector2(u, v);
    }

    static GenPolygon AddPolygon(SceneObject o, int v1, int v2, int v3, bool flat)
    {
        var p = new GenPolygon { V1 = v1, V2 = v2, V3 = v3, Flat = flat };
        p.T1 = p.D1 = o.Vertices[v1].T;
        p.T2 = p.D2 = o.Vertices[v2].T;
        p.T3 = p.D3 = o.Vertices[v3].T;
        o.Polygons.Add(p);
        return p;
    }

    // Several primitives use per-corner UVs that differ from their vertex UVs.
    static void SetLastPolygonTex(SceneObject o, float u1, float v1, float u2, float v2, float u3, float v3)
    {
        GenPolygon p = o.Polygons[^1];
        p.T1 = p.D1 = new Vector2(u1, v1);
        p.T2 = p.D2 = new Vector2(u2, v2);
        p.T3 = p.D3 = new Vector2(u3, v3);
    }

    // Forward references must not become visible accidentally during generation.
    static SceneObject Find(List<SceneObject> list, int number)
    {
        return list.FirstOrDefault(x => x.Number == number);
    }

    public static void GenerateNormals(SceneObject o)
    {
        // Keep the original cross-product order and inversion flags. Reversing either
        // changes the lighting even when backface culling is disabled.
        foreach (GenVertex v in o.Vertices) v.Normal = Vector3.Zero;

        foreach (GenPolygon p in o.Polygons)
        {
            Vector3 center = o.Vertices[p.V1].D;
            Vector3 v0 = o.Vertices[p.V2].D - center;
            Vector3 v1 = o.Vertices[p.V3].D - center;
            Vector3 n = Vector3.Cross(v1, v0);
            if (o.NormalsInverted ^ p.NormalInverted) n = -n;
            p.Normal = n;
        }

        foreach (GenPolygon p in o.Polygons)
        {
            o.Vertices[p.V1].Normal += p.Normal;
            o.Vertices[p.V2].Normal += p.Normal;
            o.Vertices[p.V3].Normal += p.Normal;
        }

        foreach (GenVertex v in o.Vertices)
        {
            float len = v.Normal.Length();
            v.Normal = len != 0 ? v.Normal / len : Vector3.Zero;
        }
    }

    public static void CountTexCoords(SceneObject o, int xr, int yr, int xoff, int yoff, bool swap, bool xswp, bool yswp)
    {
        // The source tool's axis naming is counter-intuitive: its X scale applies
        // to the second component before the optional swap. Preserve that mapping.
        Vector2 Fix(Vector2 d)
        {
            float v = (d.Y * xr) + (xoff / 255f);
            float u = (d.X * yr) + (yoff / 255f);
            if (xswp) v = 1 - v;
            if (yswp) u = 1 - u;
            return swap ? new Vector2(u, v) : new Vector2(v, u);
        }
        foreach (GenVertex v in o.Vertices) v.T = Fix(v.Dt);
        foreach (GenPolygon p in o.Polygons)
        {
            p.T1 = Fix(p.D1);
            p.T2 = Fix(p.D2);
            p.T3 = Fix(p.D3);
        }
    }

    // Primitive generators.

    static void CreatePrimitive(SceneObject o, List<SceneObject> done)
    {
        // Primitives are built around the origin in normalized local coordinates.
        // Their 3x4 scene matrix is applied later by Loft, Clone or Kit.Lift.
        int param1 = o.Params[0], param2 = o.Params[1], param3 = o.Params[2];
        switch (o.Primitive)
        {
            case Prim.Box:
                for (int x = 0; x < 14; x++)
                    AddVertex(o, BoxVertices[x * 3] / 10f, BoxVertices[(x * 3) + 1] / 10f, BoxVertices[(x * 3) + 2] / 10f);
                for (int x = 0; x < 24; x++)
                {
                    AddPolygon(o, BoxPolygons[x * 3], BoxPolygons[(x * 3) + 1], BoxPolygons[(x * 3) + 2], flat: true);
                    int t = x % 4 * 6;
                    SetLastPolygonTex(o, BoxTexCoords[t] / 2f, BoxTexCoords[t + 1] / 2f, BoxTexCoords[t + 2] / 2f,
                                         BoxTexCoords[t + 3] / 2f, BoxTexCoords[t + 4] / 2f, BoxTexCoords[t + 5] / 2f);
                }
                break;

            case Prim.Sphere:
                AddVertex(o, 0, -0.5f, 0);
                AddVertex(o, 0, 0.5f, 0);
                for (int xx = 1; xx <= param1 - 2; xx++)
                {
                    float theta = ((xx / (param1 - 1f)) - 0.5f) * 180f;
                    for (int yy = 0; yy < param2; yy++)
                    {
                        float phi = (float)yy / param2 * 360f;
                        AddVertex(o, MathF.Cos(theta * RadTheta) * MathF.Cos(phi * RadTheta) / 2f,
                                     MathF.Sin(theta * RadTheta) / 2f,
                                     MathF.Cos(theta * RadTheta) * MathF.Sin(phi * RadTheta) / 2f);
                        SetLastVertexTex(o, (float)xx / param1, (float)yy / param2);
                    }
                }
                for (int yy = 0; yy <= param2 - 1; yy++)
                {
                    AddPolygon(o, 0, ((yy + 1) % param2) + 2, (yy % param2) + 2, flat: false);
                    SetLastPolygonTex(o, 0, (yy + 0.5f) / param2,
                                         1f / (param1 - 2), (float)(yy + 1) / param2,
                                         1f / (param1 - 2), (float)yy / param2);
                    AddPolygon(o, 1, ((param1 - 3) * param2) + (yy % param2) + 2,
                                     ((param1 - 3) * param2) + ((yy + 1) % param2) + 2, flat: false);
                    SetLastPolygonTex(o, 1, (yy + 0.5f) / param2,
                                         1 - (1f / (param1 - 2)), (float)yy / param2,
                                         1 - (1f / (param1 - 2)), (float)(yy + 1) / param2);
                }
                for (int xx = 0; xx <= param1 - 4; xx++)
                    for (int yy = 0; yy <= param2 - 1; yy++)
                    {
                        AddPolygon(o, (xx * param2) + (yy % param2) + 2,
                                      (xx * param2) + ((yy + 1) % param2) + 2,
                                      ((xx + 1) * param2) + ((yy + 1) % param2) + 2, flat: false);
                        SetLastPolygonTex(o, (float)(xx + 1) / (param1 - 2), (float)yy / param2,
                                             (float)(xx + 1) / (param1 - 2), (float)(yy + 1) / param2,
                                             (float)(xx + 2) / (param1 - 2), (float)(yy + 1) / param2);
                        AddPolygon(o, (xx * param2) + (yy % param2) + 2,
                                      ((xx + 1) * param2) + ((yy + 1) % param2) + 2,
                                      ((xx + 1) * param2) + (yy % param2) + 2, flat: false);
                        SetLastPolygonTex(o, (float)(xx + 1) / (param1 - 2), (float)yy / param2,
                                             (float)(xx + 2) / (param1 - 2), (float)(yy + 1) / param2,
                                             (float)(xx + 2) / (param1 - 2), (float)yy / param2);
                    }
                break;

            case Prim.Hasab:
                // A cylinder along Y: param1 sides, param2 rings, param3 = closed ends.
                for (int xx = 0; xx <= param2; xx++)
                    for (int yy = 0; yy < param1; yy++)
                    {
                        AddVertex(o, MathF.Sin((float)yy / param1 * 360 * RadTheta) * 0.5f,
                                     ((float)xx / param2) - 0.5f,
                                     MathF.Cos((float)yy / param1 * 360 * RadTheta) * 0.5f);
                        SetLastVertexTex(o, (float)yy / param1, (float)xx / param2);
                    }
                for (int xx = 0; xx <= param1 - 1; xx++)
                    for (int yy = 0; yy <= param2 - 1; yy++)
                    {
                        AddPolygon(o, (yy * param1) + ((xx + 1) % param1),
                                      (yy * param1) + (xx % param1),
                                      ((yy + 1) * param1) + ((xx + 1) % param1), flat: false);
                        SetLastPolygonTex(o, (float)(xx + 1) / param1, (float)yy / param2,
                                             (float)xx / param1, (float)yy / param2,
                                             (float)(xx + 1) / param1, (float)(yy + 1) / param2);
                        AddPolygon(o, ((yy + 1) * param1) + ((xx + 1) % param1),
                                      (yy * param1) + (xx % param1),
                                      ((yy + 1) * param1) + (xx % param1), flat: false);
                        SetLastPolygonTex(o, (float)(xx + 1) / param1, (float)(yy + 1) / param2,
                                             (float)xx / param1, (float)yy / param2,
                                             (float)xx / param1, (float)(yy + 1) / param2);
                    }
                if (param3 != 0)
                {
                    AddVertex(o, 0, -0.5f, 0);
                    SetLastVertexTex(o, 0.5f, 0.5f);
                    AddVertex(o, 0, 0.5f, 0);
                    SetLastVertexTex(o, 0.5f, 0.5f);
                    for (int xx = 0; xx <= param1 - 1; xx++)
                    {
                        GenPolygon p = AddPolygon(o, o.Vertices.Count - 2, xx, (xx + 1) % param1, flat: true);
                        CapTex(o, p);
                        p = AddPolygon(o, (param1 * param2) + xx, o.Vertices.Count - 1, (param1 * param2) + ((xx + 1) % param1), flat: true);
                        CapTex(o, p);
                    }
                }
                break;

            case Prim.Cone:
                AddVertex(o, 0, -0.5f, 0);
                SetLastVertexTex(o, 0.5f, 0.5f);
                for (int yy = 0; yy <= param1 - 1; yy++)
                {
                    float s = MathF.Sin((float)yy / param1 * 360 * RadTheta), c = MathF.Cos((float)yy / param1 * 360 * RadTheta);
                    AddVertex(o, s * 0.5f, 0.5f, c * 0.5f);
                    SetLastVertexTex(o, (s * 0.5f) + 0.5f, (c * 0.5f) + 0.5f);
                }
                for (int xx = 0; xx <= param1 - 1; xx++)
                    AddPolygon(o, 0, (xx % param1) + 1, ((xx + 1) % param1) + 1, flat: false);
                if (param2 != 0)
                {
                    if (param1 != 3)
                    {
                        AddVertex(o, 0, 0.5f, 0);
                        for (int xx = 0; xx <= param1 - 1; xx++)
                            AddPolygon(o, (xx % param1) + 1, o.Vertices.Count - 1, ((xx + 1) % param1) + 1, flat: true);
                    }
                    else AddPolygon(o, 2, 1, 3, flat: true);
                }
                break;

            case Prim.Arc:
                // param1 segments covering param2 degrees of a circle of radius 0.5,
                // plus one extra point at each end so a loft knows which way the ends point.
                for (int xx = -1; xx <= param1 + 1; xx++)
                    AddVertex(o, 0.5f * MathF.Sin((float)xx / param1 * RadTheta * param2), 0,
                                 0.5f * MathF.Cos((float)xx / param1 * RadTheta * param2));
                break;

            case Prim.Line:
                for (int xx = -1; xx <= param1 + 1; xx++)
                    AddVertex(o, 0, 0, ((float)xx / param1) - 0.5f);
                break;

            case Prim.Grid:
                for (int yy = 0; yy <= param2; yy++)
                    for (int xx = 0; xx <= param1; xx++)
                    {
                        AddVertex(o, ((float)xx / param1) - 0.5f, 0, ((float)yy / param2) - 0.5f);
                        SetLastVertexTex(o, (float)xx / param1, -(float)yy / param2);
                    }
                for (int xx = 0; xx <= param1 - 1; xx++)
                    for (int yy = 0; yy <= param2 - 1; yy++)
                    {
                        AddPolygon(o, (yy * (param1 + 1)) + xx + 1, (yy * (param1 + 1)) + xx, ((yy + 1) * (param1 + 1)) + xx + 1, flat: false);
                        AddPolygon(o, ((yy + 1) * (param1 + 1)) + xx + 1, (yy * (param1 + 1)) + xx, ((yy + 1) * (param1 + 1)) + xx, flat: false);
                    }
                break;

            case Prim.Loft:
                Loft(o, Find(done, param1), Find(done, param2));
                break;

            case Prim.Clone:
                Clone(o, done);
                break;
        }
        GenerateNormals(o);
    }

    static void CapTex(SceneObject o, GenPolygon p)
    {
        // Planar XZ projection avoids the seam produced by cylindrical cap UVs.
        Vector3 a = o.Vertices[p.V1].D, b = o.Vertices[p.V2].D, c = o.Vertices[p.V3].D;
        p.T1 = p.D1 = new Vector2(a.X + 0.5f, a.Z + 0.5f);
        p.T2 = p.D2 = new Vector2(b.X + 0.5f, b.Z + 0.5f);
        p.T3 = p.D3 = new Vector2(c.X + 0.5f, c.Z + 0.5f);
    }

    static void Loft(SceneObject o, SceneObject path, SceneObject shape)
    {
        // Arc and Line include one helper point beyond either end. Those helpers
        // provide tangent directions but are not themselves swept into the mesh.
        if (path == null || shape == null) return;
        var up = new Vector3(0, -1, 0);
        int pathCount = path.Vertices.Count - 2;     // the two end helper points are not swept
        int shapeCount = shape.Vertices.Count - 2;
        if (path.Vertices.Count <= 1 || shape.Vertices.Count <= 1) return;

        int xx = 0;
        for (int xa = 1; xa < path.Vertices.Count - 1; xa++)
        {
            path.Vertices[xa - 1].Position = path.Xform.Point(path.Vertices[xa - 1].D);
            path.Vertices[xa].Position = path.Xform.Point(path.Vertices[xa].D);
            path.Vertices[xa + 1].Position = path.Xform.Point(path.Vertices[xa + 1].D);
            Vector3 a1 = Norm(path.Vertices[xa].Position - path.Vertices[xa - 1].Position);
            Vector3 a2 = Norm(path.Vertices[xa + 1].Position - path.Vertices[xa].Position);
            Vector3 dir = Norm(a1 + a2);

            Vector3 nx = Vector3.Cross(up, dir);
            Vector3 ny = Vector3.Cross(nx, dir);

            int yy = 0;
            for (int ya = 1; ya < shape.Vertices.Count - 1; ya++)
            {
                Vector3 vv = shape.Xform.Point(shape.Vertices[ya].D);
                Vector3 nv = (nx * vv.X) + (ny * vv.Z) + path.Vertices[xa].Position;
                AddVertex(o, nv.X, nv.Y, nv.Z);
                SetLastVertexTex(o, (float)xx / (pathCount - 1), (float)yy / (shapeCount - 1));
                yy++;
            }
            xx++;
        }

        for (xx = 0; xx <= shapeCount - 2; xx++)
            for (int yy = 0; yy <= pathCount - 2; yy++)
            {
                AddPolygon(o, (yy * shapeCount) + xx + 1, (yy * shapeCount) + xx, ((yy + 1) * shapeCount) + xx + 1, flat: false);
                AddPolygon(o, ((yy + 1) * shapeCount) + xx + 1, (yy * shapeCount) + xx, ((yy + 1) * shapeCount) + xx, flat: false);
            }
    }

    // Degenerate helper segments occur in legitimate project data.
    static Vector3 Norm(Vector3 v)
    {
        float l = v.Length();
        return l != 0 ? v / l : Vector3.Zero;
    }

    static void Clone(SceneObject o, List<SceneObject> done)
    {
        // A clone is a real merged mesh, not a scene-graph reference. Rebasing the
        // triangle indices and baking each source matrix reproduces that behavior.
        int index = 0;
        for (int x = 0; x < o.Selection.Length; x++)
        {
            if (index >= o.Selection.Length) break;
            SceneObject src = Find(done, o.Selection[index]);
            if (src == null) continue;      // the original never moves on from a missing object

            int first = o.Vertices.Count;
            foreach (GenVertex sv in src.Vertices)
            {
                GenVertex v = sv.Copy();
                v.Dt = sv.T;
                v.D = src.Xform.Point(sv.D);
                v.Base = v.D;
                o.Vertices.Add(v);
            }

            foreach (GenPolygon sp in src.Polygons)
            {
                GenPolygon p = sp.Copy();
                p.V1 += first;
                p.V2 += first;
                p.V3 += first;
                p.D1 = sp.T1;
                p.D2 = sp.T2;
                p.D3 = sp.T3;
                o.Polygons.Add(p);
            }
            index++;
        }
    }

    static void ApplyMap(Project project, SceneObject map, List<SceneObject> done)
    {
        // The displacement texture goes through TexGen, just as it did originally.
        // Each target temporarily uses the modifier's UV transform, then restores
        // its own material coordinates after its vertices have been moved.
        MaterialDef mat = project.FindMaterial(map.Texture);
        if (mat == null) return;
        TextureDef tex = project.FindTexture(mat.Texture);
        if (tex == null) return;
        byte[] pixels = TexGen.Generate(tex)[mat.Layer & 3];

        float Sample(Vector2 t)
        {
            int xp = (int)(t.X * 255f), yp = (int)(t.Y * 255f);
            if (!map.NormalsInverted)        // this flag doubles as "do not wrap"
            {
                xp %= 256;
                yp %= 256;
                if (xp < 0) xp += 256;
                if (yp < 0) yp += 256;
            }
            if (xp < 0 || xp >= 256 || yp < 0 || yp >= 256) return 0;
            int i = ((xp * 256) + yp) * 4;
            int b = map.AlphaChannel switch
            {
                0 => pixels[i],
                1 => pixels[i + 1],
                2 => pixels[i + 2],
                _ => (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3,
            };
            return b / 255f;
        }

        foreach (int number in map.Selection)
        {
            SceneObject o = Find(done, number);
            if (o == null) continue;
            CountTexCoords(o, map.TexXScale, map.TexYScale, map.TexXOffset, map.TexYOffset,
                           !map.SwapTextureXY, map.InvertTexX, map.InvertTexY);
            foreach (GenVertex v in o.Vertices)
            {
                float p = Sample(v.T);
                Mat m = map.MapKind switch
                {
                    0 => Mat.Translate(p * map.MapVec.X, p * map.MapVec.Y, p * map.MapVec.Z),
                    1 => Mat.Scale((p * (map.MapVec.X - 1)) + 1, (p * (map.MapVec.Y - 1)) + 1, (p * (map.MapVec.Z - 1)) + 1),
                    _ => Mat.Rotate(map.MapVec.X, map.MapVec.Y, map.MapVec.Z, p * map.MapAngle),
                };
                v.D = m.Point(v.Base);
            }

            foreach (GenVertex v in o.Vertices)
            {
                v.Base = v.D;
                v.T = v.Dt;
            }
            CountTexCoords(o, o.TexXScale, o.TexYScale, o.TexXOffset, o.TexYOffset, o.SwapTextureXY, o.InvertTexX, o.InvertTexY);
            GenerateNormals(o);
        }
    }
}
