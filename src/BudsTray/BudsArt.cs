using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace BudsTray;

/// <summary>
/// Monochrome product renders for each Galaxy Buds generation. The PNGs are
/// embedded in the executable and were prepared from Samsung's official product
/// shots: white background removed, desaturated, cropped to the earbuds.
/// </summary>
public static class BudsArt
{
    private sealed record Model(string Key, string Name);

    // The extended status frame's earType byte follows the model order used by
    // GalaxyBudsClient (Galaxy Buds = 0, … Galaxy Buds FE = 6, Buds3 = 8).
    private static readonly Dictionary<int, Model> ByEarType = new()
    {
        [0] = new("galaxy-buds", "Galaxy Buds"),
        [1] = new("galaxy-buds-plus", "Galaxy Buds+"),
        [2] = new("galaxy-buds-live", "Galaxy Buds Live"),
        [3] = new("galaxy-buds-pro", "Galaxy Buds Pro"),
        [4] = new("galaxy-buds2", "Galaxy Buds2"),
        [5] = new("galaxy-buds2-pro", "Galaxy Buds2 Pro"),
        [6] = new("galaxy-buds-fe", "Galaxy Buds FE"),
        [7] = new("galaxy-buds-core", "Galaxy Buds Core"),
        [8] = new("galaxy-buds3", "Galaxy Buds3"),
        [9] = new("galaxy-buds3-pro", "Galaxy Buds3 Pro"),
        [10] = new("galaxy-buds3-fe", "Galaxy Buds3 FE"),
        [11] = new("galaxy-buds4", "Galaxy Buds4"),
        [12] = new("galaxy-buds4-pro", "Galaxy Buds4 Pro")
    };

    private static readonly Dictionary<string, Bitmap> Cache = new();
    private static readonly object Gate = new();

    /// <summary>Model label for the card, or null when the model is unknown.</summary>
    public static string? ModelName(int? earType, string? deviceName)
        => Resolve(earType, deviceName)?.Name;

    /// <summary>
    /// Draws the model's product render inside <paramref name="area"/>, keeping
    /// the aspect ratio. Returns false when no render is available.
    /// </summary>
    public static bool Draw(
        Graphics graphics,
        RectangleF area,
        int? earType,
        string? deviceName,
        bool lightTheme)
    {
        var model = Resolve(earType, deviceName);
        if (model is null) return false;

        var bitmap = Load(model.Key, lightTheme);
        if (bitmap is null) return false;

        var scale = Math.Min(area.Width / bitmap.Width, area.Height / bitmap.Height);
        var width = bitmap.Width * scale;
        var height = bitmap.Height * scale;

        graphics.DrawImage(
            bitmap,
            area.X + (area.Width - width) / 2f,
            area.Y + (area.Height - height) / 2f,
            width,
            height);

        return true;
    }

    private static Model? Resolve(int? earType, string? deviceName)
    {
        if (earType is { } type && ByEarType.TryGetValue(type, out var model))
            return model;

        if (string.IsNullOrWhiteSpace(deviceName)) return null;

        var name = deviceName.ToLowerInvariant();
        return ByEarType.Values
            .OrderByDescending(m => m.Name.Length)
            .FirstOrDefault(m => name.Contains(m.Name.ToLowerInvariant()));
    }

    private static Bitmap? Load(string key, bool lightTheme)
    {
        var cacheKey = $"{key}:{(lightTheme ? "light" : "dark")}";

        lock (Gate)
        {
            if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(
            $"BudsTray.Assets.Buds.{key}.png");
        if (stream is null) return null;

        using var source = new Bitmap(stream);
        var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);

            // Graphite renders disappear on a pitch-black card, so lift the
            // midtones for the dark theme. Light theme keeps the original tones.
            var brightness = lightTheme ? 1f : 1.8f;
            var matrix = new ColorMatrix(new[]
            {
                new[] { brightness, 0f, 0f, 0f, 0f },
                new[] { 0f, brightness, 0f, 0f, 0f },
                new[] { 0f, 0f, brightness, 0f, 0f },
                new[] { 0f, 0f, 0f, 1f, 0f },
                new[] { 0f, 0f, 0f, 0f, 1f }
            });

            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(matrix);

            graphics.DrawImage(
                source,
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                0, 0, source.Width, source.Height,
                GraphicsUnit.Pixel,
                attributes);
        }

        lock (Gate)
        {
            Cache[cacheKey] = bitmap;
        }

        return bitmap;
    }
}
