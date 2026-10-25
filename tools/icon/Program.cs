using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Generates src/SchoolPdfSorter.App/icon.ico: a solid black document with a folded corner
// and a white arrow, in the same flat black-and-white style as the video sorter icon.

var outIco = args[0];
var preview = args.Length > 1 ? args[1] : null;

int[] sizes = [16, 24, 32, 48, 64, 128, 256];

var frames = new Dictionary<int, Bitmap>();

foreach (var size in sizes)
    frames[size] = Render(size);

// A 256px preview so the drawing can be inspected. It is composited on white: the icon
// itself keeps a transparent background, which would be invisible in a preview.
var previewFrame = Render(256);

using (var canvas = new Bitmap(256, 256, PixelFormat.Format32bppArgb))
using (var g = Graphics.FromImage(canvas))
{
    g.Clear(Color.White);
    g.DrawImage(previewFrame, 0, 0, 256, 256);
    canvas.Save(preview ?? Path.Combine(Path.GetTempPath(), "icon-preview.png"), ImageFormat.Png);
}

previewFrame.Dispose();

WriteIco(outIco, frames);

// A strip of the small sizes at 5x, so the detail that Explorer actually shows can be judged.
if (preview is not null)
{
    int[] small = [16, 24, 32, 48];
    const int zoom = 5;
    const int gap = 8;

    using var strip = new Bitmap(
        small.Sum(s => s * zoom) + gap * (small.Length + 1),
        48 * zoom + gap * 2,
        PixelFormat.Format32bppArgb);

    using (var g = Graphics.FromImage(strip))
    {
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        var x = gap;

        foreach (var size in small)
        {
            g.DrawImage(frames[size], new Rectangle(x, gap, size * zoom, size * zoom));
            x += size * zoom + gap;
        }

        strip.Save(Path.ChangeExtension(preview, "-small.png"), ImageFormat.Png);
    }

    Console.WriteLine(Path.ChangeExtension(preview, "-small.png"));
}

foreach (var (size, bitmap) in frames.OrderBy(f => f.Key))
    bitmap.Dispose();

Console.WriteLine($"wrote {outIco} with {sizes.Length} sizes: {string.Join(", ", sizes)}");
Console.WriteLine(preview ?? Path.Combine(Path.GetTempPath(), "icon-preview.png"));

// ---------------------------------------------------------------- rendering

/// <summary>Draws the glyph at a given pixel size.</summary>
static Bitmap Render(int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

    using var g = Graphics.FromImage(bitmap);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;

    // Opaque white ground, same as the video sorter icon: a transparent background would
    // leave a black page unreadable against a dark Explorer, where it looks like the glyph
    // is missing.
    g.Clear(Color.White);

    float P(double v) => (float)(v / 100.0 * size);

    // The page: a portrait sheet with a cut corner at the top right, centred on the canvas
    // so the glyph does not sit heavy on one side at any size.
    var page = new[]
    {
        new PointF(P(17), P(6)),
        new PointF(P(57), P(6)),
        new PointF(P(83), P(32)),
        new PointF(P(83), P(94)),
        new PointF(P(17), P(94))
    };

    using (var brush = new SolidBrush(Color.Black))
        g.FillPolygon(brush, page);

    // The arrow that does the "file this" job, in negative space. Below 24px the shaft is
    // thickened, and at 16px the shaft is dropped entirely: a lone wedge survives the size
    // better than a shape that collapses into a blob.
    using (var brush = new SolidBrush(Color.White))
    {
        if (size <= 16)
        {
            var wedge = new[]
            {
                new PointF(P(24), P(42)),
                new PointF(P(76), P(42)),
                new PointF(P(50), P(78))
            };
            g.FillPolygon(brush, wedge);
        }
        else
        {
            var shaftWidth = size <= 24 ? P(23) : P(14);
            var shaft = new RectangleF(P(50) - shaftWidth / 2, P(38), shaftWidth, P(24));
            g.FillRectangle(brush, shaft);

            var half = size <= 24 ? P(24) : P(19);
            var head = new[]
            {
                new PointF(P(50) - half, P(54)),
                new PointF(P(50) + half, P(54)),
                new PointF(P(50), P(80))
            };
            g.FillPolygon(brush, head);
        }
    }

    return bitmap;
}

/// <summary>Writes a multi-size .ico of 32bpp BMP frames plus a zeroed AND mask.</summary>
static void WriteIco(string path, Dictionary<int, Bitmap> frames)
{
    var ordered = frames.OrderBy(f => f.Key).ToList();
    var payloads = ordered.Select(pair => Encode(pair.Value)).ToList();

    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);

    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)ordered.Count);

    var offset = 6 + 16 * ordered.Count;

    for (var i = 0; i < ordered.Count; i++)
    {
        var size = ordered[i].Key;
        var payload = payloads[i];

        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)payload.Length);
        writer.Write((uint)offset);

        offset += payload.Length;
    }

    foreach (var payload in payloads)
        writer.Write(payload);
}

/// <summary>One frame as a BITMAPINFOHEADER with BGRA rows (bottom-up) and an AND mask.</summary>
static byte[] Encode(Bitmap bitmap)
{
    var width = bitmap.Width;
    var height = bitmap.Height;
    var rowBytes = width * 4;
    var maskRowBytes = ((width + 31) / 32) * 4;
    var headerSize = 40;
    var imageSize = rowBytes * height;
    var maskSize = maskRowBytes * height;

    var buffer = new byte[headerSize + imageSize + maskSize];

    BitConverter.GetBytes((uint)headerSize).CopyTo(buffer, 0);
    BitConverter.GetBytes((uint)width).CopyTo(buffer, 4);

    // The height in an ICO counts the XOR bitmap and the AND mask together. Writing only the
    // XOR height with a mask appended is read inconsistently, which crops and stretches the
    // glyph in Explorer.
    BitConverter.GetBytes((uint)(height * 2)).CopyTo(buffer, 8);
    BitConverter.GetBytes((ushort)1).CopyTo(buffer, 12);
    BitConverter.GetBytes((ushort)32).CopyTo(buffer, 14);
    BitConverter.GetBytes((uint)imageSize).CopyTo(buffer, 20);
    BitConverter.GetBytes((uint)(width * height)).CopyTo(buffer, 24);

    var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

    try
    {
        var stride = data.Stride;
        var scan = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            // The buffer holds rows bottom-up, while the bitmap is top-down.
            var sourceY = height - 1 - y;
            var row = headerSize + y * rowBytes;
            var ptr = IntPtr.Add(data.Scan0, sourceY * stride);
            System.Runtime.InteropServices.Marshal.Copy(ptr, scan, 0, stride);

            for (var x = 0; x < width; x++)
            {
                var s = x * 4;
                buffer[row + s + 0] = scan[s + 0]; // B
                buffer[row + s + 1] = scan[s + 1]; // G
                buffer[row + s + 2] = scan[s + 2]; // R
                buffer[row + s + 3] = scan[s + 3]; // A
            }
        }
    }
    finally
    {
        bitmap.UnlockBits(data);
    }

    // The AND mask matters: zero means "opaque", so a transparent background that is left
    // zeroed renders as a solid black square in shells that prefer the mask over the alpha
    // channel. One bit per pixel, 1 = transparent, starting after the header and the XOR data.
    for (var y = 0; y < height; y++)
    {
        var maskRow = headerSize + imageSize + y * maskRowBytes;

        for (var x = 0; x < width; x++)
        {
            if (buffer[headerSize + y * rowBytes + x * 4 + 3] >= 128)
                continue;

            buffer[maskRow + x / 8] |= (byte)(1 << (7 - x % 8));
        }
    }

    return buffer;
}
