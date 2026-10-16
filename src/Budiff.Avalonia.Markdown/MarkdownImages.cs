using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using global::Avalonia;
using global::Avalonia.Media.Imaging;

namespace Budiff.Avalonia.Markdown;

// Loads the project images a preview shows: read and decoded off the UI
// thread, kept by content so a re-render (or the same picture on both sides
// of the diff) does not decode it again.
internal sealed class MarkdownImages
{
    private const int MaxBytes = 32 * 1024 * 1024;

    // Wider pictures are scaled down once decoded; the preview is at most
    // 1000px wide, so this still leaves room for high-DPI screens.
    private const int MaxPixelWidth = 2400;
    private const int Capacity = 64;

    private readonly ConcurrentDictionary<string, Bitmap> _decoded = new(StringComparer.Ordinal);

    // Null when the file is absent on that side, too big, or not a raster
    // image Avalonia decodes (SVG is not).
    public Task<Bitmap?> LoadAsync(DocumentPreviewContext context, string path, DocumentSide side)
        => Task.Run(() => Load(context, path, side));

    private Bitmap? Load(DocumentPreviewContext context, string path, DocumentSide side)
    {
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var bytes = context.ReadFile(path, side);
            if (bytes is null || bytes.Length == 0 || bytes.Length > MaxBytes)
            {
                return null;
            }

            var key = Convert.ToHexString(SHA256.HashData(bytes));
            if (_decoded.TryGetValue(key, out var cached))
            {
                return cached;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new Bitmap(stream);
            if (bitmap.PixelSize.Width > MaxPixelWidth)
            {
                var height = Math.Max(1, (int)Math.Round(bitmap.PixelSize.Height * (double)MaxPixelWidth / bitmap.PixelSize.Width));
                var scaled = bitmap.CreateScaledBitmap(new PixelSize(MaxPixelWidth, height));
                bitmap.Dispose();
                bitmap = scaled;
            }

            // Crude bound: past capacity start over (shown images keep their
            // own reference, so nothing on screen is disposed).
            if (_decoded.Count >= Capacity)
            {
                _decoded.Clear();
            }

            return _decoded.GetOrAdd(key, bitmap);
        }
        catch (Exception)
        {
            // Unreadable or not an image: the placeholder stays.
            return null;
        }
    }
}
