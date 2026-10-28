using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;

namespace Budiff.App;

// "Show in Explorer": the system file manager, opened on a file (selected in
// its folder) or a folder.
internal static class FileManager
{
    public static string RevealLabel { get; } =
        OperatingSystem.IsWindows() ? "Show in Explorer"
        : OperatingSystem.IsMacOS() ? "Reveal in Finder"
        : "Open containing folder";

    public static void Reveal(string path)
    {
        // A file gone from disk (deleted in the working tree, or only in a
        // PR or commit): show the nearest folder that is still there.
        var target = Path.GetFullPath(path);
        var exists = File.Exists(target) || Directory.Exists(target);
        while (!exists && Path.GetDirectoryName(target) is { } parent)
        {
            target = parent;
            exists = Directory.Exists(target);
        }

        if (!exists)
        {
            return;
        }

        var isFile = File.Exists(target);
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("explorer.exe", isFile ? $"/select,\"{target}\"" : $"\"{target}\"")
            : OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open", isFile ? new[] { "-R", target } : new[] { target })
                : new ProcessStartInfo("xdg-open", isFile ? Path.GetDirectoryName(target)! : target);
        start.UseShellExecute = false;
        using var process = Process.Start(start);
    }

    public const string ExpandAllLabel = "Expand all";
    public const string CollapseAllLabel = "Collapse all";

    // The right-click menu of a tree row or a city building. A tree folder
    // (expand / collapse given) also expands or collapses everything under it.
    public static ContextMenu Menu(Action reveal, Action? expandAll = null, Action? collapseAll = null)
    {
        var items = new List<Control>();
        if (expandAll is not null && collapseAll is not null)
        {
            items.Add(Item(ExpandAllLabel, AppIconKind.ChevronDown, expandAll));
            items.Add(Item(CollapseAllLabel, AppIconKind.ChevronRight, collapseAll));
            items.Add(new Separator());
        }

        items.Add(Item(RevealLabel, AppIconKind.FolderOpenOutline, reveal));
        return new ContextMenu { ItemsSource = items };
    }

    private static MenuItem Item(string header, AppIconKind icon, Action click)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new AppIcon { Kind = icon, Width = 14, Height = 14 },
        };
        item.Click += (_, _) => click();
        return item;
    }
}
