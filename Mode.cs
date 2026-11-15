// Parsed process mode plus the optional HWND, /set spec and trailing tool arguments.

namespace GenesisTunnel;

sealed class Mode
{
    public bool Fullscreen = true, Config, Preview, Shot, Map;
    public nint Owner;
    public string SetSpec;
    public string[] Rest = [];

    // /set replaces the registry for this process. Windows never passes it, so the
    // control-panel preview still shows the saved setup.
    public Settings LoadSettings()
    {
        return SetSpec != null ? Settings.With(SetSpec) : Settings.Load();
    }

    // /shot and /map stay on property defaults unless /set names a setup. They do not
    // read the registry, so a test run does not depend on the installed screen saver.
    public Settings ToolSettings()
    {
        return SetSpec != null ? Settings.With(SetSpec) : new Settings();
    }

    public static Mode Parse(string[] args)
    {
        // /shot and /map deliberately stop parsing: their variable trailing argument
        // list should never be mistaken for another screen saver switch.
        var m = new Mode();
        // By Windows convention a screensaver started without any switch shows its settings.
        if (args.Length == 0) m.Config = true;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].Trim(), low = a.ToLowerInvariant();
            if (low is "/set" or "-set")
            {
                if (i + 1 < args.Length) m.SetSpec = args[++i];
            }
            else if (low is "/w" or "-w" or "/window") m.Fullscreen = false;
            else if (low is "/shot" or "-shot")
            {
                m.Shot = true;
                m.Rest = [.. args.Skip(i + 1)];
                break;
            }
            else if (low is "/map" or "-map")
            {
                m.Map = true;
                m.Rest = [.. args.Skip(i + 1)];
                break;
            }
            else if (low is "/s" or "-s") m.Fullscreen = true;
            else if (low.StartsWith("/c") || low.StartsWith("-c"))
            {
                m.Config = true;
                m.Owner = ReadHwnd(a, args, ref i);
            }
            else if (low.StartsWith("/p") || low.StartsWith("-p"))
            {
                m.Preview = true;
                m.Owner = ReadHwnd(a, args, ref i);
            }
        }
        return m;
    }

    static nint ReadHwnd(string arg, string[] args, ref int i)
    {
        // Accept decimal handles in either Windows form. Returning zero is safe:
        // config becomes unowned and preview exits instead of drawing to a bad HWND.
        string rest = null;
        int colon = arg.IndexOf(':');
        if (colon >= 0 && colon < arg.Length - 1) rest = arg[(colon + 1)..];
        else if (i + 1 < args.Length && long.TryParse(args[i + 1], out _)) rest = args[++i];
        return long.TryParse(rest, out long h) ? (nint)h : 0;
    }
}
