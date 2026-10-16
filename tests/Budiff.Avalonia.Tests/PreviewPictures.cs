using Avalonia.Controls;
using Avalonia.VisualTree;
using Budiff.Avalonia.Markdown;

namespace Budiff.Avalonia.Tests;

// For a wait on a preview's pictures that gives up: how far each one got
// (still loading, failed and why, decoded but never shown, ...).
internal static class PreviewPictures
{
    public static string Describe(Control host)
    {
        var slots = host.GetVisualDescendants().OfType<MarkdownRenderer.PictureSlot>()
            .Select(s => $"{s.Source ?? s.Alt}: {s.LoadState ?? "not a project file"}")
            .ToList();
        return slots.Count == 0 ? "no pictures in the preview" : string.Join("; ", slots);
    }
}
