using System.Drawing;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace AmbientMonitor;

internal sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly Icon _ownedIcon;
    private readonly WinForms.ToolStripMenuItem _toggle;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Func<bool> _isRunning;
    private readonly Func<bool> _isDark;
    private readonly Func<IReadOnlyList<TrayPresetItem>> _presets;
    private readonly Action<string> _selectPreset;
    private readonly WinForms.ToolStripMenuItem _presetMenu;
    private readonly Func<bool> _automationPaused;
    private readonly WinForms.ToolStripMenuItem _pauseAutomation;

    public TrayIcon(Action show, Action toggle, Action exit, Func<bool> isRunning,
        Func<bool> isDark, Func<IReadOnlyList<TrayPresetItem>> presets, Action<string> selectPreset, Func<bool> automationPaused, Action toggleAutomationPause)
    {
        _isRunning = isRunning;
        _isDark = isDark; _presets = presets; _selectPreset = selectPreset;
        _automationPaused = automationPaused;
        _ownedIcon = LoadIcon();
        _menu = new WinForms.ContextMenuStrip();
        _menu.Items.Add("Open Ambient Monitor", null, (_, _) => show());
        _toggle = new WinForms.ToolStripMenuItem("Turn overlays on", null, (_, _) => toggle());
        _menu.Items.Add(_toggle);
        _presetMenu = new WinForms.ToolStripMenuItem("Profiles / themes");
        _menu.Items.Add(_presetMenu);
        _pauseAutomation = new WinForms.ToolStripMenuItem("Pause automation", null, (_, _) => toggleAutomationPause());
        _menu.Items.Add(_pauseAutomation);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => exit());
        _menu.Opening += (_, _) => RefreshMenu();
        _icon = new WinForms.NotifyIcon { Icon = _ownedIcon, Text = "Ambient Monitor", ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => show();
    }
    public void Update(bool running) => _icon.Text = running ? "Ambient Monitor · overlays on" : "Ambient Monitor · overlays off";
    private void RefreshMenu()
    {
        _toggle.Text = _isRunning() ? "Turn overlays off" : "Turn overlays on";
        _pauseAutomation.Text = _automationPaused() ? "Resume automation" : "Pause automation";
        _pauseAutomation.Checked = _automationPaused();
        bool dark = _isDark();
        _menu.Renderer = new TrayMenuRenderer(dark);
        _menu.BackColor = dark ? Color.FromArgb(38, 38, 42) : Color.FromArgb(250, 250, 250);
        _menu.ForeColor = dark ? Color.WhiteSmoke : Color.FromArgb(25, 25, 25);
        while (_presetMenu.DropDownItems.Count > 0)
        {
            var item = _presetMenu.DropDownItems[0]; _presetMenu.DropDownItems.RemoveAt(0); item.Dispose();
        }
        _presetMenu.DropDown.Renderer = _menu.Renderer;
        foreach (var preset in _presets())
        {
            var item = new WinForms.ToolStripMenuItem(preset.Name.Replace("&", "&&")) { Checked = preset.Selected };
            item.Click += (_, _) => _selectPreset(preset.Id);
            _presetMenu.DropDownItems.Add(item);
        }
        if (_presetMenu.DropDownItems.Count == 0) _presetMenu.DropDownItems.Add(new WinForms.ToolStripMenuItem("No saved profiles") { Enabled = false });
        ApplyColors(_menu.Items, _menu.BackColor, _menu.ForeColor);
    }
    private static void ApplyColors(WinForms.ToolStripItemCollection items, Color background, Color foreground)
    {
        foreach (WinForms.ToolStripItem item in items)
        {
            item.BackColor = background; item.ForeColor = foreground;
            if (item is WinForms.ToolStripMenuItem submenu)
            {
                submenu.DropDown.BackColor = background; submenu.DropDown.ForeColor = foreground;
                ApplyColors(submenu.DropDownItems, background, foreground);
            }
        }
    }
    private static Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/AmbientMonitor;component/Ambient_Monitor_Icon.png"));
        if (resource is null) return (Icon)SystemIcons.Application.Clone();
        using var stream = resource.Stream;
        using var original = new Bitmap(stream);
        using var small = new Bitmap(original, new Size(32, 32));
        nint handle = small.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); _ownedIcon.Dispose(); }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}

internal sealed record TrayPresetItem(string Id, string Name, bool Selected);
internal sealed class TrayMenuRenderer(bool dark) : WinForms.ToolStripProfessionalRenderer(new TrayMenuColors(dark))
{
    protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e)
    {
        if (e.Item is not null) e.ArrowColor = e.Item.ForeColor;
        base.OnRenderArrow(e);
    }
    protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
    {
        var box = e.ImageRectangle;
        using var pen = new Pen(e.Item.ForeColor, 2);
        var smoothing = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(box.Left + box.Width * 0.2f, box.Top + box.Height * 0.5f),
            new PointF(box.Left + box.Width * 0.42f, box.Top + box.Height * 0.72f),
            new PointF(box.Left + box.Width * 0.8f, box.Top + box.Height * 0.25f)
        });
        e.Graphics.SmoothingMode = smoothing;
    }
}
internal sealed class TrayMenuColors(bool dark) : WinForms.ProfessionalColorTable
{
    private Color Surface => dark ? Color.FromArgb(38, 38, 42) : Color.FromArgb(250, 250, 250);
    private Color Hover => dark ? Color.FromArgb(65, 65, 72) : Color.FromArgb(225, 225, 232);
    public override Color ToolStripDropDownBackground => Surface;
    public override Color ImageMarginGradientBegin => Surface;
    public override Color ImageMarginGradientMiddle => Surface;
    public override Color ImageMarginGradientEnd => Surface;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemBorder => Hover;
    public override Color MenuBorder => dark ? Color.FromArgb(80, 80, 86) : Color.FromArgb(200, 200, 206);
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color CheckBackground => Hover;
    public override Color CheckSelectedBackground => Hover;
    public override Color CheckPressedBackground => Hover;
    public override Color SeparatorDark => MenuBorder;
    public override Color SeparatorLight => Surface;
}
