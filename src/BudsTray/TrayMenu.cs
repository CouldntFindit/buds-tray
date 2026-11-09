using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using BudsTray.Buds;

namespace BudsTray;

/// <summary>
/// The tray menu: Left, Right and Case percentages, the sound modes the
/// connected model supports, Start with Windows, and Exit. It is a plain
/// <see cref="ContextMenuStrip"/> with a custom renderer so it can follow the
/// app theme instead of always drawing the light system chrome.
/// </summary>
internal sealed class TrayMenu : ContextMenuStrip
{
    private const int BasePadX = 14;

    private readonly Func<bool> _connected;
    private readonly Func<BatteryStatus?> _status;
    private readonly Func<NoiseControlMode?> _noiseMode;
    private readonly Func<string?> _deviceName;
    private readonly Action<NoiseControlMode> _setNoiseMode;
    private readonly AppConfig _config;

    private readonly BatteryRow _leftRow = new("Left");
    private readonly BatteryRow _rightRow = new("Right");
    private readonly BatteryRow _caseRow = new("Case");

    private readonly ModeRow _ancRow = new(NoiseControlMode.Anc);
    private readonly ModeRow _ambientRow = new(NoiseControlMode.Ambient);
    private readonly ModeRow _adaptiveRow = new(NoiseControlMode.Adaptive);
    private readonly ModeRow _offRow = new(NoiseControlMode.Off);

    private readonly ToolStripSeparator _batterySeparator = new();
    private readonly ToolStripSeparator _settingsSeparator = new();
    private readonly ToggleRow _startupRow = new("Start with Windows");
    private readonly ToolStripMenuItem _exitItem = new("Exit");

    private MenuTheme _theme = MenuTheme.Dark;

    public TrayMenu(
        Func<bool> connected,
        Func<BatteryStatus?> status,
        Func<NoiseControlMode?> noiseMode,
        Func<string?> deviceName,
        Action<NoiseControlMode> setNoiseMode,
        AppConfig config,
        Action exit)
    {
        _connected = connected;
        _status = status;
        _noiseMode = noiseMode;
        _deviceName = deviceName;
        _setNoiseMode = setNoiseMode;
        _config = config;

        ShowImageMargin = false;
        ShowCheckMargin = false;
        AutoSize = true;
        Padding = new Padding(0, 4, 0, 4);

        Items.AddRange(new ToolStripItem[]
        {
            _leftRow,
            _rightRow,
            _caseRow,
            _batterySeparator,
            _ancRow,
            _ambientRow,
            _adaptiveRow,
            _offRow,
            _settingsSeparator,
            _startupRow,
            _exitItem
        });

        _ancRow.Click += (_, _) => SelectMode(_ancRow);
        _ambientRow.Click += (_, _) => SelectMode(_ambientRow);
        _adaptiveRow.Click += (_, _) => SelectMode(_adaptiveRow);
        _offRow.Click += (_, _) => SelectMode(_offRow);
        _startupRow.Click += (_, _) => ToggleStartup();

        _exitItem.Padding = new Padding(10, 1, 10, 1);
        _exitItem.Click += (_, _) => exit();

        Opening += (_, _) =>
        {
            ApplyTheme();
            RefreshValues();
            _startupRow.Active = StartupManager.IsEnabled();
            _startupRow.Invalidate();
        };

        ApplyTheme();
        RefreshValues();
    }

    /// <summary>Updates the rows without reopening the menu.</summary>
    public void RefreshValues()
    {
        var connected = _connected();
        var status = connected ? _status() : null;

        SetRow(_leftRow, connected, status?.Left, status?.LeftCharging == true);
        SetRow(_rightRow, connected, status?.Right, status?.RightCharging == true);

        // A case that is not reporting (level 0) must show "--", not a
        // misleading 0 %.
        var caseLevel = status is { CaseReported: true } ? status.Case : null;
        SetRow(_caseRow, connected, caseLevel, status?.CaseCharging == true);

        RefreshNoise(connected);
    }

    private void RefreshNoise(bool connected)
    {
        // The last status frame is kept after a disconnect, so the model's modes
        // stay in place (dimmed) instead of making the menu jump around.
        var status = _status();
        var support = NoiseControl.SupportFor(
            status?.EarType ?? _config.LastEarType, _deviceName());
        var current = connected ? _noiseMode() : null;

        SetModeRow(_ancRow, support, connected, current);
        SetModeRow(_ambientRow, support, connected, current);
        SetModeRow(_adaptiveRow, support, connected, current);
        SetModeRow(_offRow, support, connected, current);

        _batterySeparator.Visible = true;
        _batterySeparator.Available = true;
        _settingsSeparator.Visible = true;
        _settingsSeparator.Available = true;
    }

    private void SelectMode(ModeRow row)
    {
        if (!row.Enabled || !_connected()) return;

        _setNoiseMode(row.Mode);
    }

    private void ToggleStartup()
    {
        _startupRow.Active = StartupManager.Toggle();
        _startupRow.Invalidate();
    }

    private void SetRow(BatteryRow row, bool connected, int? value, bool charging)
    {
        var text = connected && value is { } level ? $"{level}%" : "--";
        var color = !connected || value is null
            ? _theme.Dim
            : charging
                ? _theme.Charging
                : value <= BatteryStatus.LowThreshold
                    ? _theme.Danger
                    : _theme.Text;

        if (row.Value == text && row.ValueColor == color) return;

        row.Value = text;
        row.ValueColor = color;
        row.Invalidate();
    }

    private void SetModeRow(
        ModeRow row,
        NoiseControlSupport support,
        bool connected,
        NoiseControlMode? current)
    {
        var supported = NoiseControl.IsSupported(support, row.Mode);
        var enabled = supported && connected;
        var active = supported && connected && current == row.Mode;

        if (row.Visible != supported) row.Visible = supported;
        if (row.Available != supported) row.Available = supported;
        if (row.Enabled != enabled) row.Enabled = enabled;
        if (row.Active != active) row.Active = active;

        row.Theme = _theme;
        row.Invalidate();
    }

    private void ApplyTheme()
    {
        var theme = MenuTheme.For(ThemeUtil.IsLight(_config.Theme));
        _theme = theme;

        Renderer = new MenuRenderer(theme);
        BackColor = theme.Background;
        ForeColor = theme.Text;

        _leftRow.Theme = theme;
        _rightRow.Theme = theme;
        _caseRow.Theme = theme;

        _ancRow.Theme = theme;
        _ambientRow.Theme = theme;
        _adaptiveRow.Theme = theme;
        _offRow.Theme = theme;

        _startupRow.Theme = theme;
        _exitItem.BackColor = theme.Background;
        _exitItem.ForeColor = theme.Text;
    }

    /// <summary>
    /// A disabled menu item that draws a label on the left and its value on the
    /// right, so the percentages line up no matter how many digits they have.
    /// </summary>
    private sealed class BatteryRow : ToolStripMenuItem
    {
        public BatteryRow(string text) : base(text)
        {
            Enabled = false;
            AutoSize = true;
            ShowShortcutKeys = true;

            // The dropdown sizes its items from the text plus the shortcut
            // column, so this invisible placeholder reserves the right-hand
            // space that OnPaint uses for the percentage.
            ShortcutKeyDisplayString = "000000000000";
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public MenuTheme Theme { get; set; } = MenuTheme.Dark;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Value { get; set; } = "--";

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color ValueColor { get; set; } = Color.White;

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            var bounds = new Rectangle(Point.Empty, Size);
            var padX = (int)Math.Round(BasePadX * (Owner?.DeviceDpi ?? 96) / 96f);

            using (var background = new SolidBrush(Theme.Background))
                graphics.FillRectangle(background, bounds);

            var textBounds = new Rectangle(
                padX, 0,
                Math.Max(0, bounds.Width - padX * 2),
                bounds.Height);

            const TextFormatFlags flags =
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine;

            TextRenderer.DrawText(graphics, Text, Font, textBounds, Theme.Text,
                flags | TextFormatFlags.Left);
            TextRenderer.DrawText(graphics, Value, Font, textBounds, ValueColor,
                flags | TextFormatFlags.Right);
        }
    }

    /// <summary>
    /// A clickable row that paints its own hover state and marks the active
    /// choice with a check on the right, which keeps the same alignment as the
    /// battery rows regardless of the system check margin.
    /// </summary>
    private class CheckRow : ToolStripMenuItem
    {
        protected CheckRow(string text) : base(text)
        {
            AutoSize = true;
            ShowShortcutKeys = true;
            ShortcutKeyDisplayString = "000000000000";
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public MenuTheme Theme { get; set; } = MenuTheme.Dark;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Active { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            var bounds = new Rectangle(Point.Empty, Size);
            var padX = (int)Math.Round(BasePadX * (Owner?.DeviceDpi ?? 96) / 96f);

            using (var background = new SolidBrush(Selected ? Theme.Hover : Theme.Background))
                graphics.FillRectangle(background, bounds);

            var textBounds = new Rectangle(
                padX, 0,
                Math.Max(0, bounds.Width - padX * 2),
                bounds.Height);

            const TextFormatFlags flags =
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine;

            TextRenderer.DrawText(graphics, Text, Font, textBounds,
                Enabled ? Theme.Text : Theme.Dim, flags | TextFormatFlags.Left);

            if (Active)
                TextRenderer.DrawText(graphics, "✓", Font, textBounds,
                    Theme.Accent, flags | TextFormatFlags.Right);
        }
    }

    private sealed class ModeRow : CheckRow
    {
        public ModeRow(NoiseControlMode mode) : base(NoiseControl.Label(mode)) => Mode = mode;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public NoiseControlMode Mode { get; }
    }

    private sealed class ToggleRow : CheckRow
    {
        public ToggleRow(string text) : base(text) { }
    }

    private sealed record MenuTheme(
        Color Background,
        Color Border,
        Color Text,
        Color Dim,
        Color Hover,
        Color Separator,
        Color Danger,
        Color Charging,
        Color Accent)
    {
        public static MenuTheme For(bool light) => light ? Light : Dark;

        public static readonly MenuTheme Dark = new(
            Background: Color.FromArgb(255, 30, 30, 30),
            Border: Color.FromArgb(255, 58, 58, 58),
            Text: Color.FromArgb(255, 255, 255, 255),
            Dim: Color.FromArgb(255, 158, 158, 158),
            Hover: Color.FromArgb(255, 51, 51, 51),
            Separator: Color.FromArgb(255, 58, 58, 58),
            Danger: Color.FromArgb(255, 255, 107, 107),
            Charging: Color.FromArgb(255, 53, 208, 127),
            Accent: Color.FromArgb(255, 96, 165, 250));

        public static readonly MenuTheme Light = new(
            Background: Color.FromArgb(255, 255, 255, 255),
            Border: Color.FromArgb(255, 214, 214, 214),
            Text: Color.FromArgb(255, 17, 17, 17),
            Dim: Color.FromArgb(255, 106, 106, 106),
            Hover: Color.FromArgb(255, 242, 242, 242),
            Separator: Color.FromArgb(255, 226, 226, 226),
            Danger: Color.FromArgb(255, 211, 47, 47),
            Charging: Color.FromArgb(255, 15, 123, 15),
            Accent: Color.FromArgb(255, 0, 103, 192));
    }

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly MenuTheme _theme;

        public MenuRenderer(MenuTheme theme) : base(new MenuColorTable(theme))
        {
            _theme = theme;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? _theme.Text : _theme.Dim;

            // The battery and mode rows paint their own text at BasePadX, so
            // nudge the remaining items (Exit) to the same left edge.
            if (e.Item is not BatteryRow and not ModeRow)
            {
                var inset = (int)Math.Round(
                    (BasePadX - 4) * (e.Item.Owner?.DeviceDpi ?? 96) / 96f);
                var rectangle = e.TextRectangle;
                rectangle.X += inset;
                e.TextRectangle = rectangle;
            }

            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
            bounds.Width -= 1;
            bounds.Height -= 1;

            using var pen = new Pen(_theme.Border);
            e.Graphics.DrawRectangle(pen, bounds);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(_theme.Separator);
            e.Graphics.DrawLine(pen, e.Item.ContentRectangle.Left + 10, y,
                e.Item.ContentRectangle.Right - 10, y);
        }
    }

    private sealed class MenuColorTable : ProfessionalColorTable
    {
        private readonly MenuTheme _theme;

        public MenuColorTable(MenuTheme theme) => _theme = theme;

        public override Color ToolStripDropDownBackground => _theme.Background;
        public override Color ImageMarginGradientBegin => _theme.Background;
        public override Color ImageMarginGradientMiddle => _theme.Background;
        public override Color ImageMarginGradientEnd => _theme.Background;
        public override Color MenuBorder => _theme.Border;
        public override Color MenuItemBorder => _theme.Hover;
        public override Color MenuItemSelected => _theme.Hover;
        public override Color MenuItemSelectedGradientBegin => _theme.Hover;
        public override Color MenuItemSelectedGradientEnd => _theme.Hover;
        public override Color MenuItemPressedGradientBegin => _theme.Hover;
        public override Color MenuItemPressedGradientMiddle => _theme.Hover;
        public override Color MenuItemPressedGradientEnd => _theme.Hover;
        public override Color SeparatorDark => _theme.Separator;
        public override Color SeparatorLight => _theme.Separator;
    }
}
