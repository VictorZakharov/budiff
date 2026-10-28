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

    // The right-click menu of a tree row or a city building.
    public static ContextMenu Menu(Action reveal)
    {
        var item = new MenuItem
        {
            Header = RevealLabel,
            Icon = new AppIcon { Kind = AppIconKind.FolderOpenOutline, Width = 14, Height = 14 },
        };
        item.Click += (_, _) => reveal();
        return new ContextMenu { ItemsSource = new[] { item } };
    }
}
