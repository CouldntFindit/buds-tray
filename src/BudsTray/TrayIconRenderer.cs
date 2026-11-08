using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BudsTray.Buds;

namespace BudsTray;

public static class TrayIconRenderer
{
    /// <summary>Windows' low-battery red, used by the shell's own battery glyph.</summary>
    private static readonly Color LowBattery = Color.FromArgb(255, 232, 17, 35);

    /// <summary>
    /// Green used for the charging interior. Both shades are dark enough for the
    /// white lightning bolt to stay readable; the light-taskbar shade is Windows'
    /// darker green so it also stands out on a pale taskbar.
    /// </summary>
    private static readonly Color ChargingGreenDark = Color.FromArgb(255, 0, 155, 99);
    private static readonly Color ChargingGreenLight = Color.FromArgb(255, 15, 123, 15);

    private static Color ChargingGreen(bool lightTheme)
        => lightTheme ? ChargingGreenLight : ChargingGreenDark;

    /// <summary>Earbuds whose levels differ by more than this get two separate rows.</summary>
    private const int DualBarThreshold = 5;

    public static Icon Render(
        int? left,
        int? right,
        bool leftCharging,
        bool rightCharging,
        bool connected,
        bool showPercentage,
        bool lightTheme,
        int size = 0,
        bool windows11 = true,
        bool leftPresent = true,
        bool rightPresent = true)
    {
        if (size <= 0) size = Math.Clamp(SystemInformation.SmallIconSize.Width, 16, 64);

        using var bitmap = RenderBitmap(
            size, left, right, leftCharging, rightCharging, connected, showPercentage, lightTheme,
            windows11, leftPresent, rightPresent);
        return CreateIcon(bitmap);
    }

    private static Bitmap RenderBitmap(
        int size,
        int? left,
        int? right,
        bool leftCharging,
        bool rightCharging,
        bool connected,
        bool showPercentage,
        bool lightTheme,
        bool windows11,
        bool leftPresent,
        bool rightPresent)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);

        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;

        if (showPercentage)
            DrawText(g, size, left, right, leftCharging, rightCharging, connected, lightTheme,
                leftPresent, rightPresent);
        else if (windows11)
            DrawBattery11(g, size, left, right, leftCharging, rightCharging, connected, lightTheme,
                leftPresent, rightPresent);
        else
            DrawBattery10(g, size, left, right, leftCharging, rightCharging, connected, lightTheme,
                leftPresent, rightPresent);

        return bitmap;
    }

    /// <summary>
    /// Windows 11 glyph: rounded horizontal body with an attached rounded nub,
    /// thin monochrome outline and a fill bar with a breathing gap.
    /// </summary>
    private static void DrawBattery11(
        Graphics g,
        int size,
        int? left,
        int? right,
        bool leftCharging,
        bool rightCharging,
        bool connected,
        bool lightTheme,
        bool leftPresent,
        bool rightPresent)
    {
        var scale = size / 16f;

        var body = new RectangleF(0.3f * scale, 2.9f * scale, 13.5f * scale, 10.2f * scale);
        var stroke = Math.Max(1f, 1.35f * scale);
        var radius = 2.2f * scale;

        var nubHeight = Math.Max(2.5f, 4.6f * scale);
        var nub = new RectangleF(
            body.Right,
            body.Y + (body.Height - nubHeight) / 2f,
            Math.Max(1.5f, 1.9f * scale),
            nubHeight);

        DrawBattery(g, body, nub, radius, stroke, size, left, right,
            leftCharging, rightCharging, connected, lightTheme, fillGap: 0.8f,
            leftPresent, rightPresent);
    }

    /// <summary>
    /// Windows 10 glyph: the flatter, squarer shell icon. Almost square corners,
    /// a 1 px outline and a fill that sits closer to the frame.
    /// </summary>
    private static void DrawBattery10(
        Graphics g,
        int size,
        int? left,
        int? right,
        bool leftCharging,
        bool rightCharging,
        bool connected,
        bool lightTheme,
        bool leftPresent,
        bool rightPresent)
    {
        var scale = size / 16f;

        var body = new RectangleF(0.6f * scale, 3.8f * scale, 12.6f * scale, 8.4f * scale);
        var stroke = Math.Max(1f, 1.0f * scale);
        var radius = 0.8f * scale;

        var nubHeight = Math.Max(2.5f, 4.8f * scale);
        var nub = new RectangleF(
            body.Right,
            body.Y + (body.Height - nubHeight) / 2f,
            Math.Max(1.5f, 2.0f * scale),
            nubHeight);

        DrawBattery(g, body, nub, radius, stroke, size, left, right,
            leftCharging, rightCharging, connected, lightTheme, fillGap: 0.5f,
            leftPresent, rightPresent);
    }

    private static void DrawBattery(
        Graphics g,
        RectangleF body,
        RectangleF nub,
        float radius,
        float stroke,
        int size,
        int? left,
        int? right,
        bool leftCharging,
        bool rightCharging,
        bool connected,
        bool lightTheme,
        float fillGap,
        bool leftPresent,
        bool rightPresent)
    {
        var scale = size / 16f;

        var hasLeft = left is >= 0 and <= 100;
        var hasRight = right is >= 0 and <= 100;

        // Single-bud mode: exactly one bud is out of the case. The glyph then
        // describes that bud only, even before its level has arrived, so it can
        // never show the other bud's (stale) reading.
        var single = leftPresent != rightPresent;

        int? level;
        if (single)
        {
            var side = leftPresent ? left : right;
            level = side is >= 0 and <= 100 ? side : null;
        }
        else
        {
            var values = new List<int>(2);
            if (leftPresent && hasLeft) values.Add(left!.Value);
            if (rightPresent && hasRight) values.Add(right!.Value);
            level = values.Count > 0 ? values.Min() : null;
        }

        var lowest = level ?? 0;

        var charging = connected && level is not null && (single
            ? leftPresent ? leftCharging : rightCharging
            : leftCharging || rightCharging);

        var low = connected && !charging && lowest > 0 && lowest <= BatteryStatus.LowThreshold;

        // The shell stays at full strength; the slash alone carries the
        // "disconnected" meaning, which reads better than a dimmed battery at
        // 16 px (a dim shell looked like an empty 0 % battery).
        var ink = lightTheme
            ? Color.FromArgb(255, 26, 26, 26)
            : Color.FromArgb(255, 255, 255, 255);

        var fillColor = charging
            ? ChargingGreen(lightTheme)
            : low ? LowBattery : ink;

        using var pen = new Pen(ink, stroke) { LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(ink);

        using (var bodyPath = RoundedRect(body, radius))
            g.DrawPath(pen, bodyPath);

        using (var nubPath = RoundedRect(nub, Math.Max(0.5f, radius * 0.45f)))
            g.FillPath(brush, nubPath);

        if (!connected)
        {
            // A diagonal slash through the empty shell, so "disconnected" cannot
            // be mistaken for a real 0 % reading. The slash is knocked out of the
            // outline first to stay legible at 16 px.
            DrawDisconnectedSlash(g, body, stroke, size, lightTheme);
            return;
        }

        // Interior fill area: inset past the outline plus a thin breathing gap.
        var inset = stroke * 0.5f + fillGap * scale;
        var fill = new RectangleF(
            body.X + inset, body.Y + inset, body.Width - inset * 2, body.Height - inset * 2);
        var fillRadius = Math.Max(0.5f, radius - inset);

        if (charging)
        {
            // Charging keeps the real level visible: the green fill bar is drawn
            // at the current percentage and the bolt sits on top of it. The bolt
            // uses the shell ink so it stays legible over both the green part and
            // the empty part of a partially filled battery.
            DrawFillBar(g, fill, lowest, fillColor, fillRadius);

            using var boltBrush = new SolidBrush(ink);
            DrawBolt(g, RectangleF.Inflate(fill, -0.35f * scale, -0.35f * scale), boltBrush);
            return;
        }

        var dual = !single && leftPresent && rightPresent && hasLeft && hasRight &&
                   Math.Abs(left!.Value - right!.Value) > DualBarThreshold;

        if (!dual)
        {
            DrawFillBar(g, fill, lowest, fillColor, Math.Min(1.2f * scale, fill.Height / 2f));
            return;
        }

        // Two rows: left bud on top, right bud below, so an imbalance is visible
        // without abandoning the native battery silhouette.
        var gap = Math.Max(0.6f, 0.8f * scale);
        var rowHeight = (fill.Height - gap) / 2f;
        var rowRadius = Math.Min(0.9f * scale, rowHeight / 2f);

        DrawFillBar(g, new RectangleF(fill.X, fill.Y, fill.Width, rowHeight), left!.Value, fillColor, rowRadius);
        DrawFillBar(
            g,
            new RectangleF(fill.X, fill.Y + rowHeight + gap, fill.Width, rowHeight),
            right!.Value,
            fillColor,
            rowRadius);
    }

    /// <summary>
    /// Draws the "not connected" mark: a diagonal strike through the empty
    /// shell interior, from the lower-left to the upper-right. It stays inside
    /// the outline so the battery silhouette remains intact and legible at
    /// 16 px, unlike a slash that cuts the frame into stray corner arcs.
    /// </summary>
    private static void DrawDisconnectedSlash(
        Graphics g, RectangleF body, float stroke, int size, bool lightTheme)
    {
        var scale = size / 16f;

        var inner = RectangleF.Inflate(
            body,
            -(stroke * 0.5f + 0.6f * scale),
            -(stroke * 0.5f + 0.6f * scale));

        var start = new PointF(inner.Left, inner.Bottom);
        var end = new PointF(inner.Right, inner.Top);

        var slashWidth = Math.Max(1.05f, 1.2f * scale);
        var slashColor = lightTheme
            ? Color.FromArgb(255, 26, 26, 26)
            : Color.FromArgb(255, 255, 255, 255);

        using var pen = new Pen(slashColor, slashWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawLine(pen, start, end);
    }

    private static void DrawFillBar(
        Graphics g,
        RectangleF area,
        int value,
        Color color,
        float radius)
    {
        if (value <= 0) return;

        var width = Math.Max(radius * 2f, area.Width * Math.Clamp(value, 0, 100) / 100f);
        using var path = RoundedRect(new RectangleF(area.X, area.Y, width, area.Height), radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>
    /// The charging bolt used by the shell's battery glyph, centered inside the
    /// body with a small margin so it never merges with the green outline.
    /// </summary>
    private static void DrawBolt(Graphics g, RectangleF body, Brush brush)
    {
        var x = body.X;
        var y = body.Y;
        var w = body.Width;
        var h = body.Height;

        var points = new[]
        {
            new PointF(x + w * 0.66f, y + h * 0.12f),
            new PointF(x + w * 0.30f, y + h * 0.56f),
            new PointF(x + w * 0.45f, y + h * 0.56f),
            new PointF(x + w * 0.34f, y + h * 0.88f),
            new PointF(x + w * 0.70f, y + h * 0.44f),
            new PointF(x + w * 0.55f, y + h * 0.44f)
        };

        using var path = new GraphicsPath();
        path.AddPolygon(points);
        g.FillPath(brush, path);
    }

    private static void DrawText(
        Graphics g,
        int size,
        int? left,
        int? right,
        bool leftCharging,
        bool rightCharging,
        bool connected,
        bool lightTheme,
        bool leftPresent,
        bool rightPresent)
    {
        // Single-bud mode always describes the bud that is in use, so the text
        // glyph cannot fall back to the other bud's percentage.
        var single = leftPresent != rightPresent;
        var values = new List<int>(2);

        if (single)
        {
            var side = leftPresent ? left : right;
            if (side is >= 0 and <= 100) values.Add(side.Value);
        }
        else
        {
            if (leftPresent && left is >= 0 and <= 100) values.Add(left.Value);
            if (rightPresent && right is >= 0 and <= 100) values.Add(right.Value);

            // Both buds in the case: fall back to their levels rather than a dash.
            if (values.Count == 0)
            {
                if (left is >= 0 and <= 100) values.Add(left.Value);
                if (right is >= 0 and <= 100) values.Add(right.Value);
            }
        }

        var charging = values.Count > 0 && (single
            ? leftPresent ? leftCharging : rightCharging
            : leftCharging || rightCharging);

        var text = values.Count > 0
            ? values.Min().ToString(CultureInfo.InvariantCulture)
            : "–";

        var low = connected && !charging && values.Count > 0 && values.Min() <= BatteryStatus.LowThreshold;

        var fontSize = size * (text.Length >= 3 ? 0.46f : 0.62f);

        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(!connected
            ? lightTheme ? Color.FromArgb(160, 26, 26, 26) : Color.FromArgb(160, 255, 255, 255)
            : charging
                ? ChargingGreen(lightTheme)
                : low
                    ? LowBattery
                    : lightTheme ? Color.FromArgb(255, 26, 26, 26) : Color.FromArgb(255, 255, 255, 255));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.None
        };

        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // A rect wider than the canvas keeps 3-digit values centered without wrapping.
        g.DrawString(text, font, brush, new RectangleF(-size, 0, size * 3, size), format);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Icon CreateIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
