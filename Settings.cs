// Screen saver settings. The registry is the per-user store. The dialog's Preview
// button, and /shot or /map when they need a specific setup, pass the same property
// names on the command line instead of writing a file.

using Microsoft.Win32;
using System.Globalization;
using System.Reflection;

namespace GenesisTunnel;

// Property initializers are the defaults for a missing registry key, a Name=Value
// pair that /set does not mention, and the dialog's Defaults button. Keeping them
// in one place prevents those cases from drifting apart.
sealed class Settings
{
    // Camera pace and route-following behavior.
    public int Speed { get; set; } = 100; // percent of the original's cruising speed (10..300)
    public int SpeedVariation { get; set; } = 60; // how much the speed wanders up and down
    public int Roll { get; set; } = 100; // how much the camera rolls (percent of the original, 0..200)
    public int Wander { get; set; } = 70; // how much the camera weaves between the pipes
    public int LookAround { get; set; } = 35; // how often it slows down and looks sideways at the pipes

    // Section contents and center-line shape.
    public int PipeDensity { get; set; } = 60; // how full of pipes the corridor is
    public int OriginalSections { get; set; } = 25; // percent of sections that are the untouched 2003 layout
    public int BigPipes { get; set; } = 35; // how often there is a big pipe to fly through
    public int Bends { get; set; } = 35; // how much the corridor curves left and right
    public int Slopes { get; set; } = 25; // how much it climbs and dives

    // Average seconds between events. Zero disables the corresponding timer.
    public int WarpEvery { get; set; } = 45;
    public int WarpStrength { get; set; } = 100; // 0..200
    public int CrashEvery { get; set; } = 150;
    public int LightChangeEvery { get; set; } = 60;
    public int ColorRange { get; set; } = 50; // how far the lighting may drift from the original blue
    public int Flicker { get; set; } = 20; // how often the lights flicker

    // Output and debugging options.
    public bool Smooth { get; set; } = true; // draw at double size and shrink (smoother edges)
    public bool Widescreen { get; set; } = true; // keep the original 16:9 picture (black bars on other monitors)
    public bool ShowMap { get; set; } = false; // draw the pipe map and the planned route in the top right corner
    public int Seed { get; set; } = 0; // 0 = a different tunnel every time

    const string RegistryKey = @"Software\GenesisTunnel";

    static IEnumerable<PropertyInfo> Values => typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    public static Settings Load()
    {
        // Start with defaults and overwrite only values that are actually DWORDs.
        // A partly written or older key therefore remains usable.
        var s = new Settings();
        try
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryKey);
            if (key == null) return s;
            foreach (PropertyInfo p in Values)
            {
                if (key.GetValue(p.Name) is not int number) continue;
                p.SetValue(s, p.PropertyType == typeof(bool) ? number != 0 : number);
            }
        }
        catch { /* an unreadable registry just means default settings */ }
        return s;
    }

    public void Save()
    {
        // Save the complete set, not just edited values, so Defaults and subsequent
        // versions see one consistent key. Registry access errors propagate to Main.
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryKey);
        foreach (PropertyInfo p in Values)
        {
            object value = p.GetValue(this);
            key.SetValue(p.Name, value is bool b ? (b ? 1 : 0) : (int)value, RegistryValueKind.DWord);
        }
    }

    // "/set Name=Value,..." carries the dialog's unsaved sliders to the preview process,
    // and lets /shot or /map name a setup, without reading or writing the registry.
    // A missing or unreadable pair keeps the property default.

    public static Settings With(string spec)
    {
        var s = new Settings();

        if (string.IsNullOrEmpty(spec))
            return s;

        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');

            if (eq <= 0)
                continue;

            PropertyInfo p = Values.FirstOrDefault(v => v.Name.Equals(part[..eq], StringComparison.OrdinalIgnoreCase));
            if (p == null)
                continue;

            string text = part[(eq + 1)..];

            if (p.PropertyType == typeof(bool) && (text is "0" or "1"))
                p.SetValue(s, text == "1");
            else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                p.SetValue(s, number);
        }

        return s;
    }

    public string Encode()
    {
        var parts = new List<string>();

        foreach (PropertyInfo p in Values)
        {
            object value = p.GetValue(this);
            string text = value is bool b ?
                (b ? "1" : "0") :
                ((int)value).ToString(CultureInfo.InvariantCulture);
            parts.Add(p.Name + "=" + text);
        }

        return string.Join(',', parts);
    }
}
