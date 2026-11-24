// The settings dialog used by "GenesisTunnel.scr /c" and by a double-clicked .scr.
// The flight itself is raylib. WinForms is this dialog, plus the shader-failure box in Program.
//
// ConfigForm.Designer.cs owns the layout, captions, slider ranges and tooltips and
// is edited in the Visual Studio designer. This file binds the controls to Settings
// by property name, which keeps adding or rearranging sliders fairly painless. OK is
// the only action that touches the registry. Preview passes the current values to
// a new process on the command line, so experimenting cannot spoil the saved setup.

using System.Diagnostics;
using System.Windows.Forms;

namespace GenesisTunnel;

public partial class ConfigForm : Form
{
    Settings cfg;
    readonly List<Action> showValues = [];

    public static void Run(nint owner)
    {
        // DPI mode has to be chosen before WinForms creates its first window. The
        // flight never comes through here, so raylib's window is unaffected.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var form = new ConfigForm();

        if (owner != 0)
            form.ShowDialog(new OwnerWindow(owner));
        else Application.Run(form);
    }

    // Adapt the HWND supplied with /c to the owner interface WinForms expects.
    // Without an owner, the control panel can easily end up covering the dialog.
    sealed class OwnerWindow(nint handle) : IWin32Window
    {
        public nint Handle { get; } = handle;
    }

    static void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* No default browser: nothing useful to do. */ }
    }

    public ConfigForm()
    {
        InitializeComponent();
    }

    // Bindings are made on load rather than in the constructor so that nothing here
    // (registry, process icon) runs while the Visual Studio designer builds the form.
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // The designer supplies usable defaults. The bindings below then replace
        // them with registry values and keep cfg in step with the controls.
        cfg = Settings.Load();

        AboutVersionLabel.Text = "Version " + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?");
        AboutProjectLinkLabel.LinkClicked += (_, _) => OpenLink(AboutProjectLinkLabel.Text);
        AboutCreditsRichTextBox.LinkClicked += (_, args) => OpenLink(args.LinkText);

        Slider(SpeedTrackBar, SpeedValueLabel, nameof(Settings.Speed), "%");
        Slider(SpeedVariationTrackBar, SpeedVariationValueLabel, nameof(Settings.SpeedVariation), "");
        Slider(RollTrackBar, RollValueLabel, nameof(Settings.Roll), "%");
        Slider(WanderTrackBar, WanderValueLabel, nameof(Settings.Wander), "");
        Slider(LookAroundTrackBar, LookAroundValueLabel, nameof(Settings.LookAround), "");

        Slider(PipeDensityTrackBar, PipeDensityValueLabel, nameof(Settings.PipeDensity), "");
        Slider(OriginalSectionsTrackBar, OriginalSectionsValueLabel, nameof(Settings.OriginalSections), "%");
        Slider(BigPipesTrackBar, BigPipesValueLabel, nameof(Settings.BigPipes), "");
        Slider(BendsTrackBar, BendsValueLabel, nameof(Settings.Bends), "");
        Slider(SlopesTrackBar, SlopesValueLabel, nameof(Settings.Slopes), "");

        Slider(WarpEveryTrackBar, WarpEveryValueLabel, nameof(Settings.WarpEvery), "s");
        Slider(WarpStrengthTrackBar, WarpStrengthValueLabel, nameof(Settings.WarpStrength), "%");
        Slider(CrashEveryTrackBar, CrashEveryValueLabel, nameof(Settings.CrashEvery), "s");
        Slider(LightChangeEveryTrackBar, LightChangeEveryValueLabel, nameof(Settings.LightChangeEvery), "s");
        Slider(ColorRangeTrackBar, ColorRangeValueLabel, nameof(Settings.ColorRange), "");
        Slider(FlickerTrackBar, FlickerValueLabel, nameof(Settings.Flicker), "");

        Check(SmoothCheckBox, nameof(Settings.Smooth));
        Check(WidescreenCheckBox, nameof(Settings.Widescreen));
        Check(ShowMapCheckBox, nameof(Settings.ShowMap));
        Number(SeedNumericUpDown, nameof(Settings.Seed));

        ShowValues();
    }

    void PreviewButton_Click(object sender, EventArgs e)
    {
        Preview();
    }

    void DefaultsButton_Click(object sender, EventArgs e)
    {
        cfg = new Settings();
        ShowValues();
    }

    void OkButton_Click(object sender, EventArgs e)
    {
        cfg.Save();
        Close();
    }

    void ShowValues()
    {
        // Assigning a control can raise its change event, but that merely writes
        // the same value back to cfg. It also makes Defaults use this same path.
        foreach (Action show in showValues) show();
    }

    // Reflection is confined to these two helpers. It saves a dozen near-identical
    // event handlers while keeping Settings as an ordinary property-based class.
    int Get(string name)
    {
        return (int)typeof(Settings).GetProperty(name).GetValue(cfg);
    }

    void Set(string name, object value)
    {
        typeof(Settings).GetProperty(name).SetValue(cfg, value);
    }

    void Slider(TrackBar bar, Label value, string setting, string unit)
    {
        // Event periods read more naturally as "never" at zero; the other sliders
        // show either a percentage or their plain numeric value.
        string Describe(int v)
        {
            return unit == "s" ? (v == 0 ? "never" : v + " s") : v + (unit == "%" ? " %" : "");
        }

        bar.ValueChanged += (_, _) =>
        {
            Set(setting, bar.Value);
            value.Text = Describe(bar.Value);
        };
        showValues.Add(() =>
        {
            bar.Value = Math.Clamp(Get(setting), bar.Minimum, bar.Maximum);
            value.Text = Describe(bar.Value);
        });
    }

    void Check(CheckBox box, string setting)
    {
        // Checkboxes use the same property-name convention as the sliders, but
        // there are too few of them to justify another reflection helper.
        box.CheckedChanged += (_, _) => Set(setting, box.Checked);
        showValues.Add(() => box.Checked = (bool)typeof(Settings).GetProperty(setting).GetValue(cfg));
    }

    void Number(NumericUpDown box, string setting)
    {
        // Clamp when loading as well as when editing. An old registry value therefore
        // cannot make NumericUpDown throw.
        box.ValueChanged += (_, _) => Set(setting, (int)box.Value);
        showValues.Add(() => box.Value = Math.Clamp(Get(setting), (int)box.Minimum, (int)box.Maximum));
    }

    void Preview()
    {
        try
        {
            // A separate process gets its own raylib window and leaves this dialog
            // responsive. ProcessPath also works after the exe has been renamed .scr.
            // The spec is names and numbers, so it needs no quoting.
            Process.Start(new ProcessStartInfo(Environment.ProcessPath, "/w /set " + cfg.Encode()) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DialogCancelButton_Click(object sender, EventArgs e)
    {
        Close();
    }
}
