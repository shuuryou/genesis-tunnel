// Endless corridor construction and route planning. Sections retain the original
// 38-unit length so copied layouts join predictably. Only a small window exists at
// once: a worker builds upcoming straight geometry, while the main thread accepts
// it, plans through it and bends it into world space.
//
// CenterLine owns the double-precision course. Grid records both actual surfaces
// and the larger no-fly margin around them. Tunnel uses that grid for collision
// checks, dynamic-programming route searches, fly-through ducts and crash targets.
// Keeping those consumers on one grid prevents the visible route and collision
// diagnostics from disagreeing about where the pipes are.

using System.Numerics;

namespace GenesisTunnel;

// CPU-side world position. Floats are adequate only after Player subtracts the eye.
struct V3d(double x, double y, double z)
{
    public double X = x, Y = y, Z = z;

    public static V3d operator +(V3d a, V3d b)
    {
        return new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    }

    public static V3d operator -(V3d a, V3d b)
    {
        return new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    }

    public static V3d Lerp(V3d a, V3d b, double t)
    {
        return new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));
    }

    public readonly Vector3 ToVector3()
    {
        return new((float)X, (float)Y, (float)Z);
    }
}

// Unit-spaced center-line samples and headings. The integration is simple, but bend
// and slope ease into their goals so section geometry does not show abrupt joints.
sealed class CenterLine
{
    readonly List<V3d> points = [];
    readonly List<double> headings = [];     // which way the corridor points, in radians
    readonly Settings cfg;
    readonly Random rng;
    int first;                                  // distance along the corridor of points[0]
    double turn, turnGoal, slope, slopeGoal;
    int turnLeft, slopeLeft;

    public CenterLine(Settings cfg, Random rng)
    {
        this.cfg = cfg;
        this.rng = rng;
        points.Add(default);
        headings.Add(0);
        turnLeft = 60 + rng.Next(60);           // start with a straight piece
        slopeLeft = 50 + rng.Next(60);
    }

    void ExtendTo(int distance)
    {
        // Extend on demand; World, Heading and Bend can ask for arbitrary fractional
        // distances without coordinating their own center-line updates.
        while (first + points.Count - 1 < distance)
        {
            if (--turnLeft <= 0) NextBend();
            if (--slopeLeft <= 0) NextSlope();
            // Ease towards the wanted bend/slope so curves begin and end softly.
            turn += (turnGoal - turn) * 0.06;
            slope += (slopeGoal - slope) * 0.04;
            double heading = headings[^1] + turn;
            V3d p = points[^1];
            p.X += Math.Sin(heading);
            p.Z += Math.Cos(heading);
            p.Y += slope;
            points.Add(p);
            headings.Add(heading);
        }
    }

    void NextBend()
    {
        // turnGoal is a heading change per unit, hence the reciprocal radius.
        // turnLeft is measured in those same center-line units.
        double amount = cfg.Bends / 100.0;
        if (amount <= 0)
        {
            turnGoal = 0;
            turnLeft = 1000;
            return;
        }
        if (turnGoal != 0)
        {
            // After a bend: a straight piece.
            turnGoal = 0;
            turnLeft = (int)((40 + (rng.NextDouble() * 160)) * (1.3 - amount));
        }
        else
        {
            double tightest = 300 + ((40 - 300) * amount);            // smallest bend radius allowed
            double radius = tightest * (1 + (rng.NextDouble() * 1.5));
            double angle = (15 + (rng.NextDouble() * 65)) * Math.PI / 180;
            turnGoal = (rng.Next(2) == 0 ? 1 : -1) / radius;
            turnLeft = Math.Max(20, (int)(angle * radius));
        }
    }

    void NextSlope()
    {
        double steepest = 0.35 * cfg.Slopes / 100.0;
        slopeGoal = rng.Next(2) == 0 ? 0 : ((rng.NextDouble() * 2) - 1) * steepest;
        slopeLeft = 40 + rng.Next(110);
    }

    public void Sample(double distance, out V3d point, out double heading)
    {
        // Linear interpolation is enough at one-unit spacing and, importantly, uses
        // the same samples when vertices, camera and light directions are computed.
        ExtendTo((int)distance + 2);
        double f = Math.Max(0, distance - first);
        int i = Math.Min((int)f, points.Count - 2);
        double t = f - i;
        point = V3d.Lerp(points[i], points[i + 1], t);
        heading = headings[i] + ((headings[i + 1] - headings[i]) * t);
    }

    public void ForgetBefore(int distance)
    {
        // Keep a couple of samples for interpolation and for the retained section
        // behind the camera. Absolute indexing lives in first, not in world floats.
        int n = Math.Min(distance - first, points.Count - 2);
        if (n <= 0) return;
        points.RemoveRange(0, n);
        headings.RemoveRange(0, n);
        first += n;
    }
}

// Per-section occupancy. Solid means sampled triangle coverage; Blocked is derived
// afterwards and includes camera clearance, wall margins and tiny enclosed pockets.
sealed class Grid
{
    public const int NX = 42, NY = 18, NZ = 76;
    public const float Cell = 0.2f, Slice = 0.5f;
    const float Left = -4.2f, Top = -1.8f;

    public readonly byte[] Solid = new byte[NX * NY * NZ];     // a surface passes through here
    public readonly byte[] Blocked = new byte[NX * NY * NZ];   // too close to something for the camera

    public static int Index(int ix, int iy, int iz)
    {
        return (((iz * NY) + iy) * NX) + ix;
    }

    public static float X(int ix)
    {
        return Left + ((ix + 0.5f) * Cell);
    }

    public static float Y(int iy)
    {
        return Top + ((iy + 0.5f) * Cell);
    }

    public static int IX(float x)
    {
        return (int)MathF.Floor((x - Left) / Cell);
    }

    public static int IY(float y)
    {
        return (int)MathF.Floor((y - Top) / Cell);
    }

    // Test without modifying the grid, allowing a candidate part to be discarded.
    public bool Collides(Soup soup)
    {
        return Visit(soup.Paint, false) || Visit(soup.Plate, false);
    }

    public void Add(Soup soup)
    {
        // Commit geometry and occupancy together; a part must never be visible but
        // absent from subsequent collision and route tests.
        Visit(soup.Paint, true);
        Visit(soup.Plate, true);
    }

    bool Visit(List<Vtx> tris, bool mark)
    {
        // This is conservative sampling rather than exact triangle/box intersection.
        // The 0.1 sample spacing is finer than a cross-section cell and keeps the
        // section build inexpensive enough to run on the worker.
        const float step = 0.1f;
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            Vector3 a = tris[i].P, e1 = tris[i + 1].P - a, e2 = tris[i + 2].P - a;
            int n1 = Math.Max(1, (int)MathF.Ceiling(e1.Length() / step));
            int n2 = Math.Max(1, (int)MathF.Ceiling(e2.Length() / step));
            for (int u = 0; u <= n1; u++)
            {
                float fu = (float)u / n1;
                for (int v = 0; v <= n2; v++)
                {
                    float fv = (float)v / n2;
                    if (fu + fv > 1.0001f) break;
                    Vector3 p = a + (e1 * fu) + (e2 * fv);
                    int ix = IX(p.X), iy = IY(p.Y), iz = (int)MathF.Floor((p.Z + Kit.HalfLength) / Slice);
                    if (ix < 0 || ix >= NX || iy < 0 || iy >= NY || iz < 0 || iz >= NZ) continue;   // inside a wall
                    int index = Index(ix, iy, iz);
                    if (mark) Solid[index] = 1;
                    else if (Solid[index] != 0) return true;
                }
            }
        }
        return false;
    }

    public void Finish()
    {
        // First seal small empty regions so the planner does not route into an
        // ordinary pipe. A duct's interior is much larger and survives this pass.
        // Then dilate surfaces into a diamond-shaped clearance margin and mark walls.
        byte[] filled = (byte[])Solid.Clone();
        var queue = new int[NX * NY];
        var region = new List<int>();
        for (int iz = 0; iz < NZ; iz++)
        {
            int sliceBase = iz * NY * NX;
            var seen = new bool[NX * NY];
            for (int start = 0; start < NX * NY; start++)
            {
                if (seen[start] || filled[sliceBase + start] != 0) continue;
                // Flood-fill this patch of empty boxes.
                region.Clear();
                int head = 0, tail = 0;
                queue[tail++] = start;
                seen[start] = true;
                while (head < tail)
                {
                    int c = queue[head++];
                    region.Add(c);
                    int cx = c % NX, cy = c / NX;
                    void Try(int x, int y)
                    {
                        if (x < 0 || x >= NX || y < 0 || y >= NY) return;
                        int n = (y * NX) + x;
                        if (seen[n] || filled[sliceBase + n] != 0) return;
                        seen[n] = true;
                        queue[tail++] = n;
                    }
                    Try(cx - 1, cy);
                    Try(cx + 1, cy);
                    Try(cx, cy - 1);
                    Try(cx, cy + 1);
                }
                if (region.Count < 40)
                    foreach (int c in region) filled[sliceBase + c] = 1;
            }
        }

        Array.Clear(Blocked);
        for (int iz = 0; iz < NZ; iz++)
            for (int iy = 0; iy < NY; iy++)
                for (int ix = 0; ix < NX; ix++)
                {
                    if (ix < 2 || ix >= NX - 2 || iy < 2 || iy >= NY - 2) Blocked[Index(ix, iy, iz)] = 1;
                    if (filled[Index(ix, iy, iz)] == 0) continue;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dy = -3; dy <= 3; dy++)
                            for (int dx = -3; dx <= 3; dx++)
                            {
                                if (Math.Abs(dx) + Math.Abs(dy) > 4) continue;
                                int x = ix + dx, y = iy + dy, z = iz + dz;
                                if (x < 0 || x >= NX || y < 0 || y >= NY || z < 0 || z >= NZ) continue;
                                Blocked[Index(x, y, z)] = 1;
                            }
                }
    }
}

// Continuity contract between neighboring generated sections.
readonly record struct Lane(float X, float Y, float R);

// Local-space, unindexed render data. Positions are already bent; Soup is discarded.
sealed class ChunkMesh
{
    public int Material;
    public Vector3[] Positions, Normals;
    public Vector2[] TexCoords;
    public Vector3[] Tints;
}

// A section's build-time and render-time state. Gpu is opaque here so Tunnel does
// not acquire a Raylib dependency; Player owns and clears it through Dropped.
sealed class Chunk
{
    public int Number;
    public Soup Soup = new(); // its triangles in straight corridor coordinates
    public Grid Grid = new();
    public List<Lane> LanesOut = []; // the pipes that continue into the next section
    public V3d Anchor; // world position the meshes are relative to
    public List<ChunkMesh> Meshes = [];
    public object Gpu; // the renderer keeps its uploaded meshes here
    public (double From, double To, float X, float Y)? FlyThrough; // a big pipe the camera should fly through
}

sealed class Tunnel
{
    public const float ChunkLength = 2 * Kit.HalfLength;
    const int Ahead = 4; // sections kept ready in front of the camera

    readonly Kit kit;
    readonly Settings cfg;
    readonly int seed;
    readonly CenterLine line;
    readonly Dictionary<int, Chunk> chunks = [];
    int lastChunk = -1;
    List<Lane> lanes;

    // Player empties this list after unloading the corresponding GPU resources.
    public readonly List<Chunk> Dropped = [];

    // Raw route cells, one per half-unit slice. Only the newest section remains
    // replannable; older slices are frozen so they cannot move beneath the camera.
    readonly List<Vector2> route = [];
    int routeFirst; // slice number of route[0]
    int settled = -1; // slices up to here will not be re-planned
    readonly List<(double S, float X, float Y)> wishes = []; // places the camera would like to visit
    readonly List<(double From, double To, float X, float Y)> flyThroughs = []; // big pipes to fly through
    double wishedUpTo;
    readonly Random wishRng;

    // Where the route had to be forced through a section no safe route could cross.
    // A negative forcedTo means the route ahead is safe throughout.
    double forcedFrom, forcedTo = -1;

    public Tunnel(Kit kit, Settings cfg, int seed)
    {
        // Independent random streams make center line, wishes and section hashes
        // reproducible without coupling them to the number of worker retries.
        this.kit = kit;
        this.cfg = cfg;
        this.seed = seed;
        line = new CenterLine(cfg, new Random(seed ^ 0x5eed));
        wishRng = new Random(seed ^ 0x77);
        lanes = RequiredLanes(0) ?? [new(-2.64f, 1.18f, 0.5f)];
        for (int i = 0; i <= Ahead; i++) GenerateNext();
    }

    // Center-line queries and lifetime of the section window.

    public V3d World(double s, float x, float y)
    {
        // X follows the heading's right vector; Y remains world-down even on slopes.
        // This matches the way section vertices are later bent in Bend.
        line.Sample(s, out V3d p, out double heading);
        return new V3d(p.X + (Math.Cos(heading) * x), p.Y + y, p.Z - (Math.Sin(heading) * x));
    }

    // Flight uses this to carry the original directional lights around bends.
    public double Heading(double s)
    {
        line.Sample(s, out _, out double heading);
        return heading;
    }

    public void Update(double cameraS)
    {
        // GenerateNext may accept a worker-built section or synchronously retry it.
        // Retire only sections more than one behind, since fog and the crash restart
        // can still refer to nearby geometry.
        int camChunk = (int)(cameraS / ChunkLength);
        while (lastChunk < camChunk + Ahead) GenerateNext();

        foreach (int k in chunks.Keys.Where(k => k < camChunk - 1).ToList())
        {
            // Only a section the renderer has uploaded needs handing back. Queuing the
            // others would keep their grids and vertex arrays alive until the next
            // rendered frame.
            if (chunks[k].Gpu != null)
                Dropped.Add(chunks[k]);

            chunks.Remove(k);
        }

        int firstSlice = (camChunk - 1) * Grid.NZ;
        if (firstSlice > routeFirst && firstSlice - routeFirst < route.Count)
        {
            route.RemoveRange(0, firstSlice - routeFirst);
            routeFirst = firstSlice;
        }
        wishes.RemoveAll(w => w.S < cameraS - 80);
        flyThroughs.RemoveAll(f => f.To < cameraS - 80);
        line.ForgetBefore((int)((camChunk - 1) * ChunkLength) - 4);
    }

    public IEnumerable<Chunk> Visible(double cameraS)
    {
        // Missing entries are harmless during a build retry; fog hides the far end.
        int camChunk = (int)(cameraS / ChunkLength);
        for (int k = camChunk; k <= camChunk + 3; k++)
            if (chunks.TryGetValue(k, out Chunk c)) yield return c;
    }

    // Seeded section selection, background construction and layout generation.

    static int Hash(int a, int b, int c)
    {
        // Hashing makes a section independent of build timing and retry order. The
        // attempt number is included by Build's seed, not by these layout decisions.
        unchecked
        {
            uint h = ((uint)a * 73856093u) ^ ((uint)b * 19349663u) ^ ((uint)c * 83492791u);
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            h ^= h >> 15;
            return (int)(h & 0x7fffffff);
        }
    }

    // The opening stays recognizable unless original sections are disabled entirely.
    bool IsOriginal(int k)
    {
        return cfg.OriginalSections > 0 && (k == 0 || Hash(seed, k, 1) % 100 < cfg.OriginalSections);
    }

    // Mirroring is another stable property of the section, not of its build attempt.
    bool IsMirrored(int k)
    {
        return k != 0 && Hash(seed, k, 2) % 2 == 0;
    }

    // Original layouts have one full-length floor pipe; neighbors must continue it
    // at the same, possibly mirrored, X position.
    List<Lane> RequiredLanes(int k)
    {
        return IsOriginal(k) ? [new(IsMirrored(k) ? 2.64f : -2.64f, 1.18f, 0.5f)] : null;
    }

    // Building a section takes a few milliseconds, enough to make the picture stutter.
    // So the next section is always built ahead of time on a background thread; the
    // main thread only has to fit it in (plan the route, bend it) when it is needed.
    Task<Chunk> nextBuild;

    void StartNextBuild()
    {
        // Capture the input lane list and section number now. Build only touches its
        // own Chunk and the immutable Kit, so no shared route/chunk state is involved.
        int k = lastChunk + 1;
        List<Lane> lanesIn = lanes;
        nextBuild = Task.Run(() => Build(k, 0, lanesIn));
    }

    void GenerateNext()
    {
        // Planning is the acceptance test. Failed candidates are replaced in the
        // dictionary before the next attempt, and later attempts contain fewer pipes.
        int k = lastChunk + 1;
        Chunk chunk = null;
        bool planned = false;

        for (int attempt = 0; attempt < 5 && !planned; attempt++)
        {
            chunk = attempt == 0 && nextBuild != null ? nextBuild.Result : Build(k, attempt, lanes);
            chunks[k] = chunk;
            // A section is only accepted if the camera can get through it. If not, it
            // is built again with fewer pipes.
            planned = PlanRoute(k);
        }

        // Even the most bare section can be impossible to pass, because the frozen route
        // may already have committed to a gap that closes. The route must still go on
        // though. Without a route, every later section would start to plan from the same
        // dead end over a longer and longer growing stretch, and no crash could get
        // out of it.
        if (!planned)
            ForceRoute(k); // Doomed will later turn the collision into a crash.

        lanes = chunk.LanesOut;
        lastChunk = k;

        if (chunk.FlyThrough is var (from, to, x, y))
            flyThroughs.Add((from, to, x, y));

        MakeWishes(k);
        Bend(chunk);
        StartNextBuild();
    }

    Chunk Build(int k, int attempt, List<Lane> lanesIn)
    {
        // Everything mutable is local to the candidate. Grid.Finish is done on the
        // worker too, leaving only route planning and float conversion on the main thread.
        var chunk = new Chunk { Number = k };
        var rng = new Random(Hash(seed, k, 100 + attempt));
        Shapes.Walls(chunk.Soup, kit);

        if (attempt == 0 && IsOriginal(k)) BuildOriginal(chunk, IsMirrored(k));
        else BuildRandom(chunk, rng, attempt, lanesIn);
        chunk.Grid.Finish();
        return chunk;
    }

    void BuildOriginal(Chunk chunk, bool mirrored)
    {
        // Long pipes and the wall are reswept/sliced so they can bend. The remaining
        // original objects are copied by their scene numbers and subdivided as needed.
        var parts = new Soup();

        // The two long floor pipes are remade in short slices so they can follow bends.
        Shapes.Tube(parts, 1, Shapes.Line(new Vector3(-2.64f, 1.18f, -19), new Vector3(-2.64f, 1.18f, 19)), 0.5f, perLength: 9.5f);
        Shapes.Tube(parts, 1, Shapes.Line(new Vector3(-1.17f, 1.18f, -14.06f), new Vector3(-1.17f, 1.18f, 14.06f)), 0.5f, perLength: 9.4f);

        foreach (int n in new[] { 4, 5, 6, 7, 11, 12, 13, 14, 15, 16, 18, 19, 20, 21, 22, 23, 24, 25, 26, 28, 29, 30, 31, 32 })
            if (kit.Obj[n] != null) parts.Append(kit.Obj[n]);

        parts.Subdivide(3f);

        Place place = Place.Shift(Vector3.Zero);

        if (mirrored)
            place.Ex = -Vector3.UnitX;

        Soup placed = parts.Placed(place);

        chunk.Soup.Append(placed);
        chunk.Grid.Add(placed);
        chunk.LanesOut = RequiredLanes(chunk.Number);
    }

    // Where long pipes are allowed to run (on the floor, under the ceiling, along the walls, in mid-air)
    static readonly Lane[] Slots =
    [
        new(-2.64f, 1.18f, 0.5f), new(-1.17f, 1.18f, 0.5f), new(0f, 1.18f, 0.5f), new(1.17f, 1.18f, 0.5f), new(2.64f, 1.18f, 0.5f),
        new(-2.64f, -1.18f, 0.5f), new(-1.17f, -1.18f, 0.5f), new(0f, -1.18f, 0.5f), new(1.17f, -1.18f, 0.5f), new(2.64f, -1.18f, 0.5f),
        new(-3.6f, -0.45f, 0.35f), new(-3.6f, 0.45f, 0.35f), new(3.6f, -0.45f, 0.35f), new(3.6f, 0.45f, 0.35f),
        new(-1.7f, -0.1f, 0.35f), new(1.7f, 0.1f, 0.35f), new(-2.0f, 0f, 0.5f), new(2.0f, 0f, 0.5f),
    ];

    static bool Clash(Lane a, Lane b)
    {
        return Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y)) < a.R + b.R + 0.25f;
    }

    static float Between(Random rng, float a, float b)
    {
        return a + ((float)rng.NextDouble() * (b - a));
    }

    static bool Chance(Random rng, double p)
    {
        return rng.NextDouble() < p;
    }

    void BuildRandom(Chunk chunk, Random rng, int attempt, List<Lane> lanes)
    {
        // Lay down continuous pipes first; everything else must avoid their occupied
        // cells. Density also falls on retries, giving the route planner progressively
        // easier candidates without changing the tunnel seed.
        int k = chunk.Number;
        bool bare = attempt >= 3; // last resort: nothing but the pipes that were already there

        // How busy this section is: the setting, a slow drift along the tunnel, less on every retry.
        float drift = 0.55f + (0.75f * (Hash(seed, k / 3, 3) % 1000 / 1000f));
        float density = Math.Clamp(cfg.PipeDensity / 100f * drift * (1f - (0.3f * attempt)), 0f, 1.3f);

        // Which long pipes end here, carry on, or begin here
        List<Lane> required = bare ? null : RequiredLanes(k + 1);
        var through = new List<Lane>();
        var ending = new List<Lane>();
        var starting = new List<Lane>();

        foreach (Lane lane in lanes)
        {
            bool keep = bare || (required != null ? required.Contains(lane) : Chance(rng, 0.7));
            (keep ? through : ending).Add(lane);
        }

        if (required != null)
        {
            foreach (Lane lane in required)
                if (!through.Contains(lane)) starting.Add(lane);
        }
        else if (!bare)
        {
            int wanted = Math.Clamp((int)MathF.Round((density * 6f) + Between(rng, -1f, 1f)), 1, 7);

            for (int tries = 0; tries < 30 && through.Count + starting.Count < wanted; tries++)
            {
                Lane slot = Slots[rng.Next(Slots.Length)];

                if (lanes.Concat(starting).Any(l => Clash(l, slot)))
                    continue;

                starting.Add(slot);
            }
        }
        chunk.LanesOut = [.. through, .. starting];

        foreach (Lane lane in through)
        {
            var pipe = new Soup();
            Shapes.Tube(pipe, 1, Shapes.Line(new Vector3(lane.X, lane.Y, -19), new Vector3(lane.X, lane.Y, 19)), lane.R, perLength: 9.5f);

            // Hexagonal collars here and there, as on the original pipes. Not on the
            // last-resort section: a collar can close the one gap the route is in.
            for (int i = bare ? 0 : rng.Next(3); i > 0; i--)
                pipe.Append(kit.Collar, Place.Along(new Vector3(lane.X, lane.Y, Between(rng, -17, 17)), Vector3.UnitZ, lane.R / 0.5f));
            Commit(chunk, pipe);
        }

        foreach (Lane lane in ending)
            Commit(chunk, LaneEnd(chunk, lane, rng, false));

        foreach (Lane lane in starting)
            Commit(chunk, LaneEnd(chunk, lane, rng, true));

        if (bare)
            return;

        // A big pipe to fly through (my favorite effect)
        if (Chance(rng, cfg.BigPipes / 100.0 * 0.55))
        {
            float length = Between(rng, 12, 28);
            float z0 = Between(rng, -17, 17 - length);

            foreach (float x in new[] { -2.4f, -1.6f, -0.8f, 0f, 0.8f, 1.6f, 2.4f }.OrderBy(_ => rng.Next()))
            {
                var duct = new Soup();
                Shapes.Duct(duct, x, 0.25f, z0, z0 + length, 1.45f);
                if (chunk.Grid.Collides(duct)) continue;
                Commit(chunk, duct);
                double s0 = (k * ChunkLength) + Kit.HalfLength + z0;
                if (Chance(rng, 0.8)) chunk.FlyThrough = (s0, s0 + length, x, 0.25f);
                break;
            }
        }

        // Groups of parts taken from the original scene, moved and mirrored
        (int[] Objects, double Odds)[] groups =
        [
            (new[] { 26, 28, 29 }, 0.5),          // three bent stubs
            (new[] { 22, 23, 24, 25 }, 0.45),     // three pipes across the corridor and a box
            (new[] { 19 }, 0.35),                 // pipe run under the ceiling
            (new[] { 20 }, 0.2),
            (new[] { 30 }, 0.3),                  // standing elbow
            (new[] { 16, 18 }, 0.3),              // riser with the tank
            (new[] { 11, 12, 13, 14, 15 }, 0.3),  // elbow into the wall with a box
        ];

        foreach (var (objects, odds) in groups)
        {
            if (!Chance(rng, odds * density * 1.3)) continue;
            var group = new Soup();
            foreach (int n in objects) group.Append(kit.Obj[n]);
            group.Bounds(out Vector3 min, out Vector3 max);
            Place place = Place.Shift(new Vector3(0, 0, Between(rng, -18.5f - min.Z, 18.5f - max.Z)));
            if (Chance(rng, 0.5)) place.Ex = -Vector3.UnitX;       // mirror left-right
            if (Chance(rng, 0.5)) place.Ey = -Vector3.UnitY;       // floor becomes ceiling
            Soup placed = group.Placed(place);
            if (chunk.Grid.Collides(placed)) continue;
            placed.Subdivide(3f);
            Commit(chunk, placed);
        }

        // Pipes that take a random path through the corridor
        int wild = (int)MathF.Round(density * Between(rng, 1.5f, 5.5f));
        for (int i = 0, tries = 0; i < wild && tries < wild * 6; tries++)
            if (WildPipe(chunk, rng)) i++;
    }

    // No candidate is complete until both representations contain it.
    static void Commit(Chunk chunk, Soup part)
    {
        chunk.Soup.Append(part);
        chunk.Grid.Add(part);
    }

    // Thinner pipes need tighter elbows to fit the same corridor details.
    static float BendFor(float radius)
    {
        return radius >= 0.45f ? 2.13f : radius * 3.4f;
    }

    // Kit.Flange is centerd, Z-aligned and sized for the original radius-0.5 pipe.
    void AddFlange(Soup soup, Vector3 at, Vector3 direction, float pipeRadius)
    {
        soup.Append(kit.Flange, Place.Along(at, direction, pipeRadius / 0.5f));
    }

    Soup LaneEnd(Chunk chunk, Lane lane, Random rng, bool isStart)
    {
        // Try visually different terminations until one avoids existing geometry.
        // A start reuses the same construction mirrored in Z, preserving continuity.
        float zc = Between(rng, -10, 9);
        var from = new Vector3(lane.X, lane.Y, -19);
        var corner = new Vector3(lane.X, lane.Y, zc);
        Place mirror = Place.Shift(Vector3.Zero);
        if (isStart) mirror.Ez = -Vector3.UnitZ;

        for (int kind = rng.Next(3); kind < 3; kind++)
        {
            var part = new Soup();
            if (kind == 0)
            {
                // Sideways into a wall, sometimes ending in a junction box as in the original.
                float side = (lane.X > 0 ? 1 : -1) * (Chance(rng, 0.7) ? 1 : -1);
                List<PathPt> path = Shapes.Route([from, corner, new Vector3(side * 4.75f, lane.Y, zc)], BendFor(lane.R));
                Shapes.Tube(part, 1, path, lane.R, perLength: 9.5f);
                foreach (PathPt p in path.Where(p => p.Mark != 0)) AddFlange(part, p.P, p.T, lane.R);
                if (MathF.Abs(lane.Y) > 1f && Chance(rng, 0.5))
                    part.Append(kit.Box, Place.Box(new Vector3(side * 3.83f, lane.Y - (MathF.Sign(lane.Y) * 0.11f), zc), new Vector3(0.97f, 1.66f, 1.66f)));
            }
            else if (kind == 1)
            {
                // Up (or down) into the opposite surface, sometimes with the tank.
                float dir = lane.Y > 0 ? -1 : 1;
                List<PathPt> path = Shapes.Route([from, corner, new Vector3(lane.X, dir * 2.4f, zc)], BendFor(lane.R));
                Shapes.Tube(part, 1, path, lane.R, perLength: 9.5f);
                foreach (PathPt p in path.Where(p => p.Mark != 0)) AddFlange(part, p.P, p.T, lane.R);
                if (lane.R >= 0.45f && Chance(rng, 0.4))
                {
                    Place tank = Place.Shift(new Vector3(lane.X, dir * 2.11f, zc));
                    if (dir > 0) tank.Ey = -Vector3.UnitY;
                    part.Append(kit.Tank, tank);
                }
            }
            else
            {
                // Just stops, closed off with a flange.
                Shapes.Tube(part, 1, Shapes.Line(from, corner), lane.R, perLength: 9.5f);
                AddFlange(part, corner, Vector3.UnitZ, lane.R);
            }
            Soup placed = part.Placed(mirror);
            if (kind == 2 || !chunk.Grid.Collides(placed)) return placed;
        }
        return new Soup();
    }

    bool WildPipe(Chunk chunk, Random rng)
    {
        // Reject bad corner sequences before sweeping geometry, then test the swept
        // pipe against the grid. Fittings are added only after the route is accepted.
        float r = rng.Next(10) switch { < 4 => 0.5f, < 8 => 0.35f, _ => 0.25f };
        float bend = BendFor(r);
        float safeY = Kit.HalfHeight - r - 0.15f;

        Vector3 p, d;
        float z = Between(rng, -15, 15);
        switch (rng.Next(4))
        {
            case 0:
                p = new Vector3(-4.75f, Between(rng, -safeY, safeY), z);
                d = Vector3.UnitX;
                break;
            case 1:
                p = new Vector3(4.75f, Between(rng, -safeY, safeY), z);
                d = -Vector3.UnitX;
                break;
            case 2:
                p = new Vector3(Between(rng, -3.4f, 3.4f), 2.4f, z);
                d = -Vector3.UnitY;
                break;
            default:
                p = new Vector3(Between(rng, -3.4f, 3.4f), -2.4f, z);
                d = Vector3.UnitY;
                break;
        }

        var corners = new List<Vector3> { p };
        int segments = 1 + rng.Next(4);
        bool capped = false;

        for (int i = 0; i < segments; i++)
        {
            Vector3 q = p;
            if (i == segments - 1)
            {
                // The last piece runs on until it leaves the corridor (or, along the corridor, just ends).
                if (d.X != 0) q.X = d.X * 4.75f;
                else if (d.Y != 0) q.Y = d.Y * 2.4f;
                else
                {
                    q.Z = Math.Clamp(p.Z + (d.Z * Between(rng, 3, 10)), -17.5f, 17.5f);
                    capped = true;
                }
                if (Vector3.Distance(p, q) < (r * 2) + 0.8f) return false;
            }
            else
            {
                float shortest = i == 0 ? 0.6f + r + 0.9f : bend * 0.8f;
                if (d.X != 0) q.X = Between(rng, -3.3f, 3.3f);
                else if (d.Y != 0) q.Y = Between(rng, -safeY, safeY);
                else q.Z = Math.Clamp(p.Z + (d.Z * Between(rng, 2.5f, 13f)), -16.5f, 16.5f);
                if (Vector3.Dot(q - p, d) < shortest) return false;
                // Turn onto one of the two other axes.
                Vector3[] turns = d.X != 0 ? [Vector3.UnitY, Vector3.UnitZ]
                                : d.Y != 0 ? [Vector3.UnitX, Vector3.UnitZ]
                                : [Vector3.UnitX, Vector3.UnitY];
                d = turns[rng.Next(2)] * (rng.Next(2) == 0 ? 1 : -1);
            }
            corners.Add(q);
            p = q;
        }

        List<PathPt> path = Shapes.Route(corners, bend);
        var pipe = new Soup();
        int material = Chance(rng, 0.25) ? 7 : 1;
        Shapes.Tube(pipe, material, path, r, r < 0.3f ? 12 : 16, perLength: 9.5f);
        if (chunk.Grid.Collides(pipe)) return false;

        // Fittings: flanges at the elbows, where it meets the wall, and on a capped end; a collar on long runs.
        if (Chance(rng, 0.5))
            foreach (PathPt pt in path.Where(pt => pt.Mark != 0)) AddFlange(pipe, pt.P, pt.T, r);

        if (Chance(rng, 0.6))
            AddFlange(pipe, path[0].P + (path[0].T * 0.7f), path[0].T, r);

        if (capped)
            AddFlange(pipe, path[^1].P, path[^1].T, r);
        else if (Chance(rng, 0.6))
            AddFlange(pipe, path[^1].P - (path[^1].T * 0.7f), path[^1].T, r);

        for (int i = 1; i < corners.Count; i++)
            if (Vector3.Distance(corners[i - 1], corners[i]) > 6.5f && Chance(rng, 0.5))
                pipe.Append(kit.Collar, Place.Along((corners[i - 1] + corners[i]) / 2, corners[i] - corners[i - 1], r / 0.5f));

        Commit(chunk, pipe);

        return true;
    }

    void Bend(Chunk chunk)
    {
        // Every vertex samples the center line at its own Z. Store positions relative
        // to the section anchor and normals in the same local frame; absolute world
        // positions stay in doubles until Player subtracts the camera.

        double s0 = (chunk.Number * ChunkLength) + Kit.HalfLength; // where the section's Z = 0 lies

        line.Sample(s0, out chunk.Anchor, out _);

        foreach (var (material, list) in new[] { (1, chunk.Soup.Paint), (7, chunk.Soup.Plate) })
        {
            if (list.Count == 0)
                continue;

            var mesh = new ChunkMesh
            {
                Material = material,
                Positions = new Vector3[list.Count],
                Normals = new Vector3[list.Count],
                TexCoords = new Vector2[list.Count],
                Tints = new Vector3[list.Count],
            };

            for (int i = 0; i < list.Count; i++)
            {
                Vtx v = list[i];
                line.Sample(s0 + v.P.Z, out V3d center, out double heading);
                float cos = (float)Math.Cos(heading), sin = (float)Math.Sin(heading);
                var right = new Vector3(cos, 0, -sin);
                var forward = new Vector3(sin, 0, cos);
                mesh.Positions[i] = (center - chunk.Anchor).ToVector3() + (right * v.P.X) + new Vector3(0, v.P.Y, 0);
                mesh.Normals[i] = (right * v.N.X) + new Vector3(0, v.N.Y, 0) + (forward * v.N.Z);
                mesh.TexCoords[i] = v.UV;
                mesh.Tints[i] = v.Tint;
            }

            chunk.Meshes.Add(mesh);
        }

        chunk.Soup = null; // no longer needed
    }

    // Route planner: dynamic programming across half-unit slices. Moves go to the
    // same or a neighboring cell, with sideways steps restricted and penalized.
    // Cell cost is squared distance to the current wish, plus fly-through attraction.

    // All route smoothing and debug drawing use the same slice-center convention.
    static double SliceS(int slice)
    {
        return (slice + 0.5) * Grid.Slice;
    }

    bool BlockedAt(int slice, int cell)
    {
        // A missing section cannot be assumed safe, especially during a retry.
        if (!chunks.TryGetValue(slice / Grid.NZ, out Chunk c)) return true;
        return c.Grid.Blocked[(slice % Grid.NZ * Grid.NX * Grid.NY) + cell] != 0;
    }

    bool PlanRoute(int newestChunk, bool force = false)
    {
        // With force, clearance and surfaces stop being forbidden and become
        // expensive instead, so a route always exists. It avoids what it can.
        // Seed the first slice either from the existing frozen endpoint or from all
        // open cells. Later slices retain only their cheapest predecessor, and the
        // final route is recovered by following the packed move indices backwards.
        const int cells = Grid.NX * Grid.NY;
        int end = ((newestChunk + 1) * Grid.NZ) - 1;
        int start, startCell = -1;

        if (route.Count == 0)
        {
            start = routeFirst;
        }
        else
        {
            start = Math.Max(settled, routeFirst);
            Vector2 at = route[start - routeFirst];
            startCell = (Grid.IY(at.Y) * Grid.NX) + Grid.IX(at.X);
        }

        int count = end - start + 1;

        if (count < 2)
            return true;

        var back = new sbyte[count * cells];
        var prev = new float[cells];
        var cur = new float[cells];
        const float never = float.MaxValue;

        for (int c = 0; c < cells; c++)
            prev[c] = startCell >= 0 ? (c == startCell ? 0 : never) : (BlockedAt(start, c) && !force ? never : Price(start, c));

        for (int i = 1; i < count; i++)
        {
            int slice = start + i;

            Wish(SliceS(slice), out float wx, out float wy, out float pull);

            chunks.TryGetValue(slice / Grid.NZ, out Chunk chunk);

            int sliceBase = slice % Grid.NZ * cells;

            for (int iy = 0; iy < Grid.NY; iy++)
                for (int ix = 0; ix < Grid.NX; ix++)
                {
                    int c = (iy * Grid.NX) + ix;
                    cur[c] = never;

                    if (chunk == null || (!force && chunk.Grid.Blocked[sliceBase + c] != 0))
                        continue;

                    float best = never;
                    int bestMove = 0;
                    // Sideways steps are only allowed on every second slice. That limits how
                    // sharply the camera can swerve and makes it start dodging early.
                    for (int m = (slice & 1) == 0 ? 0 : 4; m < ((slice & 1) == 0 ? 9 : 5); m++)
                    {
                        int px = ix + (m % 3) - 1, py = iy + (m / 3) - 1;

                        if (px < 0 || px >= Grid.NX || py < 0 || py >= Grid.NY)
                            continue;

                        float cost = prev[(py * Grid.NX) + px];

                        if (cost == never)
                            continue;

                        if (m != 4)
                            cost += (m % 2 == 0) ? 0.30f : 0.15f; // moving sideways costs something

                        if (cost < best)
                        {
                            best = cost;
                            bestMove = m;
                        }
                    }

                    if (best == never)
                        continue;

                    if (force)
                        best += chunk.Grid.Solid[sliceBase + c] != 0 ? 60f : chunk.Grid.Blocked[sliceBase + c] != 0 ? 2f : 0f;

                    float dx = Grid.X(ix) - wx, dy = Grid.Y(iy) - wy;
                    cur[c] = best + (pull * ((dx * dx) + (dy * dy)));
                    back[(i * cells) + c] = (sbyte)bestMove;
                }

            (prev, cur) = (cur, prev);
        }

        int bestCell = -1;
        for (int c = 0; c < cells; c++)
            if (prev[c] != never && (bestCell < 0 || prev[c] < prev[bestCell])) bestCell = c;
        if (bestCell < 0) return false;

        // Walk back from the cheapest end square to read off the route.
        var found = new Vector2[count];

        for (int i = count - 1; i >= 0; i--)
        {
            int ix = bestCell % Grid.NX, iy = bestCell / Grid.NX;
            found[i] = new Vector2(Grid.X(ix), Grid.Y(iy));

            if (i == 0)
                break;

            int m = back[(i * cells) + bestCell];
            bestCell = ((iy + (m / 3) - 1) * Grid.NX) + ix + (m % 3) - 1;
        }

        if (route.Count > start - routeFirst)
            route.RemoveRange(start - routeFirst, route.Count - (start - routeFirst));

        route.AddRange(found);
        // Everything except the newest section is now final.
        settled = Math.Max(start, (newestChunk * Grid.NZ) - 1);
        return true;
    }

    void ForceRoute(int newestChunk)
    {
        double from = SliceS(route.Count == 0 ? routeFirst : Math.Max(settled, routeFirst));

        if (!PlanRoute(newestChunk, force: true))
            return;

        if (forcedTo < 0) forcedFrom = from;
        forcedTo = (newestChunk + 1) * (double)ChunkLength;
    }

    public bool Doomed(double cameraS, double reach, out double crashS)
    {
        // Only a forced stretch can run into something, so the glide path is checked
        // only there. The impact has to lie in the frozen part of the route: the
        // newest section may still be re-planned, and with luck that finds a way.
        crashS = -1;

        if (forcedTo < 0)
            return false;

        if (cameraS > forcedTo + reach)
        {
            forcedTo = -1;
            return false;
        }

        double until = Math.Min(Math.Min(cameraS + 40, forcedTo + reach), SliceS(settled) - reach);

        for (double t = Math.Max(cameraS + 0.5, forcedFrom - reach); t <= until; t += 0.25)
            if (SolidAt(t, PathAt(t, reach)))
            {
                crashS = t;
                return true;
            }

        return false;
    }

    // First-slice cost uses the same wish field as every later cell.
    float Price(int slice, int cell)
    {
        Wish(SliceS(slice), out float wx, out float wy, out float pull);
        float dx = Grid.X(cell % Grid.NX) - wx, dy = Grid.Y(cell / Grid.NX) - wy;
        return pull * ((dx * dx) + (dy * dy));
    }

    void Wish(double s, out float x, out float y, out float pull)
    {
        // Smoothstep between wish points avoids feeding a piecewise-linear zigzag
        // into the planner. A nearby duct temporarily dominates both target and cost.
        x = 0.2f;
        y = -0.4f;
        pull = 0.03f;

        for (int i = 0; i + 1 < wishes.Count; i++)
        {
            if (s < wishes[i].S || s > wishes[i + 1].S)
                continue;

            float t = (float)((s - wishes[i].S) / (wishes[i + 1].S - wishes[i].S));
            t = t * t * (3 - (2 * t));
            x = wishes[i].X + ((wishes[i + 1].X - wishes[i].X) * t);
            y = wishes[i].Y + ((wishes[i + 1].Y - wishes[i].Y) * t);
            break;
        }

        if (wishes.Count > 0 && s > wishes[^1].S)
        {
            x = wishes[^1].X;
            y = wishes[^1].Y;
        }

        // A big pipe ahead pulls the camera to its middle.
        foreach (var (From, To, X, Y) in flyThroughs)
        {
            if (s < From - 14 || s > To + 3)
                continue;

            float w = (float)Math.Clamp((s - (From - 14)) / 10.0, 0, 1);
            x += (X - x) * w;
            y += (Y - y) * w;
            pull += 1.2f * w;
        }
    }

    void MakeWishes(int k)
    {
        // Sample open cells and prefer those with nearby blockage. The route still
        // obeys clearance, but the camera gets close enough for pipes to feel large.
        float wander = cfg.Wander / 100f;
        Chunk chunk = chunks[k];
        double end = (k + 1) * ChunkLength;

        if (wishedUpTo < k * ChunkLength)
            wishedUpTo = k * ChunkLength;

        while (true)
        {
            double s = wishedUpTo + ((55 - (30 * wander)) * (0.7 + (0.7 * wishRng.NextDouble())));

            if (s >= end)
                break;

            wishedUpTo = s;

            int iz = Math.Clamp((int)((s - (k * ChunkLength)) / Grid.Slice), 0, Grid.NZ - 1);
            float bestScore = -1, bx = 0.2f, by = -0.4f;

            for (int i = 0; i < 40; i++)
            {
                int ix = 2 + wishRng.Next(Grid.NX - 4), iy = 2 + wishRng.Next(Grid.NY - 4);

                if (chunk.Grid.Blocked[Grid.Index(ix, iy, iz)] != 0)
                    continue;

                float score = (float)wishRng.NextDouble();

                // Prefer squares that have something close by: skimming pipes looks best.
                for (int d = 1; d <= 3; d++)
                    if (chunk.Grid.Blocked[Grid.Index(Math.Min(ix + d, Grid.NX - 3), iy, iz)] != 0 ||
                        chunk.Grid.Blocked[Grid.Index(Math.Max(ix - d, 2), iy, iz)] != 0 ||
                        chunk.Grid.Blocked[Grid.Index(ix, Math.Min(iy + d, Grid.NY - 3), iz)] != 0 ||
                        chunk.Grid.Blocked[Grid.Index(ix, Math.Max(iy - d, 2), iz)] != 0)
                    {
                        score += 0.5f;
                        break;
                    }

                if (score > bestScore)
                {
                    bestScore = score;
                    bx = Grid.X(ix);
                    by = Grid.Y(iy);
                }
            }

            wishes.Add((s, 0.2f + ((bx - 0.2f) * wander), -0.4f + ((by + 0.4f) * wander)));
        }
    }

    public Vector2 PathAt(double s, double reach = 8.0)
    {
        // Raised-cosine smoothing removes cell steps without introducing the large
        // overshoot a spline could produce near pipes. Missing old/new slices clamp
        // to the available route. A crash needs no special case here: PlanCrash
        // rewrites the raw route, and the camera glides along it as it always does.
        Vector2 sum = Vector2.Zero;

        float total = 0;
        int from = (int)Math.Floor((s - reach) / Grid.Slice), to = (int)Math.Ceiling((s + reach) / Grid.Slice);

        for (int slice = from; slice <= to; slice++)
        {
            double d = Math.Abs(SliceS(slice) - s);

            if (d >= reach)
                continue;

            float w = 0.5f * (1 + (float)Math.Cos(Math.PI * d / reach));
            int i = Math.Clamp(slice - routeFirst, 0, route.Count - 1);
            sum += route[i] * w;
            total += w;
        }

        return total > 0 ? sum / total : route[Math.Clamp((int)(s / Grid.Slice) - routeFirst, 0, route.Count - 1)];
    }

    // Outside the cross-section counts as solid; outside built sections is unknown/free
    // for drawing purposes and is never treated as a valid planned route by BlockedAt.
    public int CellState(int slice, int ix, int iy)
    {
        if (ix < 0 || ix >= Grid.NX || iy < 0 || iy >= Grid.NY) return 2;
        if (slice < 0 || !chunks.TryGetValue(slice / Grid.NZ, out Chunk c)) return 0;
        int i = Grid.Index(ix, iy, slice % Grid.NZ);
        return c.Grid.Solid[i] != 0 ? 2 : c.Grid.Blocked[i] != 0 ? 1 : 0;
    }

    // Crash targets use Solid, not the inflated Blocked margin: the intent is to hit
    // the visible surface, not the clearance around it.
    bool SolidAt(double s, Vector2 p)
    {
        int slice = (int)Math.Floor(s / Grid.Slice);
        if (!chunks.TryGetValue(slice / Grid.NZ, out Chunk c)) return false;
        int ix = Grid.IX(p.X), iy = Grid.IY(p.Y);
        if (ix < 0 || ix >= Grid.NX || iy < 0 || iy >= Grid.NY) return true;
        return c.Grid.Solid[Grid.Index(ix, iy, slice % Grid.NZ)] != 0;
    }

    public bool PlanCrash(double cameraS, double reach, Random rng, out double crashS)
    {
        // A crash is a route like any other, except that it ends in a pipe. The raw
        // route is re-planned from just beyond the stretch the camera is averaging
        // right now, drifts over to a surface some way ahead under the planner's usual
        // limit on sideways steps, and then stays on that line. The camera follows
        // with its ordinary glide, so there is no separate steering to blend in.
        // crashS is where that glide path first touches something solid.
        crashS = -1;
        int reachSlices = (int)Math.Ceiling(reach / Grid.Slice);
        int first = (int)Math.Floor(cameraS / Grid.Slice) + reachSlices + 2;

        if (first < routeFirst || first - routeFirst >= route.Count)
            return false;

        Vector2 from = route[first - routeFirst];
        int fromCell = (Grid.IY(from.Y) * Grid.NX) + Grid.IX(from.X);

        for (int tries = 0; tries < 12; tries++)
        {
            // arrive: the raw route reaches the line through the target. impact: one
            // reach later, where the gliding camera has fully settled on that line.
            // last: the route is held a little longer so the average there is clean.
            int run = (int)((24 + (rng.NextDouble() * 22)) / Grid.Slice);
            int arrive = first + run, impact = arrive + reachSlices, last = impact + reachSlices + 4;

            // Stay inside the frozen part of the route; the newest section may be re-planned.
            if (last >= settled || last - routeFirst >= route.Count || !chunks.TryGetValue(impact / Grid.NZ, out Chunk c))
                continue;

            // The nearest pipe surface to where the safe route would have passed. The
            // outermost cells are the walls, floor and ceiling, which do not count.
            Vector2 here = route[impact - routeFirst];
            float best = float.MaxValue;
            int targetCell = -1;
            int iz = impact % Grid.NZ;

            for (int iy = 2; iy < Grid.NY - 2; iy++)
                for (int ix = 2; ix < Grid.NX - 2; ix++)
                {
                    if (c.Grid.Solid[Grid.Index(ix, iy, iz)] == 0)
                        continue;

                    float d = Vector2.Distance(new Vector2(Grid.X(ix), Grid.Y(iy)), here);

                    if (d < best)
                    {
                        best = d;
                        targetCell = (iy * Grid.NX) + ix;
                    }
                }

            if (best > 3.2f)
                continue;

            Vector2[] approach = Approach(first, fromCell, arrive, targetCell);

            if (approach == null)
                continue;

            var target = new Vector2(Grid.X(targetCell % Grid.NX), Grid.Y(targetCell / Grid.NX));
            int at = first - routeFirst, count = last - first + 1;
            List<Vector2> safe = route.GetRange(at, count);

            for (int i = 0; i < count; i++)
                route[at + i] = i < approach.Length ? approach[i] : target;

            // Usually that is the target, but anything the glide path clips on the way
            // is just as good a crash.
            for (double t = cameraS + 2; t <= SliceS(impact) + 1; t += 0.25)
                if (SolidAt(t, PathAt(t, reach)))
                {
                    crashS = t;
                    return true;
                }

            // The glide path missed everything. Put the safe route back.
            for (int i = 0; i < count; i++)
                route[at + i] = safe[i];
        }

        return false;
    }

    Vector2[] Approach(int first, int fromCell, int arrive, int targetCell)
    {
        // The same dynamic programme as PlanRoute, with three differences: it ends in
        // one given cell, the wish is a soft line from the start to that cell, and
        // surfaces and their clearance are expensive rather than forbidden. The route
        // therefore still goes around whatever stands between it and its target.
        const int cells = Grid.NX * Grid.NY;
        const float never = float.MaxValue;
        int count = arrive - first + 1;

        var back = new sbyte[count * cells];
        var prev = new float[cells];
        var cur = new float[cells];
        Array.Fill(prev, never);
        prev[fromCell] = 0;

        var p0 = new Vector2(Grid.X(fromCell % Grid.NX), Grid.Y(fromCell / Grid.NX));
        var p1 = new Vector2(Grid.X(targetCell % Grid.NX), Grid.Y(targetCell / Grid.NX));

        for (int i = 1; i < count; i++)
        {
            int slice = first + i;

            if (!chunks.TryGetValue(slice / Grid.NZ, out Chunk chunk))
                return null;

            int sliceBase = slice % Grid.NZ * cells;
            float t = (float)i / (count - 1);
            Vector2 wish = Vector2.Lerp(p0, p1, t * t * (3 - (2 * t)));

            for (int iy = 0; iy < Grid.NY; iy++)
                for (int ix = 0; ix < Grid.NX; ix++)
                {
                    int c = (iy * Grid.NX) + ix;
                    cur[c] = never;

                    float best = never;
                    int bestMove = 0;

                    for (int m = (slice & 1) == 0 ? 0 : 4; m < ((slice & 1) == 0 ? 9 : 5); m++)
                    {
                        int px = ix + (m % 3) - 1, py = iy + (m / 3) - 1;

                        if (px < 0 || px >= Grid.NX || py < 0 || py >= Grid.NY)
                            continue;

                        float cost = prev[(py * Grid.NX) + px];

                        if (cost == never)
                            continue;

                        if (m != 4)
                            cost += (m % 2 == 0) ? 0.30f : 0.15f;

                        if (cost < best)
                        {
                            best = cost;
                            bestMove = m;
                        }
                    }

                    if (best == never)
                        continue;

                    float obstacle = chunk.Grid.Solid[sliceBase + c] != 0 ? 40f : chunk.Grid.Blocked[sliceBase + c] != 0 ? 0.3f : 0f;
                    cur[c] = best + obstacle + (0.3f * Vector2.DistanceSquared(new Vector2(Grid.X(ix), Grid.Y(iy)), wish));
                    back[(i * cells) + c] = (sbyte)bestMove;
                }

            (prev, cur) = (cur, prev);
        }

        // Too far sideways to reach with the allowed steps.
        if (prev[targetCell] == never)
            return null;

        var found = new Vector2[count];
        int cell = targetCell;

        for (int i = count - 1; i >= 0; i--)
        {
            int ix = cell % Grid.NX, iy = cell / Grid.NX;
            found[i] = new Vector2(Grid.X(ix), Grid.Y(iy));

            if (i == 0)
                break;

            int m = back[(i * cells) + cell];
            cell = ((iy + (m / 3) - 1) * Grid.NX) + ix + (m % 3) - 1;
        }

        return found;
    }

    public double Restart(double cameraS)
    {
        // Discard the route under the white flash and start far enough ahead that
        // the impact geometry is gone. Replan all retained/new sections as one path,
        // then freeze everything up to the latest section.

        int k = (int)(cameraS / ChunkLength) + 2;

        while (lastChunk < k + 2)
            GenerateNext();

        route.Clear();
        routeFirst = k * Grid.NZ;
        settled = -1;
        forcedTo = -1;
        if (!PlanRoute(lastChunk)) ForceRoute(lastChunk);
        settled = (lastChunk * Grid.NZ) - 1;
        return (k * ChunkLength) + 0.5;
    }
}

// Shared CPU renderer for /map and the in-flight overlay. It visualizes grid state
// and the unsmoothed planner path, making clearance and crash routes visible.
static class TunnelDebug
{
    public static byte[] Picture(Func<int, int, int, int> cellState, Func<int, Vector2> route, int firstSlice, int slices, int cameraSlice, out int w, out int h)
    {
        // Collapse each 3D slice twice: any Y occupancy for the top view, then any X
        // occupancy for the side view. Draw the camera column first and the route
        // last so the diagnostic lines remain visible over surface pixels.
        const int zoom = 3;

        // Rows between the two views: padding, a one-row separator line, padding.
        const int pad = 3, gap = (pad * 2) + 1;
        int sideTop = Grid.NX + gap, rows = sideTop + Grid.NY;

        w = slices * zoom;
        h = rows * zoom;
        var px = new byte[w * h * 4];

        void Dot(int x, int y, byte r, byte g, byte b)
        {
            for (int dy = 0; dy < zoom; dy++)
                for (int dx = 0; dx < zoom; dx++)
                {
                    int i = ((((y * zoom) + dy) * slices * zoom) + (x * zoom) + dx) * 4;
                    px[i] = r;
                    px[i + 1] = g;
                    px[i + 2] = b;
                    px[i + 3] = 255;
                }
        }

        for (int s = 0; s < slices; s++)
        {
            Dot(s, Grid.NX + pad, 90, 90, 105);   // separator between top and side view

            for (int ix = 0; ix < Grid.NX; ix++)
            {
                int best = 0;
                for (int iy = 0; iy < Grid.NY; iy++) best = Math.Max(best, cellState(firstSlice + s, ix, iy) == 2 ? 2 : 0);
                if (best == 2) Dot(s, ix, 150, 150, 170);
            }

            for (int iy = 0; iy < Grid.NY; iy++)
            {
                int best = 0;
                for (int ix = 0; ix < Grid.NX; ix++) best = Math.Max(best, cellState(firstSlice + s, ix, iy) == 2 ? 2 : 0);
                if (best == 2) Dot(s, sideTop + iy, 150, 150, 170);
            }

            if (firstSlice + s == cameraSlice)
                for (int y = 0; y < rows; y++) Dot(s, y, 255, 255, 255);

            Vector2 p = route(firstSlice + s);

            int rx = Grid.IX(p.X), ry = Grid.IY(p.Y);
            bool hit = cellState(firstSlice + s, rx, ry) != 0;

            Dot(s, Math.Clamp(rx, 0, Grid.NX - 1), 255, (byte)(hit ? 255 : 40), 40);
            Dot(s, sideTop + Math.Clamp(ry, 0, Grid.NY - 1), 255, (byte)(hit ? 255 : 40), 40);
        }

        return px;
    }
}
