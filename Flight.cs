// Suicidal autopilot. Flight advances time and fills one Look; Player turns that Look
// into pixels. Keeping rendering out of this file makes the same flight usable by
// the screensaver, the control-panel preview and the deterministic /shot runner.
//
// Cruise moods, warps, crashes and lighting have independent timers. They can
// overlap, except where a combination would look wrong or make the route unsafe.
// Speed 100 and Roll 100 reproduce the approximate pace of the 2003 camera.

using System.Numerics;

namespace GenesisTunnel;

// A complete lighting state. Keeping fog and lights together lets transitions
// interpolate one coherent theme instead of animating unrelated shader uniforms.
sealed class Theme
{
    public Vector3 Fog;
    public float Density;
    public Vector3[] Light = new Vector3[4];
    public Vector3[] Ambient = new Vector3[4];

    public static Theme Original()
    {
        return new()
        {
            Fog = new Vector3(90, 94, 126) / 255f,
            Density = 0.0557f,
            Light = [new Vector3(101, 148, 255) / 255f, new Vector3(234, 239, 245) / 255f, new Vector3(69, 151, 241) / 255f, new Vector3(40, 42, 40) / 255f],
            Ambient = [Vector3.Zero, Vector3.Zero, new Vector3(0, 2, 0) / 255f, new Vector3(78, 81, 74) / 255f],
        };
    }

    public static Theme Mix(Theme a, Theme b, float t)
    {
        // The arrays are fixed at four because that is what the pipe scene uses.
        // A fresh Theme avoids disturbing a blend that is already in progress.
        var m = new Theme { Fog = Vector3.Lerp(a.Fog, b.Fog, t), Density = a.Density + ((b.Density - a.Density) * t) };
        for (int i = 0; i < 4; i++)
        {
            m.Light[i] = Vector3.Lerp(a.Light[i], b.Light[i], t);
            m.Ambient[i] = Vector3.Lerp(a.Ambient[i], b.Ambient[i], t);
        }
        return m;
    }

    public static Theme Shifted(float hueDegrees, float vivid, float bright, float haze)
    {
        // Start from the original theme every time. Successive lighting changes
        // therefore cannot accumulate saturation or brightness until white-out.
        Theme o = Original();
        Vector3 Shift(Vector3 c)
        {
            // Rotation about (1,1,1) leaves gray alone and changes the hue without
            // introducing a dependency on a color-space library.
            Vector3 turned = Mat.Rotate(1, 1, 1, hueDegrees * MathF.PI / 180f).Point(c);
            float gray = Vector3.Dot(turned, new Vector3(0.30f, 0.59f, 0.11f));
            return Vector3.Clamp((new Vector3(gray) + ((turned - new Vector3(gray)) * vivid)) * bright, Vector3.Zero, Vector3.One);
        }
        var t = new Theme { Fog = Shift(o.Fog), Density = o.Density * haze };
        for (int i = 0; i < 4; i++)
        {
            t.Light[i] = Shift(o.Light[i]);
            t.Ambient[i] = Shift(o.Ambient[i]);
        }
        return t;
    }
}

// Renderer input for one frame. There is no scene state here beyond the camera,
// lighting and post-processing values that actually vary during a flight.
sealed class Look
{
    public V3d Eye;                       // camera position in the world
    public Vector3 Forward, Up;           // viewing direction and "up"
    public float Fov;                     // lens angle, top to bottom, in degrees
    public Vector3 FogColor;
    public float FogDensity;
    public Vector3[] LightDir = new Vector3[4], LightColor = new Vector3[4], LightAmbient = new Vector3[4];
    public float Flash;                   // 0..1 white-out
    public float Swirl, Zoom, Blur;       // 0..1 strength of the warp effects
}

sealed class Flight
{
    const float OriginalSpeed = 25f;      // corridor units per second

    // The four lights of the original scene shine from these directions (corridor coordinates).
    static readonly Vector3[] LightFrom =
    [
        Vector3.Normalize(new Vector3(4.5625f, 5.78125f, -15.125f)),
        Vector3.Normalize(new Vector3(-1.789f, 1f, -0.33f)),
        Vector3.Normalize(new Vector3(-7.65625f, -5.34375f, 12.625f)),
        Vector3.Normalize(new Vector3(18.625f, -10.75f, 5.25f)),
    ];

    readonly Tunnel tunnel;
    readonly Settings cfg;
    readonly Random rng;

    public readonly Look Look = new();

    // Distance flown along the corridor.
    public double S { get; private set; } = 1;

    // How many times the flight has crashed and restarted.
    public int Crashes { get; private set; }

    // Camera position across the corridor (x, y).
    public Vector2 Across { get; private set; }

    // Cruise goals and the values that are easing towards them.
    float moodLeft, moodSpeed = 1;
    float speed, fov = 45, fovGoal = 45;
    float lookAhead = 150, lookAheadGoal = 150, lookX = 0.15f, lookXGoal = 0.15f, lookY = -0.5f, lookYGoal = -0.5f;
    float roll, rollRate, rollRateGoal;
    bool level; // true = keep the horizon level instead of rolling
    float glide = 8; // how many units of route are averaged into the camera position

    // A negative warpTime means no warp is running.
    float warpIn, warpTime = -1, warpLength;
    int warpKind;

    // crashS is where the re-planned route first touches a surface. It lies a few
    // seconds ahead, because the route drifts into the pipe instead of swerving.
    float crashIn;
    double crashS = -1; // where the planned crash happens (-1 = none planned)

    // Flashes are shared by the opening, warps and crash cuts.
    float flash, flashFade = 0.8f;

    // Theme transitions and the shorter, irregular lamp flicker.
    Theme themeFrom = Theme.Original(), themeTo = Theme.Original();
    float themeT = 1, themeIn;
    float flickerIn, flickerLeft, flickerLevel = 1, flickerHold;
    double clock;

    public Flight(Tunnel tunnel, Settings cfg, int seed)
    {
        // Deriving a separate random stream from the tunnel seed means adding a
        // lighting call cannot change the corridor layout or its route wishes.
        this.tunnel = tunnel;
        this.cfg = cfg;
        rng = new Random(seed ^ 0x0f17);
        NewMood(true);

        // The first seconds are the original's opening: straight, rolling, lens at 45 degrees.
        moodSpeed = 1;
        fov = fovGoal = 45;
        level = false;
        speed = OriginalSpeed * cfg.Speed / 100f;
        flash = 1;

        warpIn = Wait(cfg.WarpEvery);
        crashIn = Wait(cfg.CrashEvery);
        themeIn = Wait(cfg.LightChangeEvery);
        flickerIn = FlickerWait();
        Update(0);
    }

    float Between(float a, float b)
    {
        return a + ((float)rng.NextDouble() * (b - a));
    }

    bool Chance(double p)
    {
        return rng.NextDouble() < p;
    }

    static float Smooth(float t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - (2 * t));
    }
    // Exponential easing has no fixed arrival time, which suits moods that can be
    // replaced halfway through a transition.
    static float Ease(float value, float goal, float rate, float dt)
    {
        return value + ((goal - value) * (1 - MathF.Exp(-rate * dt)));
    }

    // A uniform 0.5..1.5 multiplier keeps events irregular without long dry spells.
    float Wait(int seconds)
    {
        return seconds <= 0 ? float.MaxValue : seconds * Between(0.5f, 1.5f);
    }

    // Flicker is an intensity setting in the UI, hence the reciprocal here.
    float FlickerWait()
    {
        return cfg.Flicker <= 0 ? float.MaxValue : 400f / cfg.Flicker * Between(0.4f, 1.6f);
    }

    void NewMood(bool cut)
    {
        // Most moods are variations on the opening shot. A sideways look is a
        // separate mode with a near target and a level horizon, not just another
        // random camera offset.
        bool lookAround = !cut && warpTime < 0 && Chance(cfg.LookAround / 100.0 * 0.45);

        if (lookAround)
        {
            // Slow pass, wide lens, looking across at one side of the corridor.
            moodSpeed = Between(0.3f, 0.55f);
            fovGoal = Between(64, 100);
            lookAheadGoal = Between(6, 10);
            lookXGoal = Between(2.5f, 3.8f) * (Chance(0.5) ? 1 : -1);
            lookYGoal = Between(0f, 0.9f);
            level = true;
            moodLeft = Between(5, 9);
        }
        else
        {
            float vary = cfg.SpeedVariation / 100f;
            moodSpeed = MathF.Exp(Between(-1, 1) * 0.8f * vary);
            fovGoal = Chance(0.5) ? Between(45, 55) : Between(60, 75);
            lookAheadGoal = Between(60, 150);
            lookXGoal = Between(-0.3f, 0.4f);
            lookYGoal = Between(-0.6f, 0.1f);
            level = cfg.Roll <= 0 || Chance(0.15);
            // The original rolls between 0.1 and 0.3 turns per second.
            rollRateGoal = Between(0.08f, 0.33f) * MathF.Tau * (Chance(0.5) ? 1 : -1) * cfg.Roll / 100f;
            moodLeft = Between(5, 12);
        }

        if (cut)
        {
            // After a cut everything starts at its new value instead of easing there.
            fov = fovGoal;
            lookAhead = lookAheadGoal;
            lookX = lookXGoal;
            lookY = lookYGoal;
            rollRate = level ? 0 : rollRateGoal;
            roll = cfg.Roll <= 0 ? 0 : Between(0, MathF.Tau);
            speed = OriginalSpeed * cfg.Speed / 100f * moodSpeed;
        }
    }

    void NewTheme()
    {
        // Freeze the currently displayed mixture as the start of the next blend.
        // Otherwise a new event arriving mid-fade would cause a visible jump.
        float range = cfg.ColorRange / 100f;
        themeFrom = Theme.Mix(themeFrom, themeTo, Smooth(themeT));
        // Often it simply goes back to the original blue. Otherwise the hue is turned; the
        // range is squared so that middling settings stay close to blue (teal to violet).
        themeTo = range <= 0 || Chance(0.45)
            ? Theme.Original()
            : Theme.Shifted(Between(-180, 180) * range * range, 1 + (Between(-0.3f, 0.6f) * range),
                            1 + (Between(-0.25f, 0.2f) * range), 1 + (Between(-0.25f, 0.4f) * range));
        themeT = 0;
    }

    public void Update(float dt)
    {
        // Route distance is integrated independently of rendering. The caller caps
        // dt after a stall so one slow frame cannot skip an entire crash or warp.
        clock += dt;

        // Choose a new cruise or look-around mood when the old one expires.
        moodLeft -= dt;
        if (moodLeft <= 0 && crashS < 0) NewMood(false);

        // Warp strength rises and falls inside a fixed-length event. Speed, lens
        // and the three screen effects all read this same envelope.
        float warp = 0; // 0..1: how far "in" the warp we are
        float strength = cfg.WarpStrength / 100f;

        if (warpTime >= 0)
        {
            warpTime += dt;
            warp = Smooth(warpTime / 1.2f) * Smooth((warpLength - warpTime) / 1.5f);

            if (warpTime >= warpLength)
            {
                warpTime = -1;
                warpIn = Wait(cfg.WarpEvery);
            }
        }
        else if (crashS < 0 && (warpIn -= dt) <= 0)
        {
            warpTime = 0;
            warpLength = Between(4.5f, 7.5f);
            warpKind = rng.Next(3); // 0 swirl, 1 zoom, 2 both

            if (level)
                NewMood(false); // no warp while looking around

            if (Chance(0.5))
            {
                flash = MathF.Max(flash, 0.7f);
                flashFade = 0.27f;
            }
        }

        // Ease actual speed towards the mood/warp target rather than stepping it.
        float wanted = OriginalSpeed * cfg.Speed / 100f * moodSpeed * (1 + (warp * 2.5f * strength));
        speed = Ease(speed, wanted, warpTime >= 0 ? 2.5f : 0.8f, dt);
        S += speed * dt;

        // A planned suicide/crash is already part of the route. Here it only turns the
        // look direction and fades to white. The restart happens once the white frame
        // hides the cut.
        float white = 0, crashSteer = 0;

        if (crashS >= 0)
        {
            double left = crashS - 0.35 - S;
            white = Smooth(1 - (float)(left / (speed * 0.26f)));
            // Turn the view towards the direction of travel over the last second or
            // so, measured in time so that a fast flight does not snap its head round.
            crashSteer = Smooth(1 - (float)(left / MathF.Max(12f, speed * 1.2f)));

            if (left <= 0)
            {
                // The flash is covering the screen. Resume further along the tunnel.
                S = tunnel.Restart(S);
                Crashes++;
                crashS = -1;
                crashIn = Wait(cfg.CrashEvery);
                warpTime = -1;
                NewMood(true);
                if (cfg.LightChangeEvery > 0 && Chance(0.5))
                {
                    NewTheme();
                    themeT = 1;
                }
                flash = 1;
                flashFade = 0.8f;
                white = 0;
                crashSteer = 0;
            }
        }
        else if (warpTime < 0 && (crashIn -= dt) <= 0)
        {
            // The tunnel re-plans the route ahead so that it ends in a pipe. Nothing
            // changes within the stretch that is currently averaged into the camera.
            if (!tunnel.PlanCrash(S, glide, rng, out crashS))
                crashIn = 1.5f; // nothing to hit there; try again soon
        }

        // Where the tunnel found no way through a section, the route goes into a
        // surface whether the autopilot is depressed or not. Same ending.
        if (crashS < 0 && tunnel.Doomed(S, glide, out double hit))
            crashS = hit;

        tunnel.Update(S);

        // Average a longer stretch of route at speed. This keeps a swerve's duration
        // roughly constant instead of turning high speed into a camera twitch.
        // On a crash course the reach stays as it was when the impact was worked out,
        // so the camera meets the surface exactly where the plan says it does.
        if (crashS < 0) glide = Ease(glide, Math.Clamp(speed * 0.32f, 8f, 16f), 1.5f, dt);
        Vector2 across = tunnel.PathAt(S, glide);
        Across = across;
        V3d eye = tunnel.World(S, across.X, across.Y);

        fov = Ease(fov, fovGoal, 0.7f, dt);
        lookAhead = Ease(lookAhead, lookAheadGoal, 0.6f, dt);
        lookX = Ease(lookX, lookXGoal, 0.6f, dt);
        lookY = Ease(lookY, lookYGoal, 0.6f, dt);

        // Look at a point further down the corridor (far away when cruising, close and
        // off to one side when looking around)...
        Vector3 forward = Vector3.Normalize((tunnel.World(S + lookAhead, lookX, lookY) - eye).ToVector3());

        if (crashSteer > 0)
        {
            // ...but on a crash course, look where we are going.
            Vector2 next = tunnel.PathAt(S + 1.5, glide);
            Vector3 heading = Vector3.Normalize((tunnel.World(S + 1.5, next.X, next.Y) - eye).ToVector3());
            forward = Vector3.Normalize(Vector3.Lerp(forward, heading, crashSteer * 0.85f));
        }

        // Roll: either keep turning, or ease back to level.
        if (level)
        {
            float upright = MathF.Round(roll / MathF.Tau) * MathF.Tau;
            rollRateGoal = Math.Clamp((upright - roll) * 1.2f, -2f, 2f);
        }

        rollRate = Ease(rollRate, rollRateGoal, level ? 3f : 0.8f, dt);
        roll += rollRate * (1 + warp) * dt;

        // "Up" is -Y in this world; the roll turns it around the viewing direction.
        Vector3 up = Mat.Rotate(forward.X, forward.Y, forward.Z, roll).Point(new Vector3(0, -1, 0));

        Look.Eye = eye;
        Look.Forward = forward;
        Look.Up = up;
        Look.Fov = Math.Clamp(fov + (warp * 28f * MathF.Min(strength, 1.3f)), 20, 140);

        // Convert the common warp envelope into the three effects Player understands.
        Look.Blur = Math.Clamp(warp * 0.9f * strength, 0, 1.5f);
        Look.Swirl = warpKind != 1 ? Math.Clamp(warp * (0.38f + (0.12f * MathF.Sin((float)clock * 1.3f))) * strength, 0, 1) : 0;
        Look.Zoom = warpKind != 0 ? Math.Clamp(warp * 0.6f * strength, 0, 1) : 0;

        flash = MathF.Max(0, flash - (dt / flashFade));
        Look.Flash = Math.Clamp(MathF.Max(flash, white), 0, 1);

        // Theme changes are slow; flicker is applied afterwards as a brief multiplier.
        if ((themeIn -= dt) <= 0)
        {
            NewTheme();
            themeIn = Wait(cfg.LightChangeEvery);
        }
        themeT = MathF.Min(1, themeT + (dt / 4f));
        Theme theme = Theme.Mix(themeFrom, themeTo, Smooth(themeT));

        // Flicker: for a moment the lights jump between dim and bright.
        if (flickerLeft > 0)
        {
            flickerLeft -= dt;

            if ((flickerHold -= dt) <= 0)
            {
                flickerLevel = Chance(0.5) ? Between(0.1f, 0.5f) : 1;
                flickerHold = Between(0.03f, 0.12f);
            }
            if (flickerLeft <= 0)
            {
                flickerLevel = 1;
                flickerIn = FlickerWait();
            }
        }
        else if ((flickerIn -= dt) <= 0)
        {
            flickerLeft = Between(0.3f, 1.2f);
        }

        Look.FogColor = theme.Fog;
        Look.FogDensity = theme.Density;

        // The lights turn with the corridor, so the walls are always lit as in the original.
        double h = tunnel.Heading(S);
        var right = new Vector3((float)Math.Cos(h), 0, -(float)Math.Sin(h));
        var ahead = new Vector3((float)Math.Sin(h), 0, (float)Math.Cos(h));

        for (int i = 0; i < 4; i++)
        {
            Look.LightDir[i] = (right * LightFrom[i].X) + new Vector3(0, LightFrom[i].Y, 0) + (ahead * LightFrom[i].Z);
            Look.LightColor[i] = theme.Light[i] * flickerLevel;
            Look.LightAmbient[i] = theme.Ambient[i] * flickerLevel;
        }
    }
}