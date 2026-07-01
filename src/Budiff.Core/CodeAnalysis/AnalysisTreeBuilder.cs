using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Budiff.CodeAnalysis;

public sealed record ExtensionStat(string Extension, int FileCount, int Lines, double Percent)
{
    public string Display => Extension.Length == 0 ? "(none)" : Extension;
}

public sealed record AuthorStat(string Author, int Commits, double Percent);

public sealed class AnalysisPathHistory
{
    public long Added { get; set; }

    public long Removed { get; set; }

    public HashSet<int> Commits { get; } = new();

    public HashSet<string> Authors { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class HistoryResult
{
    public Dictionary<string, AnalysisPathHistory> ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int CommitCount { get; set; }

    public Dictionary<string, int> AuthorCommits { get; } = new(StringComparer.OrdinalIgnoreCase);

    public AnalysisPathHistory ForPath(string path)
    {
        if (!ByPath.TryGetValue(path, out var history))
        {
            history = new AnalysisPathHistory();
            ByPath[path] = history;
        }

        return history;
    }
}

public static class AnalysisTreeBuilder
{
    private static readonly string[] ProjectExtensions = { ".csproj", ".fsproj", ".vbproj", ".esproj", ".pyproj" };

    public static IReadOnlyList<ExtensionStat> ExtensionStats(ScanResult scan)
    {
        var groups = scan.Files
            .GroupBy(f => f.Extension)
            .Select(g => (Ext: g.Key, Count: g.Count(), Lines: g.Sum(f => f.Lines.Total)))
            .ToList();
        var total = groups.Sum(g => (long)g.Lines);

        return groups
            .Select(g => new ExtensionStat(g.Ext, g.Count, g.Lines, total > 0 ? g.Lines * 100.0 / total : 0))
            .OrderByDescending(e => e.Lines)
            .ThenBy(e => e.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<AuthorStat> AuthorStats(HistoryResult? history)
    {
        if (history is null)
        {
            return Array.Empty<AuthorStat>();
        }

        var total = history.AuthorCommits.Values.Sum();
        return history.AuthorCommits
            .Select(kv => new AuthorStat(kv.Key, kv.Value, total > 0 ? kv.Value * 100.0 / total : 0))
            .OrderByDescending(a => a.Commits)
            .ThenBy(a => a.Author, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static AnalysisNode Build(
        ScanResult scan,
        HistoryResult? history,
        AnalysisGrouping grouping,
        AnalysisSortKey sortKey,
        bool ascending,
        ISet<string>? includedExtensions = null,
        string? search = null,
        bool allTime = false,
        ISet<string>? includedAuthors = null)
    {
        IEnumerable<FileStat> filtered = scan.Files;
        if (includedExtensions is not null)
        {
            filtered = filtered.Where(f => includedExtensions.Contains(f.Extension));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(f => f.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (includedAuthors is not null && history is not null)
        {
            filtered = filtered.Where(f =>
                history.ByPath.TryGetValue(f.RelativePath, out var h) && h.Authors.Overlaps(includedAuthors));
        }

        var files = filtered as IReadOnlyList<FileStat> ?? filtered.ToList();
        var root = new AnalysisNode(RootName(scan.Root), string.Empty, AnalysisNodeKind.Root);
        var folderIndex = new Dictionary<string, AnalysisNode>(StringComparer.OrdinalIgnoreCase);

        if (grouping == AnalysisGrouping.Folders)
        {
            foreach (var file in files)
            {
                var parent = EnsureFolders(root, string.Empty, DirSegments(file.RelativePath), folderIndex);
                parent.Children.Add(MakeLeaf(file, history, allTime));
            }
        }
        else
        {
            BuildProjects(root, files, scan.Files, history, folderIndex, allTime);
        }

        root.Aggregate();
        root.SortAndScale(sortKey, ascending);
        return root;
    }

    private static void BuildProjects(
        AnalysisNode root,
        IReadOnlyList<FileStat> files,
        IReadOnlyList<FileStat> allFiles,
        HistoryResult? history,
        Dictionary<string, AnalysisNode> folderIndex,
        bool allTime)
    {
        var projects = DetectProjects(allFiles);
        var projectIndex = new Dictionary<string, AnalysisNode>(StringComparer.OrdinalIgnoreCase);
        AnalysisNode? ungrouped = null;

        foreach (var file in files)
        {
            var dirs = DirSegments(file.RelativePath);
            var owner = FindOwner(file.RelativePath, projects);
            if (owner is null)
            {
                ungrouped ??= AddChild(root, new AnalysisNode("(ungrouped)", "(ungrouped)", AnalysisNodeKind.Ungrouped));
                var parent = EnsureFolders(ungrouped, string.Empty, dirs, folderIndex);
                parent.Children.Add(MakeLeaf(file, history, allTime));
                continue;
            }

            var projectNode = EnsureProject(owner.Value, root, projectIndex);
            var ownerDepth = owner.Value.Dir.Length == 0 ? 0 : owner.Value.Dir.Split('/').Length;
            var within = dirs.Skip(ownerDepth).ToArray();
            var projectParent = EnsureFolders(projectNode, owner.Value.Dir, within, folderIndex);
            projectParent.Children.Add(MakeLeaf(file, history, allTime));
        }
    }

    private static AnalysisNode EnsureFolders(
        AnalysisNode start,
        string realPrefix,
        IReadOnlyList<string> dirs,
        Dictionary<string, AnalysisNode> folderIndex)
    {
        var parent = start;
        var real = realPrefix;
        foreach (var segment in dirs)
        {
            real = real.Length == 0 ? segment : real + "/" + segment;
            if (!folderIndex.TryGetValue(real, out var node))
            {
                node = AddChild(parent, new AnalysisNode(segment, real, AnalysisNodeKind.Folder));
                folderIndex[real] = node;
            }

            parent = node;
        }

        return parent;
    }

    private static AnalysisNode EnsureProject(
        ProjectMarker owner,
        AnalysisNode root,
        Dictionary<string, AnalysisNode> projectIndex)
    {
        if (!projectIndex.TryGetValue(owner.Dir, out var node))
        {
            node = AddChild(root, new AnalysisNode(owner.Name, owner.Dir, AnalysisNodeKind.Project));
            projectIndex[owner.Dir] = node;
        }

        return node;
    }

    private static AnalysisNode AddChild(AnalysisNode parent, AnalysisNode child)
    {
        parent.Children.Add(child);
        return child;
    }

    private static AnalysisNode MakeLeaf(FileStat file, HistoryResult? history, bool allTime)
    {
        var leaf = new AnalysisNode(NameOf(file.RelativePath), file.RelativePath, AnalysisNodeKind.File)
        {
            Lines = file.Lines,
            FileCount = 1,
            IsBinary = file.IsBinary,
        };

        if (history is not null && history.ByPath.TryGetValue(file.RelativePath, out var h))
        {
            leaf.CommitIds.UnionWith(h.Commits);
            leaf.Authors.UnionWith(h.Authors);
            if (!allTime)
            {
                leaf.Added = h.Added;
                leaf.Removed = h.Removed;
            }
        }

        if (allTime)
        {
            leaf.Added = file.Lines.Total;
            leaf.Removed = 0;
        }

        return leaf;
    }

    private readonly record struct ProjectMarker(string Dir, string Name, bool FromProjectFile);

    private static List<ProjectMarker> DetectProjects(IReadOnlyList<FileStat> files)
    {
        var byDir = new Dictionary<string, ProjectMarker>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var name = NameOf(file.RelativePath);
            var ext = file.Extension;
            var dir = DirOf(file.RelativePath);

            if (ProjectExtensions.Contains(ext))
            {
                byDir[dir] = new ProjectMarker(dir, Path.GetFileNameWithoutExtension(name), true);
            }
            else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
            {
                if (!byDir.TryGetValue(dir, out var existing) || !existing.FromProjectFile)
                {
                    byDir[dir] = new ProjectMarker(dir, PackageName(dir), false);
                }
            }
        }

        return byDir.Values
            .OrderByDescending(p => p.Dir.Length == 0 ? 0 : p.Dir.Split('/').Length)
            .ToList();
    }

    private static ProjectMarker? FindOwner(string relativePath, List<ProjectMarker> projects)
    {
        var dir = DirOf(relativePath);
        foreach (var project in projects)
        {
            if (project.Dir.Length == 0)
            {
                return project;
            }

            if (dir.Equals(project.Dir, StringComparison.OrdinalIgnoreCase)
                || dir.StartsWith(project.Dir + "/", StringComparison.OrdinalIgnoreCase))
            {
                return project;
            }
        }

        return null;
    }

    private static string PackageName(string dir) => dir.Length == 0 ? "(root)" : dir.Split('/').Last();

    private static string RootName(string root)
    {
        var trimmed = root.TrimEnd('/', '\\');
        var name = Path.GetFileName(trimmed);
        return name.Length == 0 ? trimmed : name;
    }

    private static string NameOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? relativePath : relativePath[(slash + 1)..];
    }

    private static string DirOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    private static string[] DirSegments(string relativePath)
    {
        var dir = DirOf(relativePath);
        return dir.Length == 0 ? Array.Empty<string>() : dir.Split('/');
    }
}
