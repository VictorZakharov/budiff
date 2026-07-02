using System;
using System.Text;

namespace Budiff;

// The one home for raw-bytes-to-text decisions shared by the diff loader and
// the code scanner, so the "is this binary" and BOM rules cannot drift apart.
public static class TextBytes
{
    private const int BinarySniffBytes = 8000;

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

    // Non-throwing UTF-8, stripping a leading BOM.
    public static string DecodeUtf8(byte[] bytes)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? utf8.GetString(bytes, 3, bytes.Length - 3)
            : utf8.GetString(bytes);
    }
}
