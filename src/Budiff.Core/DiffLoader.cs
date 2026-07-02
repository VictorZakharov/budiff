using System;
using System.IO;
using System.Text;

namespace Budiff;

public static class DiffLoader
{
    public const long MaxInlineDiffBytes = 1024 * 1024;
    private const int BinarySniffBytes = 8000;

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
            return LooksBinary(bytes)
                ? new BlobInfo(string.Empty, bytes.Length, IsBinary: true)
                : new BlobInfo(DecodeText(bytes), bytes.Length, IsBinary: false);
        }
        catch
        {
            return BlobInfo.Empty;
        }
    }

    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var n = Math.Min(bytes.Length, BinarySniffBytes);
        for (var i = 0; i < n; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    public static string DecodeText(byte[] bytes)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? utf8.GetString(bytes, 3, bytes.Length - 3)
            : utf8.GetString(bytes);
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
