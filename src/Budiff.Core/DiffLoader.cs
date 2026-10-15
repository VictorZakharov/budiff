using System;
using System.IO;
using System.Text;

namespace Budiff;

public static class DiffLoader
{
    public const long MaxInlineDiffBytes = 1024 * 1024;

    public static DiffResult Build(BlobInfo old, BlobInfo @new)
    {
        if (old.IsBinary || @new.IsBinary)
        {
            return DiffResult.Message(BinaryMessage(old, @new));
        }

        if (old.Bytes > MaxInlineDiffBytes || @new.Bytes > MaxInlineDiffBytes)
        {
            return DiffResult.Message(TooLargeMessage(old, @new));
        }

        return DiffResult.FromRows(LineDiff.Compute(old.Text, @new.Text));
    }

    // A plain file view with nothing to compare against (the folder is not a
    // git repository): every line is an Unchanged row numbered on both sides,
    // so the diff renderer shows the file as-is. The trailing newline does not
    // get a blank row of its own.
    public static DiffResult BuildView(BlobInfo file)
    {
        if (file.IsBinary)
        {
            return DiffResult.Message($"Binary file not shown ({FormatSize(file.Bytes)}).");
        }

        if (file.Bytes > MaxInlineDiffBytes)
        {
            return DiffResult.Message($"File too large to show inline ({FormatSize(file.Bytes)}; limit {FormatSize(MaxInlineDiffBytes)}).");
        }

        if (file.Text.Length == 0)
        {
            return DiffResult.Message("Empty file.");
        }

        var lines = LineDiff.SplitLines(file.Text);
        var count = lines.Length > 1 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var rows = new DiffRow[count];
        for (var i = 0; i < count; i++)
        {
            rows[i] = new DiffRow(i + 1, lines[i], i + 1, lines[i], DiffKind.Unchanged);
        }

        return DiffResult.FromRows(rows);
    }

    public static BlobInfo ReadWorkingFile(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path))
            {
                return BlobInfo.Empty;
            }

            var length = new FileInfo(path).Length;
            if (length > MaxInlineDiffBytes)
            {
                return new BlobInfo(string.Empty, length, IsBinary: false);
            }

            var bytes = File.ReadAllBytes(path);
            return TextBytes.LooksBinary(bytes)
                ? new BlobInfo(string.Empty, bytes.Length, IsBinary: true)
                : new BlobInfo(TextBytes.DecodeUtf8(bytes), bytes.Length, IsBinary: false);
        }
        catch
        {
            return BlobInfo.Empty;
        }
    }

    private static string BinaryMessage(BlobInfo old, BlobInfo @new)
    {
        if (old.Bytes == 0 && @new.Bytes > 0)
        {
            return $"Binary file not shown - added ({FormatSize(@new.Bytes)}).";
        }

        if (@new.Bytes == 0 && old.Bytes > 0)
        {
            return $"Binary file not shown - deleted (was {FormatSize(old.Bytes)}).";
        }

        return $"Binary file not shown - {FormatSize(old.Bytes)} -> {FormatSize(@new.Bytes)}.";
    }

    private static string TooLargeMessage(BlobInfo old, BlobInfo @new)
        => $"File too large to diff inline ({FormatSize(Math.Max(old.Bytes, @new.Bytes))}; limit {FormatSize(MaxInlineDiffBytes)}).";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var kb = bytes / 1024.0;
        return kb < 1024 ? $"{kb:0.#} KB" : $"{kb / 1024.0:0.#} MB";
    }
}
