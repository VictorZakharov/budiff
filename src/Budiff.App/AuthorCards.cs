using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Budiff.Avalonia;
using Budiff.Git;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Budiff.App;

// A person's picture in the author list: initials on a colored disc until
// their GitHub avatar has downloaded (and if it cannot).
public sealed partial class AvatarVm : ObservableObject
{
    private static readonly IBrush[] Discs =
    {
        HexBrush.Parse("#8250DF"),
        HexBrush.Parse("#0969DA"),
        HexBrush.Parse("#1A7F37"),
        HexBrush.Parse("#BC4C00"),
        HexBrush.Parse("#CF222E"),
        HexBrush.Parse("#0A7EA4"),
        HexBrush.Parse("#9A6700"),
        HexBrush.Parse("#BF3989"),
    };

    public AvatarVm(RepoPerson person)
    {
        Initials = InitialsOf(person);

        // Stable across runs (string.GetHashCode is not): a person keeps a color.
        var hash = 0;
        foreach (var c in person.Login.ToLowerInvariant())
        {
            hash = unchecked((hash * 31) + c);
        }

        Background = Discs[(hash & int.MaxValue) % Discs.Length];
    }

    public string Initials { get; }

    public IBrush Background { get; }

    [ObservableProperty]
    private IImage? _image;

    // "Ada Lovelace" -> "AL", "Plato" -> "P", login "ada" -> "A".
    internal static string InitialsOf(RepoPerson person)
    {
        var words = (person.Name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length switch
        {
            0 => person.Login.Length > 0 ? char.ToUpperInvariant(person.Login[0]).ToString() : "?",
            1 => char.ToUpperInvariant(words[0][0]).ToString(),
            _ => string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[^1][0])),
        };
    }
}

// GitHub avatars, downloaded once per run (as long as the people list is
// kept), a few at a time. A failed download leaves the initials.
internal static class AvatarImages
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly ConcurrentDictionary<string, Task<IImage?>> Cache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Downloads = new(6);

    public static Task<IImage?> LoadAsync(string url) => Cache.GetOrAdd(url, DownloadAsync);

    private static async Task<IImage?> DownloadAsync(string url)
    {
        await Downloads.WaitAsync();
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            return await Task.Run(() => (IImage)new Bitmap(new MemoryStream(bytes)));
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Downloads.Release();
        }
    }
}
