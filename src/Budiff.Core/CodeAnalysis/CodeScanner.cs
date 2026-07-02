using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Budiff.CodeAnalysis;

public sealed class FileStat
{
    public required string RelativePath { get; init; }

    public required string Extension { get; init; }

    public LineCounts Lines { get; init; }

    public bool IsBinary { get; init; }
}

public sealed class ScanResult
{
    public required string Root { get; init; }

    public required IReadOnlyList<FileStat> Files { get; init; }

    public bool HasGit { get; init; }

    public bool Truncated { get; init; }
}

public static class CodeScanner
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules",
    };

    private const long MaxCountBytes = 8L * 1024 * 1024;

    public static ScanResult Scan(
        string root,
        IReadOnlyList<string>? relativePaths = null,
        bool hasGit = false,
        int maxFiles = 200_000,
        CancellationToken ct = default)
    {
        var paths = relativePaths is null
            ? EnumerateFiles(root, maxFiles).ToList()
            : relativePaths.Select(NormalizePath).ToList();

        var kept = paths.Where(p => !IsSkipped(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var truncated = kept.Count > maxFiles;
        if (truncated)
        {
            kept = kept.Take(maxFiles).ToList();
        }

        var stats = new FileStat?[kept.Count];
        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
        };
        Parallel.For(0, kept.Count, options, i => stats[i] = CountFile(root, kept[i]));

        return new ScanResult
        {
            Root = root,
            Files = stats.Where(s => s is not null).Select(s => s!).ToList(),
            HasGit = hasGit,
            Truncated = truncated,
        };
    }

    private static IEnumerable<string> EnumerateFiles(string root, int maxFiles)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var yielded = 0;

        while (pending.Count > 0 && yielded <= maxFiles)
        {
            var dir = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                children = Array.Empty<string>();
            }

            foreach (var child in children)
            {
                if (!SkipDirs.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch
            {
                files = Array.Empty<string>();
            }

            foreach (var file in files)
            {
                yielded++;
                yield return NormalizePath(Path.GetRelativePath(root, file));
                if (yielded > maxFiles)
                {
                    yield break;
                }
            }
        }
    }

    private static bool IsSkipped(string relativePath)
        => relativePath.Split('/').Any(segment => SkipDirs.Contains(segment));

    private static FileStat? CountFile(string root, string relativePath)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length > MaxCountBytes)
            {
                return new FileStat { RelativePath = relativePath, Extension = ext, IsBinary = true };
            }

            var bytes = File.ReadAllBytes(fullPath);
            if (TextBytes.LooksBinary(bytes))
            {
                return new FileStat { RelativePath = relativePath, Extension = ext, IsBinary = true };
            }

            var text = TextBytes.DecodeUtf8(bytes);
            var counts = LineCounter.Count(text, CommentSyntax.For(relativePath));
            return new FileStat { RelativePath = relativePath, Extension = ext, Lines = counts };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizePath(string path) => RepoPaths.Normalize(path);
}
