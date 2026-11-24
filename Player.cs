// Frame renderer. The fixed Project Genesis scripted timeline is gone, but I still
// need its drawing operations. The Flight class synthesizes the remaining timeline
// events needed for swirl, zoom, feedback blur and the white flash. DrawEvent will
// execute them.
//
// The world pass uses GLSL because modern OpenGL no longer supplies the original
// fixed-function lights and fog that Project Genesis used. The post effects still
// largely do what Project Genesis did: snapshot the world, distort that snapshot
// through a grid, add zoomed copies for radial blur, then draw the flash over it.

using Raylib_cs;
using System.Numerics;

namespace GenesisTunnel;

sealed class Player
{
    // Project Genesis draws into a virtual 640x480 screen and shows everything inside
    // a 16:9 window from line 59 to line 420. We stretch that window over our picture.
    const float VirtualTop = 59f, VirtualBottom = 420f, VirtualWidth = 640f;
    public const float Aspect = VirtualWidth / (VirtualBottom - VirtualTop);

    const int GridX = 64, GridY = 48;

    readonly Project project;
    readonly int width, height;                    // size of our off-screen picture in pixels
    RenderTexture2D frame;                         // the picture being built
    readonly RenderTexture2D[] shots = new RenderTexture2D[20];   // snapshots
    readonly float[][] shotRect = new float[20][]; // x1,y1,x2,y2 as the original stored them
    readonly Dictionary<int, Texture2D> materialTextures = [];
    readonly double[,,] gridJitter = new double[GridX + 1, GridY + 1, 2];
    Texture2D white;
    Shader shader;
    Material material;
    readonly int locDiffuse, locEnvPass, locFogOn, locFogColor, locFogDensity, locLightCount,
        locLightPos, locLightAmb, locLightCol, locLightAtt, locSpotDir, locSpot;

    public RenderTexture2D Frame => frame;

    readonly float aspect; // picture width / height

    /// <summary>A section's meshes once they are on the graphics card.</summary>
    sealed class GpuChunk { public List<(Mesh Mesh, int Material)> Parts = []; }

    public Player(Project project, int width, int height, float aspect)
    {
        // All rendering, including the final 2D effects, happens in one off-screen
        // target. Program.cs can then letterbox, downsample or copy it to a preview.
        this.project = project;
        this.aspect = aspect;
        this.width = width;
        this.height = height;

        frame = Raylib.LoadRenderTexture(width, height);
        Raylib.SetTextureFilter(frame.Texture, TextureFilter.Bilinear);
        Raylib.BeginTextureMode(frame);
        Raylib.ClearBackground(Color.Black);
        Raylib.EndTextureMode();

        Image img = Raylib.GenImageColor(1, 1, Color.White);
        white = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);

        shader = Raylib.LoadShaderFromMemory(VertexShader, FragmentShader);
        locDiffuse = Raylib.GetShaderLocation(shader, "matDiffuse");
        locEnvPass = Raylib.GetShaderLocation(shader, "envPass");
        locFogOn = Raylib.GetShaderLocation(shader, "fogOn");
        locFogColor = Raylib.GetShaderLocation(shader, "fogColor");
        locFogDensity = Raylib.GetShaderLocation(shader, "fogDensity");
        locLightCount = Raylib.GetShaderLocation(shader, "lightCount");
        locLightPos = Raylib.GetShaderLocation(shader, "lightPos");
        locLightAmb = Raylib.GetShaderLocation(shader, "lightAmb");
        locLightCol = Raylib.GetShaderLocation(shader, "lightCol");
        locLightAtt = Raylib.GetShaderLocation(shader, "lightAtt");
        locSpotDir = Raylib.GetShaderLocation(shader, "lightSpotDir");
        locSpot = Raylib.GetShaderLocation(shader, "lightSpot");
        material = Raylib.LoadMaterialDefault();
        material.Shader = shader;

        // Random offsets for the "jitter" grid effects (shoplifted from Project Genesis)
        TexGen.SRand(1);
        InitGridJitter(0, 1.0f - (480.0f / 1024.0f), 640.0f / 1024.0f, 1);
    }

    public bool ShaderOk => shader.Id != 0 && locDiffuse >= 0;

    public void Unload()
    {
        // Program.cs builds a new Player whenever the picture size changes, which
        // while a window edge is being dragged is every frame. This resulted in
        // a memory leak, because there is no garbage collection in Raylib. The
        // render targets alone are tens of megabytes of video memory each. Section
        // meshes are not touched, because they belong to the sections and stay
        // valid for the next Player.
        Raylib.UnloadRenderTexture(frame);

        foreach (RenderTexture2D shot in shots)
            if (shot.Id != 0) Raylib.UnloadRenderTexture(shot);

        foreach (Texture2D tex in materialTextures.Values)
            if (tex.Id != white.Id) Raylib.UnloadTexture(tex);

        materialTextures.Clear();
        Raylib.UnloadTexture(white);
        Raylib.UnloadShader(shader);

        // Not UnloadMaterial: that would also delete the shader and whichever
        // texture was drawn last, both of which were released above.
        unsafe { Raylib.MemFree(material.Maps); }
    }

    Texture2D MaterialTexture(int number)
    {
        // TexGen output is cached by TextureDef, and this GPU texture is cached by
        // material number. Alpha is forced opaque because the corridor's material
        // colors and the screensaver framebuffer both use RGB, not transparency.

        if (materialTextures.TryGetValue(number, out Texture2D tex))
            return tex;
        tex = white;

        MaterialDef m = project.FindMaterial(number);
        TextureDef t = m != null ? project.FindTexture(m.Texture) : null;

        if (t != null)
        {
            byte[] layer = TexGen.Generate(t)[m.Layer & 3];
            byte[] rgba = (byte[])layer.Clone();

            for (int i = 3; i < rgba.Length; i += 4)
                rgba[i] = 255;

            unsafe
            {
                fixed (byte* p = rgba)
                {
                    var img = new Image { Data = p, Width = 256, Height = 256, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 };
                    tex = Raylib.LoadTextureFromImage(img);
                }
            }

            Raylib.GenTextureMipmaps(ref tex);
            Raylib.SetTextureFilter(tex, TextureFilter.Trilinear);
            Raylib.SetTextureWrap(tex, TextureWrap.Repeat);
        }

        materialTextures[number] = tex;
        return tex;
    }

    // Frame assembly and the small interpreter for Project Genesis timeline operations.
    public void Render(Look look, IEnumerable<Chunk> visible, List<Chunk> dropped)
    {
        // Free GPU meshes for sections Tunnel has removed. Tunnel only takes them
        // out of its dictionary; the Raylib objects are deleted here, on the thread
        // that owns the OpenGL context. This runs before the frame draws anything,
        // and only for sections that were already off screen, so the driver is no
        // longer reading their buffers.

        foreach (Chunk c in dropped)
            if (c.Gpu is GpuChunk old)
            {
                foreach (var part in old.Parts) Raylib.UnloadMesh(part.Mesh);
                c.Gpu = null;
            }

        dropped.Clear();

        Raylib.BeginTextureMode(frame);
        EnterFrameState();
        Rlgl.EnableDepthMask();
        // Far away everything dissolves into the haze, so the haze color is the background.
        Rlgl.ClearColor((byte)(look.FogColor.X * 255), (byte)(look.FogColor.Y * 255), (byte)(look.FogColor.Z * 255), 255);
        Rlgl.ClearScreenBuffers();

        DrawWorld(look, visible);

        if (look.Swirl > 0.001f || look.Zoom > 0.001f || look.Blur > 0.001f)
        {
            var full = new Vector4(1, 1, 1, 1);
            TakeSnapshot(0, Window[0], Window[1], Window[2] + 1, Window[3] + 1);

            // The grid effects take a "time" and turn it into a strength themselves
            // (sin squared), so the wanted strength is converted back into that time.
            if (look.Swirl > 0.001f)
                DrawEvent(Effect(EventType.GridEffect, full, One, Zero, fx: 6, amount: 2f / MathF.PI * MathF.Asin(MathF.Sqrt(look.Swirl))), 0);

            if (look.Zoom > 0.001f)
                DrawEvent(Effect(EventType.GridEffect, full * MathF.Min(1, look.Zoom * 2.5f) * 0.8f, One, One, fx: 2, amount: MathF.Asin(MathF.Sqrt(look.Zoom)) / MathF.PI), 0);

            Rlgl.DrawRenderBatchActive();

            if (look.Blur > 0.001f)
            {
                TakeSnapshot(1, Window[0], Window[1], Window[2] + 1, Window[3] + 1);
                // 23 copies; the original's zoom step is 1.148, weaker blur = bigger divisor.
                TimelineEvent blur = Effect(EventType.Feedback, full, One, OneMinusSrcAlpha);
                blur.Texture = 1;
                blur.LayerCount = 23;
                blur.ZoomDivisor = 1.1484375f / look.Blur;
                DrawEvent(blur, 0);
            }

            Rlgl.DrawRenderBatchActive();
        }

        if (look.Flash > 0.001f)
            DrawEvent(Effect(EventType.Layer2D, new Vector4(look.Flash, look.Flash, look.Flash, 0), One, One), 0);

        Rlgl.DrawRenderBatchActive();
        Rlgl.ColorMask(true, true, true, true);
        Rlgl.EnableBackfaceCulling();
        Rlgl.DisableDepthTest();
        Rlgl.EnableDepthMask();
        Rlgl.EnableColorBlend();
        Rlgl.SetBlendMode(BlendMode.Alpha);
        Raylib.EndTextureMode();
    }

    // The visible window on the 640x480 screen, and blend factor numbers as the intro stores them.
    static readonly int[] Window = [0, 59, 639, 419];

    // Blend factor numbers as Project Genesis stores them.
    const int Zero = 0, One = 1, OneMinusSrcAlpha = 5;

    static TimelineEvent Effect(EventType type, Vector4 color, int blendSrc, int blendDst, int fx = 0, float amount = 0)
    {
        // Most runtime effects are stationary full-window events. Reusing the loaded
        // TimelineEvent shape avoids a second renderer for essentially the same work.
        float[] c = [color.X, color.Y, color.Z, color.W];

        return new TimelineEvent
        {
            Type = type,
            StartRect = Window,
            EndRect = Window,
            StartCol = c,
            EndCol = c,
            BlendSrc = blendSrc,
            BlendDst = blendDst,
            Effect = fx,
            GridStart = amount,
            GridEnd = amount,
            Texture = 0,
        };
    }

    void EnterFrameState()
    {
        // Snapshot creation temporarily changes render target and color mask, so
        // this is called on every return to the main frame as well as at its start.
        // The original window had no alpha channel; alpha value 1 is the same result.

        Rlgl.ColorMask(true, true, true, false);
        Rlgl.DisableBackfaceCulling();
        Rlgl.EnableDepthTest();

        Use2D();
    }

    void Use2D()
    {
        // Keeping the old coordinate system also keeps all effect rectangles and
        // snapshot calculations comparable.
        Rlgl.DrawRenderBatchActive();
        Rlgl.Viewport(0, 0, width, height);
        Rlgl.SetMatrixProjection(Ortho(0, VirtualWidth, VirtualBottom, VirtualTop));
        Rlgl.SetMatrixModelView(Matrix4x4.Identity);
    }

    static readonly int[] BlendFactors = [0, 1, 0x300, 0x301, 0x302, 0x303, 0x304, 0x305, 0x306, 0x307, 0x308];

    static void SetBlend(int src, int dst)
    {
        // Flush before changing fixed pipeline state; rlgl may still have quads from
        // the previous event in its batch.
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableColorBlend();
        Rlgl.SetBlendFactors(src, dst, 0x8006);
        Rlgl.SetBlendMode(BlendMode.Custom);
    }

    // Timeline files store indices, not OpenGL enum values.
    static void SetEventBlend(TimelineEvent e)
    {
        SetBlend(BlendFactors[Math.Min(e.BlendSrc, 10)], BlendFactors[Math.Min(e.BlendDst, 10)]);
    }

    static float DoLerp(float a, float b, float t)
    {
        return a + ((b - a) * t);
    }

    static Vector4 EventColor(TimelineEvent e, float t)
    {
        return new(
        DoLerp(e.StartCol[0], e.EndCol[0], t), DoLerp(e.StartCol[1], e.EndCol[1], t),
        DoLerp(e.StartCol[2], e.EndCol[2], t), DoLerp(e.StartCol[3], e.EndCol[3], t));
    }

    static void SetColor(Vector4 c)
    {
        Rlgl.Color4f(
        Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1), Math.Clamp(c.Z, 0, 1), Math.Clamp(c.W, 0, 1));
    }

    void DrawEvent(TimelineEvent e, float t)
    {
        // t is retained from the timeline player even though runtime-created events
        // normally pass zero. Their start/end rectangles, colors and grid strengths
        // can therefore be interpolated without special cases.

        float x1 = MathF.Floor(DoLerp(e.StartRect[0], e.EndRect[0], t));
        float y1 = MathF.Floor(DoLerp(e.StartRect[1], e.EndRect[1], t));
        float x2 = MathF.Floor(DoLerp(e.StartRect[2], e.EndRect[2], t)) + 1;
        float y2 = MathF.Floor(DoLerp(e.StartRect[3], e.EndRect[3], t)) + 1;

        switch (e.Type)
        {
            case EventType.Clear:
                Rlgl.DrawRenderBatchActive();
                if (e.ClearZ && !e.ClearScreen)
                {
                    Rlgl.EnableDepthMask();
                    Rlgl.ColorMask(false, false, false, false);
                    Rlgl.ClearScreenBuffers();
                    Rlgl.ColorMask(true, true, true, false);
                }
                else if (!e.ClearZ && e.ClearScreen)
                {
                    Rlgl.DisableDepthMask();
                    Rlgl.ClearScreenBuffers();
                    Rlgl.EnableDepthMask();
                }
                else if (e.ClearZ && e.ClearScreen)
                {
                    Rlgl.EnableDepthMask();
                    Rlgl.ClearScreenBuffers();
                }
                break;

            case EventType.Layer2D:
                // A flat rectangle. With additive blending and a color that fades
                // from white to black this is the white flash between shots.

                Rlgl.EnableDepthTest();
                Rlgl.DisableDepthMask();

                SetEventBlend(e);

                uint tex = e.Textured ? MaterialTexture(e.Texture).Id : white.Id;

                Rlgl.SetTexture(tex);
                Rlgl.Begin(DrawMode.Quads);

                SetColor(EventColor(e, t));

                Rlgl.TexCoord2f(0, 0); Rlgl.Vertex3f(x1, y1, 0);
                Rlgl.TexCoord2f(0, 1); Rlgl.Vertex3f(x2, y1, 0);
                Rlgl.TexCoord2f(1, 1); Rlgl.Vertex3f(x2, y2, 0);
                Rlgl.TexCoord2f(1, 0); Rlgl.Vertex3f(x1, y2, 0);

                Rlgl.End();

                Rlgl.SetTexture(0);
                Rlgl.DrawRenderBatchActive();
                Rlgl.EnableDepthMask();
                break;

            case EventType.RenderToTexture:
                TakeSnapshot(e.Texture, x1, y1, x2, y2);
                break;

            case EventType.Feedback:
                // The radial blur: the snapshot is drawn LayerCount times. Copy number x is
                // zoomed by 1 + x / LayerCount / ZoomDivisor and gets fainter as x grows.
                if (shotRect[e.Texture] == null)
                    break;

                Rlgl.DisableDepthTest();
                SetEventBlend(e);

                {
                    float[] s = shotRect[e.Texture];
                    float xc = (s[0] + s[2]) / 2f, yc = (s[1] + s[3]) / 2f;
                    float xs = (s[2] - s[0]) / 2f, ys = (s[3] - s[1]) / 2f;

                    Vector4 col = EventColor(e, t);

                    Rlgl.SetTexture(shots[e.Texture].Texture.Id);
                    Rlgl.Begin(DrawMode.Quads);

                    for (int x = 0; x < e.LayerCount; x++)
                    {
                        float zoom = 1.0f + ((float)x / e.LayerCount / e.ZoomDivisor);
                        SetColor(col * ((20 - x) / 20.0f));
                        ShotCoord(s, xc - (xs / zoom), yc - (ys / zoom)); Rlgl.Vertex3f(x1, y1, 0);
                        ShotCoord(s, xc + (xs / zoom), yc - (ys / zoom)); Rlgl.Vertex3f(x2, y1, 0);
                        ShotCoord(s, xc + (xs / zoom), yc + (ys / zoom)); Rlgl.Vertex3f(x2, y2, 0);
                        ShotCoord(s, xc - (xs / zoom), yc + (ys / zoom)); Rlgl.Vertex3f(x1, y2, 0);
                    }
                }

                Rlgl.End();
                Rlgl.SetTexture(0);
                Rlgl.DrawRenderBatchActive();
                Rlgl.EnableDepthTest();
                break;

            case EventType.GridEffect:
                if (shotRect[e.Texture] == null)
                    break;

                Rlgl.EnableDepthTest();
                Rlgl.DisableDepthMask();
                SetEventBlend(e);

                {
                    float[] s = shotRect[e.Texture];

                    Rlgl.SetTexture(shots[e.Texture].Texture.Id);
                    Rlgl.Begin(DrawMode.Quads);
                    SetColor(EventColor(e, t));
                    GridEffect(e.Effect, DoLerp(e.GridStart, e.GridEnd, t), x1, y1, x2, y2, s);
                    Rlgl.End();
                }

                Rlgl.SetTexture(0);
                Rlgl.DrawRenderBatchActive();
                Rlgl.EnableDepthMask();
                break;
        }
    }

    // Corridor geometry, fixed-function lighting in GLSL, and GPU mesh ownership.
    void DrawWorld(Look look, IEnumerable<Chunk> visible)
    {
        // Only small, local coordinates reach the GPU. The absolute center line is
        // double precision on the CPU; subtracting the eye before upload prevents
        // jitter and depth breakdown after a long flight.
        Rlgl.DrawRenderBatchActive();
        Rlgl.Viewport(0, 0, width, height);
        Rlgl.SetMatrixProjection(Perspective(look.Fov, aspect, 0.1f, 500f));

        // The camera sits at the origin and the world is moved around it. (The tunnel
        // gets far too long for the GPU's small numbers otherwise.)
        Rlgl.SetMatrixModelView(LookAt(Vector3.Zero, look.Forward, look.Up));

        /* Fog and lights */
        Raylib.SetShaderValue(shader, locFogOn, 1, ShaderUniformDataType.Int);
        Raylib.SetShaderValue(shader, locFogColor, look.FogColor, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(shader, locFogDensity, look.FogDensity, ShaderUniformDataType.Float);
        var pos = new Vector4[8];
        var amb = new Vector3[8];
        var col = new Vector3[8];
        var att = new Vector3[8];
        var sdir = new Vector3[8];
        var spot = new Vector2[8];
        for (int i = 0; i < 4; i++)
        {
            pos[i] = new Vector4(look.LightDir[i], 0); // w = 0: light from a direction, like the sun
            amb[i] = look.LightAmbient[i];
            col[i] = look.LightColor[i];
            spot[i] = new Vector2(0, -2f);
        }
        Raylib.SetShaderValue(shader, locLightCount, 4, ShaderUniformDataType.Int);
        Raylib.SetShaderValueV(shader, locLightPos, pos, ShaderUniformDataType.Vec4, 8);
        Raylib.SetShaderValueV(shader, locLightAmb, amb, ShaderUniformDataType.Vec3, 8);
        Raylib.SetShaderValueV(shader, locLightCol, col, ShaderUniformDataType.Vec3, 8);
        Raylib.SetShaderValueV(shader, locLightAtt, att, ShaderUniformDataType.Vec3, 8);
        Raylib.SetShaderValueV(shader, locSpotDir, sdir, ShaderUniformDataType.Vec3, 8);
        Raylib.SetShaderValueV(shader, locSpot, spot, ShaderUniformDataType.Vec2, 8);
        Raylib.SetShaderValue(shader, locEnvPass, 0, ShaderUniformDataType.Int);
        Raylib.SetShaderValue(shader, locDiffuse, new Vector4(1, 1, 1, 1), ShaderUniformDataType.Vec4);

        Rlgl.DisableBackfaceCulling();
        Rlgl.EnableDepthTest();
        Rlgl.DisableColorBlend();
        Rlgl.EnableDepthMask();

        /* Out it goes */
        foreach (Chunk chunk in visible)
        {
            if (chunk.Gpu is not GpuChunk gpu)
                chunk.Gpu = gpu = Upload(chunk);

            Vector3 offset = (chunk.Anchor - look.Eye).ToVector3();
            var transform = new Matrix4x4(1, 0, 0, offset.X, 0, 1, 0, offset.Y, 0, 0, 1, offset.Z, 0, 0, 0, 1);

            foreach (var (mesh, materialNumber) in gpu.Parts)
            {
                Raylib.SetMaterialTexture(ref material, MaterialMapIndex.Albedo, MaterialTexture(materialNumber));
                Raylib.DrawMesh(mesh, material, transform);
            }
        }

        Rlgl.EnableDepthMask();
        Use2D();
    }

    static GpuChunk Upload(Chunk chunk)
    {
        // Vertex color stores the original object tint. The shader multiplies it
        // into diffuse lighting, so no extra uniform or texture channel is needed.

        var gpu = new GpuChunk();

        foreach (ChunkMesh m in chunk.Meshes)
        {
            int n = m.Positions.Length;
            var mesh = new Mesh(n, n / 3);
            mesh.AllocVertices();
            mesh.AllocNormals();
            mesh.AllocTexCoords();
            mesh.AllocColors();
            m.Positions.CopyTo(mesh.VerticesAs<Vector3>());
            m.Normals.CopyTo(mesh.NormalsAs<Vector3>());
            m.TexCoords.CopyTo(mesh.TexCoordsAs<Vector2>());

            Span<Color> colors = mesh.ColorsAs<Color>();
            for (int i = 0; i < n; i++)
                colors[i] = new Color((byte)(m.Tints[i].X * 255), (byte)(m.Tints[i].Y * 255), (byte)(m.Tints[i].Z * 255), (byte)255);

            Raylib.UploadMesh(ref mesh, false);
            gpu.Parts.Add((mesh, m.Material));
        }
        chunk.Meshes = null;      // the graphics card has them now
        return gpu;
    }

    // Render-target copies and the coordinate mapping used by all screen effects.
    void TakeSnapshot(int index, float x1, float y1, float x2, float y2)
    {
        // A full-size render texture replaces Project Genesis player's 1024x1024 copy.
        // shotRect preserves Conspiracy's measurement system so GridEffect and Feedback
        // can keep their original constants and still sample the right pixels.

        if (shots[index].Id == 0)
        {
            shots[index] = Raylib.LoadRenderTexture(width, height);
            Raylib.SetTextureFilter(shots[index].Texture, TextureFilter.Bilinear);
            Raylib.SetTextureWrap(shots[index].Texture, TextureWrap.Clamp);
            // Clear it including alpha (we are in the middle of a frame, where alpha writes are off).
            Rlgl.DrawRenderBatchActive();
            Raylib.EndTextureMode();
            Raylib.BeginTextureMode(shots[index]);
            Rlgl.ColorMask(true, true, true, true);
            Raylib.ClearBackground(Color.Black);
            Raylib.EndTextureMode();
            Raylib.BeginTextureMode(frame);
            EnterFrameState();
        }

        Rlgl.DrawRenderBatchActive();
        Raylib.EndTextureMode();
        Raylib.BeginTextureMode(shots[index]);
        Rlgl.ColorMask(true, true, true, false);
        Rlgl.DisableBackfaceCulling();
        Rlgl.DisableDepthTest();
        Rlgl.DisableColorBlend();
        Rlgl.SetMatrixProjection(Ortho(0, 1, 0, 1));
        Rlgl.SetMatrixModelView(Matrix4x4.Identity);
        Rlgl.SetTexture(frame.Texture.Id);
        Rlgl.Begin(DrawMode.Quads);
        Rlgl.Color4f(1, 1, 1, 1);
        Rlgl.TexCoord2f(0, 0); Rlgl.Vertex3f(0, 0, 0);
        Rlgl.TexCoord2f(1, 0); Rlgl.Vertex3f(1, 0, 0);
        Rlgl.TexCoord2f(1, 1); Rlgl.Vertex3f(1, 1, 0);
        Rlgl.TexCoord2f(0, 1); Rlgl.Vertex3f(0, 1, 0);
        Rlgl.End();
        Rlgl.SetTexture(0);
        Rlgl.DrawRenderBatchActive();
        Raylib.EndTextureMode();
        Raylib.BeginTextureMode(frame);
        EnterFrameState();

        // Project Genesis copied into a 1024x1024 texture and remembered which part of it
        // was used. At 1024x768 that part is x: 0..(w/640), y: (h/640)..0. Keeping the
        // same numbers keeps the warp effects the same strength.
        shotRect[index] = [0f, (y2 - y1) / 640f, (x2 - x1) / 640f, 0f, x1, y1, x2, y2];
    }

    static void ShotCoord(float[] s, float u, float v)
    {
        // Convert the virtual 640x480 destination rectangle to UVs in our complete
        // off-screen image, including the inverted Y used by render textures.
        // Where the snapshot's rectangle sits in our full-picture copy.

        float left = s[4] / VirtualWidth, right = s[6] / VirtualWidth;
        float top = 1f - ((s[5] - VirtualTop) / (VirtualBottom - VirtualTop));
        float bottom = 1f - ((s[7] - VirtualTop) / (VirtualBottom - VirtualTop));
        Rlgl.TexCoord2f(left + (u / s[2] * (right - left)), bottom + (v / s[1] * (top - bottom)));
    }

    // Conspiracy's grid effects. I don't fully understand how the math works
    // and just ported it over. I hate absolutely everything about 3D graphics
    // programming. Please let me die.

    void InitGridJitter(float u1, float v1, float u2, float v2)
    {
        // Using System.Random here fucks up the the effect. It depends
        // on Project Genesis' own custom random number generator.

        for (int i = 0; i <= GridX; i++)
            for (int j = 0; j <= GridY; j++)
            {
                double reli = (float)i / GridX, relj = (float)j / GridY;
                double o = (TexGen.Rand() - 16384) / 163840.0;
                double p = ((u2 - u1) * reli) + u1;

                if (o + p < u1)
                    o = p - u1;
                if (o + p > u2)
                    o = p - u2;

                gridJitter[i, j, 0] = o;

                o = (TexGen.Rand() - 16384) / 163840.0;
                p = ((v2 - v1) * relj) + v1;

                if (o + p < v1)
                    o = p - v1;
                if (o + p > v2)
                    o = p - v2;

                gridJitter[i, j, 1] = o;
            }
    }

    static float DoSqr(float n)
    {
        return n * n;
    }

    void GridEffect(int fx, float time, float x1, float y1, float x2, float y2, float[] s)
    {
        // Destination vertices remain a regular grid; only texture coordinates move.
        // Flight supplies strength, converted back to the sine-based time parameter
        // used by these original formulas.

        float u1 = s[0], v1 = s[1], u2 = s[2], v2 = s[3];
        var grid = new float[GridX + 1, GridY + 1, 2];
        const float pi = MathF.PI;
        time = Math.Clamp(time, 0f, 1f);
        u1 += 0.002f;
        u2 -= 0.002f;
        v1 -= 0.002f;
        v2 += 0.002f;
        float um = 0.5f * (u1 + u2), vm = 0.5f * (v1 + v2);
        // Ripple and disintegrate fade in and out over 180 degrees, so 0..1 becomes 0..3.6.
        if (fx is 3 or 4) time *= 3.6f;

        for (int i = 0; i <= GridX; i++)
            for (int j = 0; j <= GridY; j++)
            {
                float reli = (float)i / GridX, relj = (float)j / GridY;
                float u = ((u2 - u1) * reli) + u1, v = ((v2 - v1) * relj) + v1, f, angle;
                float reli2 = reli - 0.5f, relj2 = relj - 0.5f;

                // The effect numbers are the aDDict radio buttons in Conspiracy's editor.

                switch (fx)
                {
                    case 0:
                        // Sine slime: a small traveling wave. Strength grows to the end of the event.
                        u += ((MathF.Sin((time * 2f) + (reli * 20f) + (relj * 15f)) * 0.05f) + (MathF.Cos((time * 2f) + (reli * 10) + (relj * 25f)) * 0.05f)) * DoSqr(MathF.Sin(time * pi * 0.5f));
                        v += ((MathF.Sin((time * 2) + (reli * 18) + (relj * 25)) * 0.05f) + (MathF.Sin((time * 2) + (reli * 28) + (relj * 15)) * 0.05f)) * DoSqr(MathF.Sin(time * pi * 0.5f));
                        u = Math.Clamp(u, u1, u2);
                        if (v > v1) v = v1;
                        if (v < v2) v = v2;
                        break;
                    case 1:
                        // Glass bottom: pulled into the middle. Past the halfway circle the picture flips through the center.
                        f = MathF.Sqrt(DoSqr(reli2) + DoSqr(relj2)) * 2.0f;
                        f = MathF.Pow(f, time * 3.5f);
                        f *= DoSqr(MathF.Sin(time * pi));
                        u = ((u - um) * (1 - f)) + um;
                        v = ((v - vm) * (1 - f)) + vm;
                        break;
                    case 2:
                        // FOV zoom, the warp zoom: the middle of the screen reads texels from farther out.
                        f = MathF.Sqrt(DoSqr(reli2) + DoSqr(relj2)) * 2;
                        f = f > 0.001f ? MathF.Pow(f, (1 / ((4 * DoSqr(MathF.Sin(time * pi))) + 1)) - 1) : 0;
                        u = ((u - um) * f) + um;
                        v = ((v - vm) * f) + vm;
                        break;
                    case 3:
                        // Ripple: each grid point is shoved by its random offset, then kept inside the snapshot, so the cells stretch.
                        u += DoSqr(MathF.Sin(time * pi / 180 * 50)) * (float)gridJitter[i, j, 0];
                        v += DoSqr(MathF.Sin(time * pi / 180 * 50)) * (float)gridJitter[i, j, 1];
                        u = Math.Clamp(u, u1, u2);
                        if (v > v1) v = v1;
                        if (v < v2) v = v2;
                        break;
                    case 4:
                        // Disintegrate: the same offsets, without the clamp. The draw below keeps each cell's square intact.
                        u += DoSqr(MathF.Sin(time * pi / 180 * 50)) * (float)gridJitter[i, j, 0];
                        v += DoSqr(MathF.Sin(time * pi / 180 * 50)) * (float)gridJitter[i, j, 1];
                        break;
                    case 6:
                        // Twirl, the warp swirl: points near the middle turn around it, most at the center.
                        f = MathF.Sqrt(DoSqr(reli2) + DoSqr(relj2));
                        angle = f > 0.01f ? MathF.Atan(relj2 / reli2) : 0;
                        if (reli2 < 0) angle += pi;
                        if (f < 0.5f) angle -= 25 * DoSqr(0.5f - f) * DoSqr(MathF.Sin(time * pi * 0.5f));
                        u = ((u2 - u1) * ((f * MathF.Cos(angle)) + 0.5f)) + u1;
                        v = ((v2 - v1) * ((f * MathF.Sin(angle)) + 0.5f)) + v1;
                        break;
                    case 7:
                        // Wave: rings of the picture are pulled towards the center.
                        f = MathF.Sqrt(DoSqr(reli2) + DoSqr(relj2));
                        angle = f > 0.01f ? MathF.Atan(relj2 / reli2) : 0;
                        if (reli2 < 0) angle -= pi;
                        f -= DoSqr(MathF.Sin((time * pi / 180 * 400) + (f * 10))) * 0.2f * DoSqr(MathF.Sin(time * pi / 180 * 180));
                        u = ((u2 - u1) * ((f * MathF.Cos(angle)) + 0.5f)) + u1;
                        v = ((v2 - v1) * ((f * MathF.Sin(angle)) + 0.5f)) + v1;
                        break;
                }
                grid[i, j, 0] = u;
                grid[i, j, 1] = v;
            }

        float du = (u2 - u1) / GridX, dv = (v2 - v1) / GridY;
        for (int i = 0; i < GridX; i++)
            for (int j = 0; j < GridY; j++)
            {
                float ax = ((x2 - x1) * ((float)i / GridX)) + x1, bx = ((x2 - x1) * ((float)(i + 1) / GridX)) + x1;
                float ay = ((y2 - y1) * ((float)j / GridY)) + y1, by = ((y2 - y1) * ((float)(j + 1) / GridY)) + y1;
                if (fx == 4)
                {
                    // Every cell keeps its own little square of picture, just moved.
                    float gu = grid[i, j, 0], gv = grid[i, j, 1];
                    ShotCoord(s, gu, gv); Rlgl.Vertex3f(ax, ay, 0);
                    ShotCoord(s, gu + du, gv); Rlgl.Vertex3f(bx, ay, 0);
                    ShotCoord(s, gu + du, gv + dv); Rlgl.Vertex3f(bx, by, 0);
                    ShotCoord(s, gu, gv + dv); Rlgl.Vertex3f(ax, by, 0);
                }
                else
                {
                    ShotCoord(s, grid[i, j, 0], grid[i, j, 1]); Rlgl.Vertex3f(ax, ay, 0);
                    ShotCoord(s, grid[i + 1, j, 0], grid[i + 1, j, 1]); Rlgl.Vertex3f(bx, ay, 0);
                    ShotCoord(s, grid[i + 1, j + 1, 0], grid[i + 1, j + 1, 1]); Rlgl.Vertex3f(bx, by, 0);
                    ShotCoord(s, grid[i, j + 1, 0], grid[i, j + 1, 1]); Rlgl.Vertex3f(ax, by, 0);
                }
            }
    }

    // Matrices are written out explicitly: column vectors, translation in the last
    // column, and the row-major System.Numerics layout Raylib-cs passes to raylib.

    // Orthographic projection for effect rectangles in virtual screen coordinates.
    static Matrix4x4 Ortho(float left, float right, float bottom, float top)
    {
        return new(
        2f / (right - left), 0, 0, -(right + left) / (right - left),
        0, 2f / (top - bottom), 0, -(top + bottom) / (top - bottom),
        0, 0, -1, 0,
        0, 0, 0, 1);
    }

    static Matrix4x4 Perspective(float fovDegrees, float aspect, float near, float far)
    {
        // This is an OpenGL projection, not the DirectX 0..1 depth variant.
        float f = 1f / MathF.Tan(fovDegrees * MathF.PI / 360f);
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (far + near) / (near - far), 2f * far * near / (near - far),
            0, 0, -1, 0);
    }

    static Matrix4x4 LookAt(Vector3 eye, Vector3 target, Vector3 up)
    {
        // Flight supplies a forward vector and an already rolled -Y up vector.
        Vector3 f = Vector3.Normalize(target - eye);
        Vector3 s = Vector3.Normalize(Vector3.Cross(f, up));
        Vector3 u = Vector3.Cross(s, f);
        return new Matrix4x4(
            s.X, s.Y, s.Z, -Vector3.Dot(s, eye),
            u.X, u.Y, u.Z, -Vector3.Dot(u, eye),
            -f.X, -f.Y, -f.Z, Vector3.Dot(f, eye),
            0, 0, 0, 1);
    }

    // Old OpenGL fixed-function lighting and fog recreated in the two shaders below.

    // Per-vertex ambient + Lambert diffuse, with no specular term. The sphere-map
    // branch is retained for the original draw path even though the corridor uses
    // its ordinary texture pass.
    const string VertexShader = @"#version 330
in vec3 vertexPosition;
in vec2 vertexTexCoord;
in vec3 vertexNormal;
in vec4 vertexColor;
uniform mat4 mvp;
uniform mat4 matModel;
uniform mat4 matView;
uniform mat4 matNormal;
uniform vec4 matDiffuse;
uniform int envPass;
uniform int lightCount;
uniform vec4 lightPos[8];
uniform vec3 lightAmb[8];
uniform vec3 lightCol[8];
uniform vec3 lightAtt[8];
uniform vec3 lightSpotDir[8];
uniform vec2 lightSpot[8];
out vec4 fragColor;
out vec2 fragUv;
out float fragDepth;

void main()
{
    vec4 world = matModel * vec4(vertexPosition, 1.0);
    vec3 n = mat3(matNormal) * vertexNormal;
    float nl = length(n);
    n = nl > 0.0 ? n / nl : vec3(0.0);

    // 0.04 = OpenGL's default scene ambient (0.2) times default material ambient (0.2).
    vec3 sum = vec3(0.04);
    for (int i = 0; i < lightCount; i++)
    {
        vec3 toLight;
        float att = 1.0;
        if (lightPos[i].w == 0.0) toLight = normalize(lightPos[i].xyz);        // a sun
        else
        {
            vec3 d = lightPos[i].xyz - world.xyz;                              // a lamp
            float dist = length(d);
            toLight = d / max(dist, 1e-6);
            att = 1.0 / (lightAtt[i].x + lightAtt[i].y * dist + lightAtt[i].z * dist * dist);
            if (lightSpot[i].y > -1.5)
            {
                float sd = dot(-toLight, normalize(lightSpotDir[i]));
                att *= sd < lightSpot[i].y ? 0.0 : pow(max(sd, 0.0), lightSpot[i].x);
            }
        }
        sum += att * (lightAmb[i] * 0.2 + max(dot(n, toLight), 0.0) * lightCol[i] * matDiffuse.rgb * vertexColor.rgb);
    }
    fragColor = vec4(clamp(sum, 0.0, 1.0), matDiffuse.a);

    gl_Position = mvp * vec4(vertexPosition, 1.0);
    vec4 eye = matView * world;
    // Distance in front of the camera. (No abs() here: big wall triangles reach behind
    // the camera, and the value must stay a straight line across them.)
    fragDepth = gl_Position.w;
    if (envPass == 1)
    {
        // Reflect the view direction off the surface and use where the mirror
        // ray points to pick a spot on the round reflection texture.
        vec3 u = normalize(eye.xyz);
        vec3 en = normalize(mat3(matView) * n);
        vec3 r = u - 2.0 * dot(en, u) * en;
        float m = 2.0 * sqrt(r.x * r.x + r.y * r.y + (r.z + 1.0) * (r.z + 1.0));
        fragUv = vec2(r.x / m + 0.5, r.y / m + 0.5);
    }
    else fragUv = vertexTexCoord;
}";

    const string FragmentShader = @"#version 330
in vec4 fragColor;
in vec2 fragUv;
in float fragDepth;
uniform sampler2D texture0;
uniform int fogOn;
uniform vec3 fogColor;
uniform float fogDensity;
out vec4 finalColor;

void main()
{
    vec4 c = fragColor * vec4(texture(texture0, fragUv).rgb, 1.0);
    if (fogOn == 1)
    {
        // Exponential fog: the further away, the more of the fog color.
        float f = clamp(exp(-fogDensity * abs(fragDepth)), 0.0, 1.0);
        c.rgb = mix(fogColor, c.rgb, f);
    }
    finalColor = c;
}";
}