using System;
using System.IO;
using Budiff.Avalonia;
using Budiff.Git;

namespace Budiff.App;

// The project files a markdown preview reaches - the pictures it shows, the
// files it links to - read from disk or from the diff's revisions.
internal static class DocumentFiles
{
    public const long MaxBytes = 32L * 1024 * 1024;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // From disk for a plain folder and for the working-tree side; otherwise
    // from the revision that side of the diff shows.
    public static byte[]? Read(string root, string path, DocumentSide side, GitRepositoryScope? scope, DiffTarget? target)
    {
        if (scope is null || target is null)
        {
            return ReadDisk(root, path);
        }

        if (side == DocumentSide.New && target.HeadIsWorkingTree)
        {
            return ReadDisk(scope.RepositoryRoot, path);
        }

        var revision = side == DocumentSide.Old ? target.BaseRef : target.HeadRef ?? "HEAD";
        return GitRepositoryReader.ReadFileAtRevision(scope, path, revision, MaxBytes);
    }

    // A project path's full path; null when it climbs out of the root.
    public static string? FullPath(string root, string path)
    {
        var baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(baseDir, path.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(baseDir + Path.DirectorySeparatorChar, PathComparison) ? full : null;
    }

    // The folder a file's relative path is relative to, and that path
    // '/'-separated (just the file name if the two do not line up).
    public static (string Root, string Path) Split(string fullPath, string relativePath)
    {
        var full = Path.GetFullPath(fullPath);
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return relative.Length > 0 && full.EndsWith(Path.DirectorySeparatorChar + relative, PathComparison)
            ? (full[..^relative.Length], relativePath.Replace('\\', '/'))
            : (Path.GetDirectoryName(full) ?? full, Path.GetFileName(full));
    }

    private static byte[]? ReadDisk(string root, string path)
    {
        try
        {
            var full = FullPath(root, path);
            if (full is null)
            {
                return null;
            }

            var info = new FileInfo(full);
            return info.Exists && info.Length <= MaxBytes ? File.ReadAllBytes(full) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
