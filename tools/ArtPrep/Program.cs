using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// Turns the white-background Samsung product renders in assets/buds/raw into
// transparent, grayscale, tightly cropped PNGs in assets/buds.
//
//   dotnet run --project tools/ArtPrep -- <inputDir> <outputDir>

var inputDir = args.Length > 0 ? args[0] : "assets/buds/raw";
var outputDir = args.Length > 1 ? args[1] : "assets/buds";
Directory.CreateDirectory(outputDir);

const int MaxWidth = 640;
const int Padding = 8;
const int BackgroundThreshold = 238;

foreach (var file in Directory.EnumerateFiles(inputDir, "*.png").OrderBy(f => f))
{
    var name = Path.GetFileName(file);
    using var source = new Bitmap(file);
    using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);

    using (var graphics = Graphics.FromImage(bitmap))
        graphics.DrawImage(source, 0, 0, source.Width, source.Height);

    var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
    var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    var buffer = new byte[data.Stride * bitmap.Height];
    Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

    RemoveWhiteBackground(buffer, data.Stride, bitmap.Width, bitmap.Height);
    Desaturate(buffer, data.Stride, bitmap.Width, bitmap.Height);

    Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
    bitmap.UnlockBits(data);

    var bounds = ContentBounds(buffer, data.Stride, bitmap.Width, bitmap.Height);
    if (bounds.Width <= 0 || bounds.Height <= 0)
    {
        Console.WriteLine($"skip {name}: no content");
        continue;
    }

    bounds = Rectangle.FromLTRB(
        Math.Max(0, bounds.Left - Padding),
        Math.Max(0, bounds.Top - Padding),
        Math.Min(bitmap.Width, bounds.Right + Padding),
        Math.Min(bitmap.Height, bounds.Bottom + Padding));

    using var cropped = bitmap.Clone(bounds, PixelFormat.Format32bppArgb);

    var targetWidth = Math.Min(MaxWidth, cropped.Width);
    var targetHeight = (int)Math.Round(cropped.Height * (targetWidth / (double)cropped.Width));

    using var resized = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(resized))
    {
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.DrawImage(cropped, 0, 0, targetWidth, targetHeight);
    }

    var output = Path.Combine(outputDir, name);
    resized.Save(output, ImageFormat.Png);
    Console.WriteLine($"wrote {output} ({targetWidth}x{targetHeight})");
}

static void RemoveWhiteBackground(byte[] buffer, int stride, int width, int height)
{
    var visited = new bool[width * height];
    var queue = new Queue<int>();

    void Seed(int x, int y)
    {
        var index = y * width + x;
        if (visited[index]) return;
        visited[index] = true;
        if (!IsWhite(buffer, stride, x, y)) return;
        queue.Enqueue(index);
    }

    for (var x = 0; x < width; x++)
    {
        Seed(x, 0);
        Seed(x, height - 1);
    }

    for (var y = 0; y < height; y++)
    {
        Seed(0, y);
        Seed(width - 1, y);
    }

    while (queue.Count > 0)
    {
        var index = queue.Dequeue();
        var x = index % width;
        var y = index / width;
        SetTransparent(buffer, stride, x, y);

        void Visit(int nx, int ny)
        {
            if (nx < 0 || ny < 0 || nx >= width || ny >= height) return;
            var next = ny * width + nx;
            if (visited[next]) return;
            visited[next] = true;
            if (!IsWhite(buffer, stride, nx, ny)) return;
            queue.Enqueue(next);
        }

        Visit(x - 1, y);
        Visit(x + 1, y);
        Visit(x, y - 1);
        Visit(x, y + 1);
    }
}

static bool IsWhite(byte[] buffer, int stride, int x, int y)
{
    var offset = y * stride + x * 4;
    return buffer[offset] >= 238 && buffer[offset + 1] >= 238 && buffer[offset + 2] >= 238;
}

static void SetTransparent(byte[] buffer, int stride, int x, int y)
    => buffer[y * stride + x * 4 + 3] = 0;

static void Desaturate(byte[] buffer, int stride, int width, int height)
{
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var offset = y * stride + x * 4;
            if (buffer[offset + 3] == 0) continue;

            var gray = (byte)((buffer[offset + 2] * 299 + buffer[offset + 1] * 587 + buffer[offset] * 114) / 1000);
            buffer[offset] = gray;
            buffer[offset + 1] = gray;
            buffer[offset + 2] = gray;
        }
    }
}

static Rectangle ContentBounds(byte[] buffer, int stride, int width, int height)
{
    var left = width;
    var top = height;
    var right = -1;
    var bottom = -1;

    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            if (buffer[y * stride + x * 4 + 3] <= 8) continue;
            if (x < left) left = x;
            if (y < top) top = y;
            if (x > right) right = x;
            if (y > bottom) bottom = y;
        }
    }

    return right < left || bottom < top
        ? Rectangle.Empty
        : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
}
