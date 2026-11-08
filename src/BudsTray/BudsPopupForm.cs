using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BudsTray.Buds;

namespace BudsTray;

/// <summary>
/// The right-click surface. It follows the Galaxy Buds app card layout (product
/// art, big device name, ring gauges) but uses the chrome of the running OS:
/// rounded corners and a soft shadow on Windows 11, square corners on Windows 10.
/// The whole card is custom painted so light and dark themes stay consistent.
/// </summary>
public sealed class BudsPopupForm : Form
{
    private enum Page
    {
        Main,
        Settings
    }

    private const int BaseWidth = 360;
    private const int BaseHeight = 410;
    private const int SettingsRowStart = 132;
    private const int SettingsRowStep = 44;

    private readonly Func<PopupSnapshot> _snapshot;
    private readonly AppConfig _config;
    private readonly Action _reconnect;
    private readonly Action _exit;
    private readonly Action _closeSamsung;
    private readonly Action _iconOptionsChanged;

    private readonly List<Hit> _hits = new();
    private Hit? _hover;
    private Page _page = Page.Main;
    private Palette _palette;

    private int _fontDpi;
    private Font? _nameFont;
    private Font? _titleFont;
    private Font? _statusFont;
    private Font? _bodyFont;
    private Font? _smallFont;
    private Font? _valueFont;
    private Font? _labelFont;
    private Font? _iconFont;
    private Font? _iconFontSmall;

    private sealed record Hit(RectangleF Bounds, Action Action);

    public BudsPopupForm(
        Func<PopupSnapshot> snapshot,
        AppConfig config,
        Action reconnect,
        Action exit,
        Action closeSamsung,
        Action iconOptionsChanged)
    {
        _snapshot = snapshot;
        _config = config;
        _reconnect = reconnect;
        _exit = exit;
        _closeSamsung = closeSamsung;
        _iconOptionsChanged = iconOptionsChanged;

        _palette = Palette.For(ThemeUtil.IsLight(_config.Theme));

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = _palette.Background;

        SetStyle(
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);

        ApplySize();
    }

    public void RefreshData()
    {
        if (IsDisposed) return;

        // The settings page grows by one row while the Samsung app is running.
        if (_page == Page.Settings) ApplySize();
        Invalidate();
    }

    public void NavigateToSettings()
    {
        SetPage(Page.Settings);
    }

    public void ShowNearCursor()
    {
        var cursor = Cursor.Position;
        var screen = Screen.FromPoint(cursor).WorkingArea;

        var x = cursor.X > screen.Left + screen.Width / 2
            ? cursor.X - Width + Scale(12)
            : cursor.X - Scale(12);

        var y = cursor.Y > screen.Top + screen.Height / 2
            ? cursor.Y - Height - Scale(12)
            : cursor.Y + Scale(12);

        x = Math.Clamp(x, screen.Left + Scale(8), screen.Right - Width - Scale(8));
        y = Math.Clamp(y, screen.Top + Scale(8), screen.Bottom - Height - Scale(8));

        Location = new Point(x, y);
        Show();
        Activate();
        SetForegroundWindow(Handle);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplySize();

        if (ThemeUtil.IsWindows11)
        {
            var preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */,
                ref preference, sizeof(int));
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplySize();
        Invalidate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var hit = _hits.LastOrDefault(h => h.Bounds.Contains(e.Location));
        if (ReferenceEquals(hit, _hover)) return;

        _hover = hit;
        Cursor = hit is null ? Cursors.Default : Cursors.Hand;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is null) return;

        _hover = null;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;

        _hits.LastOrDefault(h => h.Bounds.Contains(e.Location))?.Action();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        EnsureFonts();
        _palette = Palette.For(ThemeUtil.IsLight(_config.Theme));
        _hits.Clear();

        var data = _snapshot();

        DrawBackground(g);
        if (_page == Page.Main) DrawMain(g, data);
        else DrawSettings(g, data);
        DrawBorder(g);
    }

    private void ApplySize()
    {
        var height = _page == Page.Settings ? SettingsHeight(_snapshot()) : BaseHeight;
        var size = new Size(Scale(BaseWidth), Scale(height));

        if (ClientSize == size) return;

        ClientSize = size;
        KeepOnScreen();
    }

    /// <summary>Number of action rows on the settings page.</summary>
    private static int SettingsRowCount(PopupSnapshot data) => 6 + (data.SamsungRunning ? 1 : 0);

    /// <summary>Height that fits the current settings rows exactly, without a dead gap.</summary>
    private static int SettingsHeight(PopupSnapshot data)
        => SettingsRowStart + SettingsRowCount(data) * SettingsRowStep + 48;

    /// <summary>Keeps the top-left anchored but pulls the card back inside the work area after a resize.</summary>
    private void KeepOnScreen()
    {
        if (!IsHandleCreated || WindowState != FormWindowState.Normal) return;

        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = Math.Clamp(Left, screen.Left + Scale(8), Math.Max(screen.Left + Scale(8), screen.Right - Width - Scale(8)));
        var y = Math.Clamp(Top, screen.Top + Scale(8), Math.Max(screen.Top + Scale(8), screen.Bottom - Height - Scale(8)));

        Location = new Point(x, y);
    }

    private float DpiScale => DeviceDpi / 96f;

    private int Scale(int value) => (int)Math.Round(value * DpiScale);

    private float S(float value) => value * DpiScale;

    private RectangleF R(float x, float y, float width, float height)
        => new(x * DpiScale, y * DpiScale, width * DpiScale, height * DpiScale);

    private void DrawBackground(Graphics g)
    {
        // Flat, uncluttered card: pitch black in the dark theme and pure white
        // in the light one, with no gradient or coloured glow behind the art.
        using var brush = new SolidBrush(_palette.Background);
        g.FillRectangle(brush, ClientRectangle);
    }

    private void DrawBorder(Graphics g)
    {
        var p = _palette;
        using var pen = new Pen(p.Border, Math.Max(1f, S(1f)));

        if (ThemeUtil.IsWindows11)
        {
            using var path = RoundedRect(
                new RectangleF(S(0.5f), S(0.5f), Width - S(1f), Height - S(1f)), S(10f));
            g.DrawPath(pen, path);
        }
        else
        {
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    private void DrawMain(Graphics g, PopupSnapshot data)
    {
        var p = _palette;
        var w = BaseWidth;
        var status = data.Status;
        var light = ThemeUtil.IsLight(_config.Theme);

        // Top-right quick actions: reconnect, settings, theme.
        DrawIconButton(g, w - 16 - 34, 14, "\uE72C", _reconnect);
        DrawIconButton(g, w - 16 - 34 - 40, 14, "\uE713", () => SetPage(Page.Settings));
        DrawIconButton(g, w - 16 - 34 - 80, 14, light ? "\uE706" : "\uE708", ToggleTheme);

        var artArea = R(70, 46, 220, 118);
        if (!BudsArt.Draw(g, artArea, status?.EarType, data.DeviceName, light))
            DrawEarbudsArt(g, artArea);

        var modelName = BudsArt.ModelName(status?.EarType, data.DeviceName);
        if (modelName is not null)
            DrawString(g, modelName, _smallFont!, p.Tertiary,
                R(24, 168, w - 48, 16), StringAlignment.Center);

        DrawString(g, data.DeviceName, _nameFont!, p.Primary,
            R(24, 188, w - 48, 38), StringAlignment.Center, StringAlignment.Center,
            StringTrimming.EllipsisCharacter);

        var (statusText, dotColor) = DescribeStatus(data);
        var statusWidth = g.MeasureString(statusText, _statusFont!).Width;
        var statusX = S(w) / 2f - statusWidth / 2f;

        using (var dot = new SolidBrush(dotColor))
            g.FillEllipse(dot, statusX - S(12), S(237), S(6), S(6));

        DrawString(g, statusText, _statusFont!, p.Secondary,
            R(24, 230, w - 48, 20), StringAlignment.Center);

        var single = status?.IsSingleBud == true;

        // In single-bud mode the bud that is in the case (or disconnected) is
        // left out of the gauge so it matches the tray icon.
        var budLabel = single
            ? status!.LeftPresent ? "Left" : "Right"
            : "Left • Right";
        var budValue = single
            ? status!.LeftPresent ? status.Left : status.Right
            : status?.LowestBattery;
        var budCharging = single
            ? status!.LeftPresent ? status.LeftCharging : status.RightCharging
            : status?.AnyBudCharging == true;

        DrawGauge(g, 118, 288, 74, budValue, budCharging, buds: true);
        DrawGauge(g, 242, 288, 74, status?.Case, status?.CaseCharging == true, buds: false);

        DrawGaugeLabel(g, 118, 334, budLabel, budValue);
        DrawGaugeLabel(g, 242, 334, "Case", status?.Case);

        var perBud = status is null
            ? "L --   R --"
            : single
                ? status.LeftPresent
                    ? $"L {Format(status.Left)}   R not in use"
                    : $"L not in use   R {Format(status.Right)}"
                : $"L {Format(status.Left)}   R {Format(status.Right)}";
        DrawString(g, perBud, _smallFont!, p.Tertiary,
            R(24, 356, w - 48, 18), StringAlignment.Center);

        DrawString(g, "Earbud levels refresh every 15 s", _smallFont!, p.Tertiary,
            R(24, 382, w - 48, 16), StringAlignment.Center);
    }

    private void DrawSettings(Graphics g, PopupSnapshot data)
    {
        var p = _palette;
        var w = BaseWidth;

        DrawIconButton(g, 16, 14, "\uE72B", () => SetPage(Page.Main));
        DrawString(g, "Settings", _titleFont!, p.Primary,
            R(60, 14, w - 120, 34), StringAlignment.Center);

        DrawString(g, "APPEARANCE", _smallFont!, p.Tertiary,
            R(20, 62, w - 40, 16), StringAlignment.Near);
        DrawThemeSegments(g, R(16, 82, w - 32, 34));

        var y = (float)SettingsRowStart;
        DrawRow(g, y, "\uE72C", "Reconnect now", null, null, false, false, _reconnect);
        y += SettingsRowStep;
        DrawRow(g, y, "\uE7E8", "Start with Windows", null, StartupManager.IsEnabled(), false, false, ToggleStartup);
        y += SettingsRowStep;
        DrawRow(g, y, "\uE8A5", "Show percentage in icon", null, _config.ShowPercentage, false, false, TogglePercentage);
        y += SettingsRowStep;
        DrawRow(g, y, "\uE702", "Open Bluetooth settings", null, null, false, true,
            () => OpenUrl("ms-settings:bluetooth"));
        y += SettingsRowStep;
        DrawRow(g, y, "\uE8A5", "Open log file", null, null, false, true,
            () => OpenUrl(Log.FilePath));
        y += SettingsRowStep;

        if (data.SamsungRunning)
        {
            DrawRow(g, y, "\uE7BA", "Close Samsung Galaxy Buds app", null, null, true, false,
                _closeSamsung);
            y += SettingsRowStep;
        }

        DrawRow(g, y, "\uE74D", "Forget remembered device", null, null, true, false,
            ForgetDevice);
        y += SettingsRowStep;

        DrawSeparator(g, y + 6);
        DrawString(g, "Earbud levels refresh every 15 s", _smallFont!, p.Tertiary,
            R(24, y + 16, w - 48, 16), StringAlignment.Center);
    }

    /// <summary>Hand-drawn fallback used when the connected model has no render.</summary>
    private void DrawEarbudsArt(Graphics g, RectangleF area)
    {
        var cx = area.X + area.Width / 2f;
        var cy = area.Y + area.Height * 0.52f;
        var budWidth = area.Width * 0.27f;
        var budHeight = area.Height * 0.42f;

        DrawBud(g, cx - area.Width * 0.16f, cy, budWidth, budHeight, -13f);
        DrawBud(g, cx + area.Width * 0.16f, cy, budWidth, budHeight, 13f);
    }

    private void DrawBud(Graphics g, float cx, float cy, float width, float height, float angle)
    {
        var light = ThemeUtil.IsLight(_config.Theme);
        var state = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(angle);

        var rect = new RectangleF(-width / 2f, -height / 2f, width, height);

        using (var path = RoundedRect(rect, width * 0.42f))
        {
            using var body = new LinearGradientBrush(
                rect,
                light ? Color.FromArgb(255, 138, 144, 150) : Color.FromArgb(255, 78, 84, 90),
                light ? Color.FromArgb(255, 42, 46, 50) : Color.FromArgb(255, 16, 19, 22),
                130f);
            g.FillPath(body, path);

            using var edge = new Pen(Color.FromArgb(56, 255, 255, 255), Math.Max(1f, S(1f)));
            g.DrawPath(edge, path);
        }

        var driver = new RectangleF(-width * 0.26f, -height * 0.14f, width * 0.52f, width * 0.52f);
        using (var driverBrush = new SolidBrush(
                   light ? Color.FromArgb(255, 26, 30, 34) : Color.FromArgb(255, 8, 10, 12)))
            g.FillEllipse(driverBrush, driver);
        using (var driverEdge = new Pen(Color.FromArgb(72, 255, 255, 255), Math.Max(1f, S(1f))))
            g.DrawEllipse(driverEdge, driver);

        using (var highlight = new SolidBrush(Color.FromArgb(34, 255, 255, 255)))
            g.FillEllipse(highlight, -width * 0.36f, -height * 0.42f, width * 0.34f, height * 0.26f);

        using (var tip = new GraphicsPath())
        {
            tip.AddEllipse(width * 0.16f, height * 0.08f, width * 0.36f, height * 0.3f);
            using var tipBrush = new SolidBrush(
                light ? Color.FromArgb(255, 66, 72, 78) : Color.FromArgb(255, 28, 32, 36));
            g.FillPath(tipBrush, tip);
        }

        g.Restore(state);
    }

    private void DrawGauge(
        Graphics g,
        float cx,
        float cy,
        float diameter,
        int? value,
        bool charging,
        bool buds)
    {
        var p = _palette;
        var stroke = S(6f);
        var ring = new RectangleF(
            S(cx) - S(diameter) / 2f + stroke / 2f,
            S(cy) - S(diameter) / 2f + stroke / 2f,
            S(diameter) - stroke,
            S(diameter) - stroke);

        using (var track = new Pen(p.Track, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawEllipse(track, ring);

        if (value is >= 0)
        {
            var sweep = 360f * Math.Clamp(value.Value, 0, 100) / 100f;
            // Monochrome by default, red when low and green while charging, so
            // the card stays flat and only colour carries meaning.
            var color = charging
                ? p.Charging
                : value.Value <= BatteryStatus.LowThreshold ? p.Danger : p.Primary;
            using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };

            if (sweep >= 359.5f)
                g.DrawEllipse(pen, ring);
            else if (sweep > 0)
                g.DrawArc(pen, ring, -90f, sweep);
        }

        var icon = new RectangleF(S(cx) - S(13f), S(cy) - S(13f), S(26f), S(26f));
        if (buds) DrawBudsGlyph(g, icon, p.Secondary);
        else DrawCaseGlyph(g, icon, p.Secondary);
    }

    private void DrawBudsGlyph(Graphics g, RectangleF rect, Color color)
    {
        var width = rect.Width;
        var height = rect.Height;
        var budWidth = width * 0.36f;
        var budHeight = height * 0.52f;
        var top = rect.Y + (height - budHeight) / 2f;

        using var pen = new Pen(color, Math.Max(1f, S(1.4f)));
        using var brush = new SolidBrush(color);

        var left = new RectangleF(rect.X + width * 0.05f, top, budWidth, budHeight);
        var right = new RectangleF(rect.X + width * 0.59f, top, budWidth, budHeight);

        using (var path = RoundedRect(left, budWidth * 0.42f))
            g.DrawPath(pen, path);
        using (var path = RoundedRect(right, budWidth * 0.42f))
            g.DrawPath(pen, path);

        g.FillEllipse(brush, left.X + budWidth * 0.27f, left.Y + budHeight * 0.3f,
            budWidth * 0.46f, budWidth * 0.46f);
        g.FillEllipse(brush, right.X + budWidth * 0.27f, right.Y + budHeight * 0.3f,
            budWidth * 0.46f, budWidth * 0.46f);
    }

    private void DrawCaseGlyph(Graphics g, RectangleF rect, Color color)
    {
        var width = rect.Width;
        var height = rect.Height;
        var body = new RectangleF(
            rect.X + width * 0.14f, rect.Y + height * 0.26f,
            width * 0.72f, height * 0.48f);

        using var pen = new Pen(color, Math.Max(1f, S(1.4f)));
        using (var path = RoundedRect(body, body.Height * 0.28f))
            g.DrawPath(pen, path);

        g.DrawLine(pen, body.X, body.Y + body.Height * 0.4f, body.Right, body.Y + body.Height * 0.4f);

        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, body.X + body.Width * 0.44f, body.Y + body.Height * 0.58f,
            body.Width * 0.12f, body.Width * 0.12f);
    }

    private void DrawGaugeLabel(Graphics g, float cx, float y, string label, int? value)
    {
        var p = _palette;
        var valueText = value is { } v ? $"{v}%" : "--";

        var labelSize = g.MeasureString(label, _labelFont!);
        var valueSize = g.MeasureString(valueText, _valueFont!);
        var gap = S(5f);
        var total = labelSize.Width + gap + valueSize.Width;
        var x = S(cx) - total / 2f;
        var top = S(y);

        using (var brush = new SolidBrush(p.Secondary))
            g.DrawString(label, _labelFont!, brush,
                new PointF(x, top + valueSize.Height - labelSize.Height),
                StringFormat.GenericTypographic);

        using (var brush = new SolidBrush(p.Primary))
            g.DrawString(valueText, _valueFont!, brush,
                new PointF(x + labelSize.Width + gap, top),
                StringFormat.GenericTypographic);
    }

    private void DrawRow(
        Graphics g,
        float y,
        string glyph,
        string text,
        string? value,
        bool? toggle,
        bool danger,
        bool chevron,
        Action action)
    {
        var p = _palette;
        var rect = R(12, y, BaseWidth - 24, 40);

        if (_hover is not null && _hover.Bounds == rect)
        {
            using var hover = new SolidBrush(p.Hover);
            using var hoverPath = RoundedRect(rect, S(8f));
            g.FillPath(hover, hoverPath);
        }

        DrawGlyph(g, glyph, R(28, y + 12, 16, 16),
            danger ? p.Danger : p.Secondary, _iconFontSmall!);

        DrawString(g, text, _bodyFont!, danger ? p.Danger : p.Primary,
            R(56, y, 208, 40), StringAlignment.Near);

        if (toggle is { } on)
        {
            DrawToggle(g, R(BaseWidth - 16 - 38, y + 10, 38, 20), on);
        }
        else if (value is not null)
        {
            DrawString(g, value, _smallFont!, p.Secondary,
                R(BaseWidth - 16 - 148, y, 116, 40), StringAlignment.Far);
            DrawGlyph(g, "\uE76C", R(BaseWidth - 16 - 24, y + 12, 16, 16), p.Tertiary, _iconFontSmall!);
        }
        else if (chevron)
        {
            DrawGlyph(g, "\uE76C", R(BaseWidth - 16 - 24, y + 12, 16, 16), p.Tertiary, _iconFontSmall!);
        }

        AddHit(rect, action);
    }

    private void DrawToggle(Graphics g, RectangleF rect, bool on)
    {
        var p = _palette;

        using (var path = RoundedRect(rect, rect.Height / 2f))
        using (var brush = new SolidBrush(on ? p.Accent : p.ToggleOff))
            g.FillPath(brush, path);

        var knob = rect.Height - S(4f);
        var knobX = on ? rect.Right - knob - S(2f) : rect.X + S(2f);

        using var knobBrush = new SolidBrush(Color.White);
        g.FillEllipse(knobBrush, knobX, rect.Y + S(2f), knob, knob);
    }

    private void DrawThemeSegments(Graphics g, RectangleF rect)
    {
        var p = _palette;

        using (var background = RoundedRect(rect, S(9f)))
        using (var brush = new SolidBrush(p.Hover))
            g.FillPath(brush, background);

        var options = new[] { AppTheme.System, AppTheme.Light, AppTheme.Dark };
        var segmentWidth = rect.Width / options.Length;

        for (var i = 0; i < options.Length; i++)
        {
            var option = options[i];
            var segment = new RectangleF(
                rect.X + i * segmentWidth + S(2f), rect.Y + S(2f),
                segmentWidth - S(4f), rect.Height - S(4f));

            if (option == _config.Theme)
            {
                using var selected = RoundedRect(segment, S(7f));
                using var brush = new SolidBrush(p.Accent);
                g.FillPath(brush, selected);
            }

            var color = option == _config.Theme ? p.AccentText : p.Secondary;
            DrawString(g, ThemeUtil.DisplayName(option), _smallFont!, color,
                segment, StringAlignment.Center);

            AddHit(segment, () => SelectTheme(option));
        }
    }

    private void DrawIconButton(Graphics g, float x, float y, string glyph, Action action)
    {
        var rect = R(x, y, 34, 34);

        if (_hover is not null && _hover.Bounds == rect)
        {
            using var brush = new SolidBrush(_palette.Hover);
            using var path = RoundedRect(rect, S(8f));
            g.FillPath(brush, path);
        }

        DrawGlyph(g, glyph, rect, _palette.Secondary, _iconFont!);
        AddHit(rect, action);
    }

    private void DrawGlyph(Graphics g, string glyph, RectangleF rect, Color color, Font font)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        g.DrawString(glyph, font, brush, rect, format);
    }

    private void DrawString(
        Graphics g,
        string text,
        Font font,
        Color color,
        RectangleF rect,
        StringAlignment alignment,
        StringAlignment line = StringAlignment.Center,
        StringTrimming trimming = StringTrimming.EllipsisCharacter)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = alignment,
            LineAlignment = line,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = trimming
        };
        g.DrawString(text, font, brush, rect, format);
    }

    private void DrawSeparator(Graphics g, float y)
    {
        using var pen = new Pen(_palette.Separator, Math.Max(1f, S(1f)));
        g.DrawLine(pen, S(16f), S(y), S(BaseWidth - 16f), S(y));
    }

    private (string Text, Color Dot) DescribeStatus(PopupSnapshot data)
    {
        var p = _palette;

        return data.State switch
        {
            ConnectionState.Connected when data.Status is { } status =>
                ($"Connected · updated {status.UpdatedAt:HH:mm:ss}", p.Accent),
            ConnectionState.Connected =>
                ("Connected · waiting for battery", p.Accent),
            ConnectionState.Connecting =>
                ("Connecting…", Color.FromArgb(255, 255, 159, 10)),
            _ => (string.IsNullOrWhiteSpace(data.Error)
                    ? "Disconnected"
                    : $"Disconnected · {data.Error}",
                p.Danger)
        };
    }

    private void AddHit(RectangleF bounds, Action action) => _hits.Add(new Hit(bounds, action));

    private void SetPage(Page page)
    {
        if (_page == page) return;

        _page = page;
        ApplySize();
        Invalidate();
    }

    private void ToggleTheme()
    {
        _config.Theme = ThemeUtil.IsLight(_config.Theme) ? AppTheme.Dark : AppTheme.Light;
        _config.Save();
        _palette = Palette.For(ThemeUtil.IsLight(_config.Theme));
        Invalidate();
    }

    private void SelectTheme(AppTheme theme)
    {
        _config.Theme = theme;
        _config.Save();
        _palette = Palette.For(ThemeUtil.IsLight(_config.Theme));
        Invalidate();
    }

    private void ToggleStartup()
    {
        try
        {
            StartupManager.SetEnabled(!StartupManager.IsEnabled());
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update startup entry", ex);
        }

        Invalidate();
    }

    private void TogglePercentage()
    {
        _config.ShowPercentage = !_config.ShowPercentage;
        _config.Save();
        _iconOptionsChanged();
        Invalidate();
    }

    private void ForgetDevice()
    {
        _config.DeviceAddress = null;
        _config.DeviceName = null;
        _config.Save();
        _reconnect();
        Invalidate();
    }

    private static void OpenUrl(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open {target}", ex);
        }
    }

    private static string Format(int? value) => value is { } v ? $"{v}%" : "--";

    private void EnsureFonts()
    {
        if (_fontDpi == DeviceDpi && _nameFont is not null) return;

        DisposeFonts();
        _fontDpi = DeviceDpi;
        var s = DpiScale;

        _nameFont = new Font("Segoe UI", 25f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        _titleFont = new Font("Segoe UI", 17f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        _statusFont = new Font("Segoe UI", 12f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        _bodyFont = new Font("Segoe UI", 13f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        _smallFont = new Font("Segoe UI", 11f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        _valueFont = new Font("Segoe UI", 14f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        _labelFont = new Font("Segoe UI", 11f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        _iconFont = new Font("Segoe MDL2 Assets", 16f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        _iconFontSmall = new Font("Segoe MDL2 Assets", 13f * s, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private void DisposeFonts()
    {
        _nameFont?.Dispose();
        _titleFont?.Dispose();
        _statusFont?.Dispose();
        _bodyFont?.Dispose();
        _smallFont?.Dispose();
        _valueFont?.Dispose();
        _labelFont?.Dispose();
        _iconFont?.Dispose();
        _iconFontSmall?.Dispose();

        _nameFont = _titleFont = _statusFont = _bodyFont = _smallFont = null;
        _valueFont = _labelFont = _iconFont = _iconFontSmall = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeFonts();
        base.Dispose(disposing);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private sealed record Palette(
        Color Background,
        Color Border,
        Color Primary,
        Color Secondary,
        Color Tertiary,
        Color Hover,
        Color Separator,
        Color Track,
        Color Accent,
        Color AccentText,
        Color ToggleOff,
        Color Danger,
        Color Charging)
    {
        public static Palette For(bool light) => light ? LightPalette : DarkPalette;

        private static readonly Palette DarkPalette = new(
            Background: Color.FromArgb(255, 0, 0, 0),
            Border: Color.FromArgb(255, 38, 38, 38),
            Primary: Color.FromArgb(255, 255, 255, 255),
            Secondary: Color.FromArgb(255, 168, 168, 168),
            Tertiary: Color.FromArgb(255, 111, 111, 111),
            Hover: Color.FromArgb(20, 255, 255, 255),
            Separator: Color.FromArgb(18, 255, 255, 255),
            Track: Color.FromArgb(255, 42, 42, 42),
            Accent: Color.FromArgb(255, 0, 230, 118),
            AccentText: Color.FromArgb(255, 6, 22, 14),
            ToggleOff: Color.FromArgb(255, 58, 66, 73),
            Danger: Color.FromArgb(255, 255, 92, 92),
            Charging: Color.FromArgb(255, 0, 155, 99));

        private static readonly Palette LightPalette = new(
            Background: Color.FromArgb(255, 255, 255, 255),
            Border: Color.FromArgb(255, 226, 226, 226),
            Primary: Color.FromArgb(255, 17, 17, 17),
            Secondary: Color.FromArgb(255, 92, 92, 92),
            Tertiary: Color.FromArgb(255, 138, 138, 138),
            Hover: Color.FromArgb(14, 0, 0, 0),
            Separator: Color.FromArgb(14, 0, 0, 0),
            Track: Color.FromArgb(255, 230, 230, 230),
            Accent: Color.FromArgb(255, 0, 168, 107),
            AccentText: Color.FromArgb(255, 255, 255, 255),
            ToggleOff: Color.FromArgb(255, 195, 203, 209),
            Danger: Color.FromArgb(255, 211, 47, 47),
            Charging: Color.FromArgb(255, 15, 123, 15));
    }
}

/// <summary>Everything the popup needs to draw one frame.</summary>
public sealed record PopupSnapshot(
    ConnectionState State,
    string DeviceName,
    BatteryStatus? Status,
    string? Error,
    bool SamsungRunning);
