# 3D Genesis Tunnel (OpenGL)

<img width="640" height="360" src="https://github.com/user-attachments/assets/a8ff06b4-9381-49ba-a19f-fccb1e838afc" />

This is a Windows screen saver that lets me stay inside my favorite place from Conspiracy's *Project Genesis*, a 64k intro released at Breakpoint 2003. Like all projects in my GitHub account, I primarily built this for myself.

Unlike the effect in the intro, my pipe tunnel is infinite and random. New sections are constantly built ahead of the camera. An autopilot finds a safe route through the tunnel, but every now and then becomes so depressed that it decides to commit suicide by doing a kamikaze dive into a pipe. The screen then fades to white, and its torture repeats with a new tunnel.

Originally I tried to recreate the pipe tunnel by modeling a similar collection of pipes, but it looked like shit and was never a realistic way to get the right result.  Thankfully Conspiracy released the source code for its intro and the demomaker used to create it in 2012: https://github.com/ConspiracyHu/2012SourcePack

So I decided to just try to understand their source code, read the original project data, and port the code that interprets it. Being slightly drunk in the process made it more tolerable.

As a result, this uses the actual *Project Genesis* scene data and generators. It's basically a small part of the intro constantly being manipulated on the fly, if you want to put it that way.

The main ports are:

| Original source | Port | Purpose |
| --- | --- | --- |
| `introinput.cpp` | `Project.cs` | Read the intro data file |
| `TexGen.cpp` | `TexGen.cs` | Execute procedural texture scripts |
| `introgen.cpp` | `MeshGen.cs` | Generate primitive meshes, lofts, clones and map modifiers |
| `Track.cpp`, `vectors.cpp` | `Track.cs` | Hermite tracks and the original matrix conventions |
| `introplayer.cpp`, `inroengine.cpp` | `Player.cs` | Frame operations, aDDict effects, feedback blur and flash |

The math and pretty things are all made by the people at Conspiracy who are all much smarter and more talented than me. My contribution is the endless tunnel section builder, occupancy grid, route planner, autopilot, settings UI and Windows screen saver host.

The screensaver is about 40x larger than the original 64k intro. I guess it's a bit of an insult to their work, given that it does way less than the original intro. I'm sorry.

## How Project Genesis Data is Loaded

When poking at Conspiracy's code, it appeared that the data the intro reads is basically a serialized aDDict authoring session. Loading it has two distinct stages in the screen saver: `Project.Load` reads the records, and a port of the original generators execute those records when the renderer needs them.

### Reading the Records

The first byte says how many bytes were retained for each floating-point value. To save space, the writer seems to discard low-order bytes from IEEE floats. `ByteReader.Float` reads the remaining little-endian bytes and shifts them back into the high end of a 32-bit float, with zeroes in the discarded positions. This is why using an ordinary `BinaryReader.ReadSingle` would immediately misread the file.

The remaining top-level records appear in order:

1. Textures<br>
   Each texture has an ID and a sequence of nine-byte generator commands. A command can create fractal noise, cells or an environment map, distort another layer, blur, colorise, mix layers, and so on. Command 7 is text and has an additional length-prefixed string. The pipe scenes do not use text, so the font-dependent drawing operation is not needed here.
2. Materials<br>
   A material selects a layer from a generated texture and also stores the original alpha/reflection fields. For example, the tunnel keeps material 1 for pipe and wall metal and material 7 for the darker fittings.
3. Scenes<br>
   A scene starts with fog and lights, followed by objects, cameras and object animations.
4. Timeline<br>
   Timeline records describe drawing a 3D scene, clearing buffers, drawing a 2D layer, copying a frame to a texture, feedback blur and grid effects.

An object begins with a packed flags word. Its low byte is the primitive ID; other bits say which color channels, texture settings and secondary material fields are present. Only those fields follow in the file. Ordinary primitives have a 3x4 transformation matrix and a small parameter list.

Object order matters. A clone or modifier can only refer to objects that precede it, so `MeshGen.Generate` processes the scene in file order rather than treating the objects as independent records.

Camera channels and object animation channels are stored as keyframes. After the first key, later keys contain a bitmask saying which values changed; unchanged values carry forward. `Track` calculates the original Hermite tangents after loading. I read these tracks but do not fly the saved scene-2 camera anymore: In `Flight` I replace it with the endless autopilot.

The timeline's timestamps, rectangles, colors, blend-factor indices and effect parameters are also preserved. The screen saver does not play the intro from beginning to end. Instead, `Flight` creates the few equivalent events it needs at runtime, and `Player.DrawEvent` handles them through the same draw path as the ported player.

### Turning Instructions into Meshes and Images

`Kit` asks `MeshGen` to generate scene 2 (the pipe tunnel). The primitives are built in their local coordinates, texture coordinates are transformed using the original scale/offset/swap rules, and face and vertex normals are accumulated with the original winding and inversion behavior. Clone indices are rebased, loft helper points supply end tangents, and map modifiers sample textures produced by `TexGen`.

`Kit.Lift` then bakes each object's matrix into its vertices. Normals go through the inverse transpose so scaled objects still light correctly. The extracted objects retain their original numbers because the section builder refers to groups such as the standing elbow, ceiling run and riser by those numbers. The wall corners and diagonal normals, flange, collar, tank and normalized junction box are cached for reuse.

`TexGen` executes each texture command against four 256x256 RGBA working layers. Its output is cached on the texture definition; a material simply chooses a layer. On first use, `Player` uploads that layer with mipmaps, trilinear filtering and repeat wrapping.

Some implementation details look like bugs or weird decsisions but affect the resulting pixels: wrapping byte arithmetic, truncated float-to-byte conversions, an in-place blur, a directional blur's off-by-one divisor, and the Microsoft C runtime `rand` sequence. Those behaviors are kept. Replacing anything makes the tunnel no longer look like the original.

## Making the Tunnel Endless

A section is still 38 units long, like the original tunnel. Section 0 is the untouched opening layout unless original sections are disabled. Later sections are either original copies, sometimes mirrored, or generated variations assembled from the extracted parts.

Geometry is authored in straight tunnel coordinates:

- X runs across the tunnel, with walls around -4.16 and +4.16;
- Y points down, from ceiling -1.8 to floor +1.8;
- Z runs along the section from -19 to +19.

Long pipes are swept again as short ring segments, and walls are sliced along Z. These parts cannot simply be copied as rigid meshes because every vertex must later follow the curved center line. Other copied triangles are subdivided when their Z edges are too long. `Tunnel.Bend` samples the center line at each vertex's Z and writes positions relative to a section anchor.

Each section also gets a 42 x 18 x 76 occupancy grid. Cells are 0.2 units across the tunnel and 0.5 units long. Triangle samples mark surfaces. A second pass marks the camera clearance around those surfaces, the wall margins and tiny enclosed spaces such as ordinary pipe interiors. A large duct's interior remains open, allowing the route to fly through it.

New pipes are placed only if they do not collide with cells already occupied by earlier parts. Continuous pipes go down first, followed by optional fly-through ducts, copied original groups and wandering pipes. If no route can cross a section, I rebuild it with fewer pipes; the last attempts retain only essential through-pipes.

The route planner walks forward one half-unit slice at a time using dynamic programming. It keeps the cheapest predecessor for every open cell, penalizes sideways motion and adds a squared-distance cost towards the current wish point. Wish points favor open cells near pipes, while an upcoming duct adds a strong pull towards its center. Following the cheapest endpoint backwards recovers the path. Everything except the newest section is then frozen so a later build cannot move the route underneath the camera. `PathAt` applies raised-cosine smoothing to turn those cell centers into the camera's glide path.

The next section is built on a worker because triangle sampling and grid construction can take long enough to hitch a frame. Route planning and bending remain on the main thread. Old sections are dropped, and `Player` releases their GPU meshes on the render thread.

## 2003 OpenGL vs. My Renderer

Project Genesis was made for the fixed-function OpenGL pipeline. It could configure lights, materials, fog and matrix stacks and then submit geometry without writing a lighting shader. Its post effects used framebuffer copies, textured quads, blend functions and CPU-generated texture-coordinate grids.

A modern OpenGL 3.3 renderer does not provide that fixed-function lighting/fog pipeline in core profile, and immediate-mode rendering is not a suitable foundation for the world pass. I chose to use Raylib, which supplies the window, GPU resources, shaders, mesh drawing and render targets, but the old scene behavior still had to be reimplemented explicitly.

What's different compared to Project Genesis:

- I moved all the lighting into GLSL. The vertex shader transforms positions and normals and evaluates the original-style ambient plus Lambert diffuse lighting. The tunnel uses four directional lights, corresponding to OpenGL lights with `w = 0`. The initial `0.04` term is the old default scene ambient (0.2) multiplied by the default material ambient (0.2). Vertex color carries each original object's tint. There is no added specular lighting or PBR model.
- I moved the Fog effect into the fragment shader. The original exponential relationship is recreated as `exp(-density * depth)` and mixed with the scene's fog color. View-space depth is interpolated rather than reconstructed from a post-process depth texture.
- All of the original effects remain grid and quad operations. During a warp, the world image is copied to a render texture. Swirl and zoom draw it through the original 64x48 texture-coordinate grid. Radial blur draws 23 increasingly zoomed and dimmed copies. The white flash is another full-window layer with the original blending behavior.
- Snapshot coordinates retain the old measurement system. The source player copied into a 1024x1024 texture and recorded the occupied rectangle. This port uses full-size render textures but maps the old 640x480 virtual-screen coordinates onto them, preserving the effect strengths and constants.
- Effect state is managed around Raylib's batches. Pending draws are flushed before render-target, blend and depth-state changes. The frame's alpha behavior, depth mask and 2D viewport are restored when returning from a snapshot.
- The virtual image is still 640x480 with a visible band from y = 59 to y = 420. That band is 640/361, almost 16:9. `FitPicture` letterboxes it when requested rather than quietly changing the shot's vertical field of view.
- Endless flight needs a floating origin. Center-line positions are doubles on the CPU. Vertices are stored relative to section anchors, the camera stays at the origin for the world pass, and each section is translated by `anchor - eye`. Sending an hours-long absolute coordinate to a 32-bit GPU matrix would cause visible jitter and depth problems.

The off-screen image can be rendered at twice its display size and reduced afterwards to smooth the pipe edges. It is stored bottom row first, so the final blit flips it.

The small Windows control-panel preview was a huge pain the ass to get working. Raylib cannot adopt a HWND owned by the control panel applet. That preview therefore renders in a hidden Raylib window, reads back a 320x180 image and copies it to the supplied window with `StretchBlt`. This is janky and often causes light flickering. I don't know how else to do it.

## Install and Build

You need the .NET 10 SDK. Running the screen saver requires the .NET 10 Desktop Runtime and an OpenGL 3.3-capable GPU/driver. It should run fine even on basic integrated GPUs. You don't need a high performance gaming rig.

```cmd
dotnet publish -c Release
```

Raylib is packed into the single-file build. Right-click the `.scr` and choose Install, or copy it somewhere and select it in the Windows screen saver control panel applet. A `.scr` is an executable, so the same file also accepts the switches below.

During development, `dotnet run` is more convenient and leaves a normal exe beside `raylib.dll`.

If startup or rendering throws, the program writes `GenesisTunnel-error.log` beside the executable. If that directory is not writable, as is often the case under `System32`, it tries the temp directory instead.

## Command line

Switches are case-insensitive.

| Switch | Meaning |
| --- | --- |
| *(none)* | Open the settings dialog. This is what a double-clicked `.scr` does. |
| `/s` | Run fullscreen. Used by Test and by Windows when the screen saver starts. |
| `/c`, `/c HWND`, `/c:HWND` | Open settings, optionally owned by the supplied control-panel window. |
| `/p HWND`, `/p:HWND` | Run the small preview and renders it into the window specified by `HWND`. |
| `/w` | Run in a resizable 1280x722 window for debugging. <kbd>Esc</kbd> quits; <kbd>M</kbd> toggles the autopilot's map. |
| `/set Name=Value,...` | Use these values instead of the registry. Names are the properties in `Settings.cs`. Checkboxes are `0` or `1`. |
| `/shot DIR SEED SECONDS EVERY` | Run a hidden, fixed-60-Hz flight and save periodic BMPs plus `log.txt`. |
| `/map DIR SEED` | Save `DIR/map.bmp`, showing occupancy and the planned route for the opening sections. |

`/shot` and `/map` consume every argument after themselves, so `/set` has to come first. Without `/set` they use the built-in defaults, not the registry. The Preview button starts `/w /set` with the current sliders and does not write the registry.

In fullscreen, <kbd>Esc</kbd> quits immediately. Other keys, mouse buttons and real mouse movement quit after a 0.7-second grace period, preventing the click that launched a test from instantly closing it. In windowed mode, only Esc or closing the window quits; the mouse does not.

`/shot` uses simulation steps of 1/60 second regardless of rendering speed. Images are 960x540, rendered at double size and reduced. A name such as `f00500.bmp` means 5.00 seconds. The log records camera/effect state, the slowest simulation step and the number of steps whose camera cell touched a surface.

## Settings

I write DWORD values under `HKEY_CURRENT_USER\Software\GenesisTunnel`. That's how screen savers do it.

### Flight

<img width="313" height="245" alt="image" src="https://github.com/user-attachments/assets/523e9900-6971-484c-8cf9-d1b78b50c2e9" /><br><br>

| Setting | Default | Behavior |
| --- | ---: | --- |
| Speed | 100 | Percentage of the original cruise speed, approximately 25 tunnel units per second. Range 10–300. |
| Speed changes | 60 | Varies each normal mood's cruise multiplier. At zero, speed stays on the Speed setting. Changes ease in rather than jumping, except after a crash cut. |
| Roll | 100 | Percentage of the original-style roll rate. Zero keeps the floor down; values above 100 allow faster rolling. Some moods still hold the horizon level. |
| Weaving | 70 | Pulls wish points from the central line towards open cells near pipes and places them closer together. Zero follows the central wish line. |
| Looking around | 35 | Controls the chance of a slow, level, sideways look with a wider lens. At 100, roughly 45% of eligible moods become look-around passes. |

### Tunnel

<img width="313" height="245" alt="image" src="https://github.com/user-attachments/assets/ed711353-d3e5-4571-b2e8-50d3f6a76b2f" /><br><br>

| Setting | Default | Behavior |
| --- | ---: | --- |
| Pipe density | 60 | Controls continuous-pipe counts, copied groups and wandering pipes. A slow spatial drift prevents every section from feeling equally busy. Density falls automatically on route-planning retries. |
| Original sections | 25 | Percentage chance of using the intro's original layout. The choice is hashed from seed and section number. Half of later original copies are mirrored. |
| Big pipes | 35 | Controls the chance of attempting a radius-1.45 fly-through duct. About 80% of successfully placed ducts also attract the route planner. |
| Bends | 35 | Controls turn radius, angle and the spacing of straight runs. Zero keeps the original straight tunnel. |
| Slopes | 25 | Controls climb/dive steepness. Half of the slope choices are level, and changes ease in over distance. |

### Events

<img width="313" height="245" alt="image" src="https://github.com/user-attachments/assets/5c30e024-e38b-423c-950e-c7cb4e5c888d" /><br><br>

Event periods are averages. The actual wait varies between half and one-and-a-half times the setting. Zero disables the event.

| Setting | Default | Behavior |
| --- | ---: | --- |
| Warp speed every | 45 s | Starts a 4.5–7.5-second acceleration with swirl, zoom or both. Warps do not begin during a sideways look or after a crash has been planned. |
| Warp strength | 100 | Scales acceleration, extra lens angle and screen distortion. Zero leaves the timer and possible opening flash but removes the warp itself. |
| Crash every | 150 s | Selects a pipe surface a few seconds ahead and re-plans the route so that it drifts into it instead of dodging, then whites out on impact. Under the flash, the flight restarts further along the tunnel with a fresh mood. |
| Lighting change every | 60 s | Blends fog and lamp colors over four seconds. Many changes return to the original steel blue. |
| color range | 50 | Limits hue, saturation, brightness and haze changes. Hue range grows with the square of the slider, so middling values stay close to the original palette. |
| Flicker | 20 | Controls brief bursts in which lamps jump between full brightness and 10–50%. Zero disables flicker. |

### Picture

<img width="313" height="245" alt="image" src="https://github.com/user-attachments/assets/87927016-672d-4373-a14a-c069e5f620a6" /><br><br>

| Setting | Default | Behavior
| --- | ---: | --- |
| Smooth edges | On | Renders at twice the target size and reduces the result. It is skipped above 1920 pixels wide, where the extra cost stops being worthwhile. |
| Keep the original 16:9 picture | On | Preserves the intro's visible 640/361 band with black bars where necessary. Turning it off fills the monitor while retaining the vertical field of view. |
| Show the pipe map | Off | Enables the top-right route overlay. |
| Tunnel number | 0 | Random seed for tunnel contents and autopilot behavior. The same seed and settings reproduce the same flight. `0` takes a seed from the system clock. |

### About

<img width="313" height="245" alt="image" src="https://github.com/user-attachments/assets/8b2d5868-933e-41b4-bf59-0b82d952cf61" /><br><br>

Shows version and copyright information.

## Route Map

<img width="912" height="201" alt="image" src="https://github.com/user-attachments/assets/02d14447-e748-4a89-ad10-661e271e7367" />

I needed it for debugging the autopilot but then decided it looks kind of cool and kept it.

The upper strip is the tunnel from above, with the left wall at the top. The lower strip is the side view, with the ceiling at the top. Forward is to the right. A filled column or row means that some cell in that line contains a surface.

| color | Meaning |
| --- | --- |
| Gray | A pipe, fitting, duct or wall surface. |
| Red | The planned route in an open cell. |
| Yellow | The planned point is on a blocked or solid cell. This can happen near a clearance margin and is expected when the autopilot decides to commit suicide by crashing into a pipe. |
| White vertical line | The camera's current slice in the live overlay. |

The red/yellow line is the raw route, not the smoothed camera position. The camera averages several units around that line to glide similar to how Project Genesis flies through the tunnel.

