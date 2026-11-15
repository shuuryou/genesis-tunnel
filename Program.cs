using Raylib_cs;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace GenesisTunnel;

// Genesis Tunnel: an endless flight through the pipe corridor from Conspiracy's
// Project Genesis (Breakpoint 2003). This file owns process lifetime and the frame
// loop. Mode.cs parses the command line, NativeMethods.cs is the preview's Win32
// surface, and PreviewBuffer.cs copies each frame into the control-panel window.
// It does not generate geometry or animate the camera.
//
// Windows screen saver switches (case-insensitive; '-' also works):
//   no arguments                  settings dialog
//   /s                            fullscreen test or idle-time screen saver
//   /c, /c HWND, /c:HWND          settings, optionally owned by the control panel
//   /p HWND, /p:HWND              draw the small control-panel preview
//
// Development and testing switches:
//   /w                            resizable window; Esc quits, M toggles the map
//   /set Name=Value,...           use these values instead of the registry (or instead
//                                 of the defaults, for /shot and /map)
//   /shot DIR SEED SECONDS EVERY  fixed 60 Hz flight, periodic BMPs and log.txt
//   /map DIR SEED                 save the opening sections' occupancy/route map
// /shot and /map consume the remaining arguments, so /set must precede them.
//
// Startup reads the embedded genesis.bin with Project.Load. TexGen and MeshGen run
// the original generators on demand, and Kit extracts scene 2 into reusable parts.
// Tunnel builds sections ahead of the camera, marks occupied space, plans a safe
// route and bends local vertices onto a double-precision center line. Flight turns
// time and settings into a Look. Player renders that Look to an off-screen texture,
// including the original post effects, after which this file scales it to the window.
// Sections behind the camera are dropped and their GPU meshes freed by Player.
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Mode mode = Mode.Parse(args);
        try
        {
            if (mode.Config)
            {
                ConfigForm.Run(mode.Owner);
                return 0;
            }

            if (mode.Shot)
                return Shots(mode);

            if (mode.Map)
                return SaveMap(mode);

            if (mode.Preview)
                return ShowPreview(mode);
            return Run(mode);
        }
        catch (Exception ex)
        {
            WriteError(ex);
            return 1;
        }
    }

    // A screensaver has no console, so leave a note next to the exe.
    // (Or in the temp folder, if the exe sits somewhere we may not write, like System32.)
    static void WriteError(Exception ex)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "GenesisTunnel-error.log"), ex.ToString());
        }
        catch
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "GenesisTunnel-error.log"), ex.ToString());
            }
            catch { }
        }
    }

    static byte[] LoadProjectGenesisData()
    {
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("genesis.bin");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // Keep zero as the UI's "random every launch" value; /shot always supplies an
    // explicit seed and therefore remains reproducible.
    static int PickSeed(Settings cfg)
    {
        return cfg.Seed != 0 ? cfg.Seed : Environment.TickCount & 0x7fffffff;
    }

    static int Run(Mode mode)
    {
        // Settings and the raylib window come first. The project, kit and initial
        // sections are then built behind a black frame so startup does not flash an
        // uninitialized back buffer.
        Settings cfg = mode.LoadSettings();
        OpenWindow(mode);

        Project project = Project.Load(LoadProjectGenesisData());
        var kit = new Kit(project);
        int seed = PickSeed(cfg);
        var tunnel = new Tunnel(kit, cfg, seed);
        var flight = new Flight(tunnel, cfg, seed);

        Player player = null;
        MapOverlay map = null;
        bool showMap = cfg.ShowMap;
        int builtFor = 0;
        Vector2 mouse = Raylib.GetMousePosition();
        double armed = 0;

        while (!Raylib.WindowShouldClose())
        {
            int sw = Math.Max(Raylib.GetScreenWidth(), 16), sh = Math.Max(Raylib.GetScreenHeight(), 16);
            Rectangle picture = cfg.Widescreen ? FitPicture(sw, sh) : new Rectangle(0, 0, sw, sh);

            // (Re)create the player when the picture size changes. With "smooth" on, the
            // picture is drawn at twice the size and shrunk when shown, which smooths the pipe edges.
            int scale = cfg.Smooth && picture.Width <= 1920 ? 2 : 1;
            int pw = (int)picture.Width * scale, ph = (int)picture.Height * scale;
            if (player == null || builtFor != (pw * 100000) + ph)
            {
                player = new Player(project, pw, ph, cfg.Widescreen ? Player.Aspect : picture.Width / picture.Height);
                if (!player.ShaderOk)
                {
                    Raylib.CloseWindow();
                    ReportShaderFailure();
                    return 1;
                }
                builtFor = (pw * 100000) + ph;
            }
            else flight.Update(Math.Min(Raylib.GetFrameTime(), 0.1f));

            player.Render(flight.Look, tunnel.Visible(flight.S), tunnel.Dropped);

            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Black);
            // The off-screen picture is stored bottom row first, hence the negative height.
            Raylib.DrawTexturePro(player.Frame.Texture, new Rectangle(0, 0, pw, -ph), picture, Vector2.Zero, 0, Color.White);
            if (showMap) (map ??= new MapOverlay()).Draw(tunnel, flight, picture);
            Raylib.EndDrawing();

            if (Leave(mode, ref mouse, ref armed, ref showMap)) break;
        }

        Raylib.CloseWindow();
        return 0;
    }

    static void OpenWindow(Mode mode)
    {
        Raylib.SetTraceLogLevel(TraceLogLevel.Warning);
        if (mode.Fullscreen)
        {
            // Size 0,0 means "the monitor's own resolution".
            Raylib.SetConfigFlags(ConfigFlags.FullscreenMode | ConfigFlags.VSyncHint);
            Raylib.InitWindow(0, 0, "Genesis Tunnel");
            Raylib.HideCursor();
        }
        else
        {
            Raylib.SetConfigFlags(ConfigFlags.VSyncHint | ConfigFlags.ResizableWindow);
            Raylib.InitWindow(1280, 722, "Genesis Tunnel");
        }
        Raylib.SetExitKey(KeyboardKey.Null);

        // Show a black screen straight away; building textures and the first sections takes a moment.
        Raylib.BeginDrawing();
        Raylib.ClearBackground(Color.Black);
        Raylib.EndDrawing();
    }

    // Fully qualified: a using would clash with raylib's Color, Rectangle and Image.
    static void ReportShaderFailure()
    {
        System.Windows.Forms.MessageBox.Show(
            "The scene shader did not compile.",
            "Genesis Tunnel",
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Error);
    }

    // Esc always leaves. In screensaver mode, after a short arming delay, so does
    // any key, click or real mouse move. Windowed mode also toggles the map with M.
    static bool Leave(Mode mode, ref Vector2 mouse, ref double armed, ref bool showMap)
    {
        armed += Raylib.GetFrameTime();
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) return true;
        if (!mode.Fullscreen && Raylib.IsKeyPressed(KeyboardKey.M)) showMap = !showMap;
        if (!mode.Fullscreen || armed < 0.7)
        {
            mouse = Raylib.GetMousePosition();
            return false;
        }
        if (Raylib.GetKeyPressed() != 0) return true;
        if (Raylib.IsMouseButtonPressed(MouseButton.Left) || Raylib.IsMouseButtonPressed(MouseButton.Right)) return true;
        return Vector2.Distance(Raylib.GetMousePosition(), mouse) > 12f;
    }

    static Rectangle FitPicture(int sw, int sh)
    {
        // The visible band is 640/361 rather than exactly 16:9. A small tolerance
        // avoids one-pixel bars on ordinary 16:9 displays because of that difference.
        float screenAspect = (float)sw / sh;
        if (MathF.Abs((screenAspect / Player.Aspect) - 1f) < 0.03f) return new Rectangle(0, 0, sw, sh);
        if (screenAspect > Player.Aspect)
        {
            float w = MathF.Round(sh * Player.Aspect);
            return new Rectangle(MathF.Round((sw - w) / 2), 0, w, sh);
        }
        float h = MathF.Round(sw / Player.Aspect);
        return new Rectangle(0, MathF.Round((sh - h) / 2), sw, h);
    }

    // Off-screen flight used by /shot and by the control-panel preview.

    sealed class Rehearsal
    {
        public readonly Tunnel Tunnel;
        public readonly Flight Flight;
        readonly Player player;
        readonly int w, h, scale;

        public Rehearsal(Settings cfg, int seed, int w, int h, int scale)
        {
            this.w = w;
            this.h = h;
            this.scale = scale;
            Raylib.SetConfigFlags(ConfigFlags.HiddenWindow);
            Raylib.SetTraceLogLevel(TraceLogLevel.Warning);
            Raylib.InitWindow(w, h, "Genesis Tunnel still");
            Project project = Project.Load(LoadProjectGenesisData());
            player = new Player(project, w * scale, h * scale, Player.Aspect);
            Tunnel = new Tunnel(new Kit(project), cfg, seed);
            Flight = new Flight(Tunnel, cfg, seed);
        }

        public byte[] Picture(out int outW, out int outH)
        {
            // Render targets are bottom-up and may be supersampled. Convert to a
            // conventional top-down RGBA image before either BMP or GDI consumes it.
            player.Render(Flight.Look, Tunnel.Visible(Flight.S), Tunnel.Dropped);
            Image img = Raylib.LoadImageFromTexture(player.Frame.Texture);
            Raylib.ImageFlipVertical(ref img);
            if (scale != 1) Raylib.ImageResize(ref img, w, h);
            Raylib.ImageFormat(ref img, PixelFormat.UncompressedR8G8B8A8);
            outW = img.Width;
            outH = img.Height;
            byte[] px = new byte[outW * outH * 4];
            unsafe { Marshal.Copy((nint)img.Data, px, 0, px.Length); }
            Raylib.UnloadImage(img);
            return px;
        }
    }

    static int Shots(Mode mode)
    {
        // Simulation time is fixed and independent of how long rendering takes. The
        // stopwatch is only for diagnostics, so a slow machine still gets the same
        // route, events and image sequence for a given seed and /set values.
        string[] rest = mode.Rest;
        if (rest.Length < 4) return 1;
        string dir = Path.GetFullPath(rest[0]);
        Directory.CreateDirectory(dir);
        int seed = int.Parse(rest[1]);
        float seconds = float.Parse(rest[2], CultureInfo.InvariantCulture);
        float every = float.Parse(rest[3], CultureInfo.InvariantCulture);

        var show = new Rehearsal(mode.ToolSettings(), seed, 960, 540, 2);
        var log = new StringBuilder();
        var watch = new Stopwatch();
        double slowest = 0, next = 0;
        int clips = 0;
        int steps = (int)(seconds * 60);

        for (int i = 0; i <= steps; i++)
        {
            watch.Restart();
            show.Flight.Update(1f / 60);
            double ms = watch.Elapsed.TotalMilliseconds;
            if (ms > slowest) slowest = ms;
            // Count the steps where the camera is inside a map box that holds a surface.
            Look at = show.Flight.Look;
            int slice = (int)(show.Flight.S / Grid.Slice);
            bool touching = show.Tunnel.CellState(slice, Grid.IX(show.Flight.Across.X), Grid.IY(show.Flight.Across.Y)) == 2;
            if (at.Flash < 0.5f && touching) clips++;
            if (ms > 8) log.AppendLine(FormattableString.Invariant($"t {i / 60.0:0.00} slow step {ms:0.0} ms"));
            if (i / 60.0 >= next)
            {
                byte[] px = show.Picture(out int w, out int h);
                SaveBmp(Path.Combine(dir, $"f{(int)Math.Round(next * 100):00000}.bmp"), px, w, h);
                Look l = show.Flight.Look;
                log.AppendLine(FormattableString.Invariant($"t {next:0.00} crashes {show.Flight.Crashes} s {show.Flight.S:0.0} fov {l.Fov:0} flash {l.Flash:0.00} swirl {l.Swirl:0.00} zoom {l.Zoom:0.00} blur {l.Blur:0.00}"));
                next += every;
            }
        }
        log.AppendLine(FormattableString.Invariant($"slowest step {slowest:0.0} ms, steps touching a surface {clips} of {steps}"));
        File.WriteAllText(Path.Combine(dir, "log.txt"), log.ToString());
        Raylib.CloseWindow();
        return 0;
    }

    static int SaveMap(Mode mode)
    {
        // No renderer is needed here. Building the tunnel also exercises the route
        // planner, whose raw cell path is what the debug picture displays.
        if (mode.Rest.Length < 2) return 1;
        string dir = Path.GetFullPath(mode.Rest[0]);
        Directory.CreateDirectory(dir);
        Settings cfg = mode.ToolSettings();
        var tunnel = new Tunnel(new Kit(Project.Load(LoadProjectGenesisData())), cfg, int.Parse(mode.Rest[1]));
        byte[] px = TunnelDebug.Picture(tunnel.CellState, slice => tunnel.PathAt((slice + 0.5) * Grid.Slice), 0, Grid.NZ * 4, -1, out int w, out int h);
        SaveBmp(Path.Combine(dir, "map.bmp"), px, w, h);
        return 0;
    }

    static void SaveBmp(string path, byte[] rgba, int w, int h)
    {
        // A tiny writer avoids another dependency for test output. Rows are padded
        // to four bytes and stored bottom-up; alpha is simply discarded.
        int row = ((w * 3) + 3) & ~3;
        byte[] file = new byte[54 + (row * h)];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(54).CopyTo(file, 10);
        BitConverter.GetBytes(40).CopyTo(file, 14);
        BitConverter.GetBytes(w).CopyTo(file, 18);
        BitConverter.GetBytes(h).CopyTo(file, 22);
        file[26] = 1;
        file[28] = 24;
        BitConverter.GetBytes(row * h).CopyTo(file, 34);

        for (int y = 0; y < h; y++)
        {
            int dst = 54 + ((h - 1 - y) * row), src = y * w * 4;
            for (int x = 0; x < w; x++, dst += 3, src += 4)
            {
                file[dst] = rgba[src + 2];
                file[dst + 1] = rgba[src + 1];
                file[dst + 2] = rgba[src];
            }
        }
        File.WriteAllBytes(path, file);
    }

    // Win32 control-panel preview. raylib cannot adopt another process's HWND.

    static int ShowPreview(Mode mode)
    {
        // Render in Rehearsal's hidden raylib window, then blit into the HWND supplied
        // by Windows. IsWindow is also the shutdown signal: the control panel destroys
        // its preview child when another saver is selected.
        nint hwnd = mode.Owner;
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd)) return 0;

        const int width = 320, height = 180;
        Settings cfg = mode.LoadSettings();
        var show = new Rehearsal(cfg, PickSeed(cfg), width, height, 1);
        try
        {
            using var buffer = PreviewBuffer.Create(width, height);
            var clock = Stopwatch.StartNew();
            double last = 0;
            while (NativeMethods.IsWindow(hwnd) && NativeMethods.GetClientRect(hwnd, out NativeMethods.Rect client))
            {
                double now = clock.Elapsed.TotalSeconds;
                show.Flight.Update((float)Math.Min(now - last, 0.1));
                last = now;
                buffer.Load(show.Picture(out _, out _));
                buffer.Blit(hwnd, client);
                Thread.Sleep(30);
            }
            return 0;
        }
        finally
        {
            Raylib.CloseWindow();
        }
    }
}
