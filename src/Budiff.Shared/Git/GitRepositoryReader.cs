using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Budiff;
using Budiff.CodeAnalysis;
using LibGit2Sharp;

namespace Budiff.Git;

public static class GitRepositoryReader
{
    internal const int DefaultHistoryCommitLimit = 1_000;
    internal const string HistoryMaxCommitsEnvironmentVariable = "BUDIFF_HISTORY_MAX_COMMITS";

    public static GitRepositoryScope Open(string startPath, bool gitRoot)
    {
        var start = CanonicalPath(Path.GetFullPath(string.IsNullOrWhiteSpace(startPath) ? Environment.CurrentDirectory : startPath));
        if (File.Exists(start))
        {
            start = Path.GetDirectoryName(start) ?? start;
        }

        var repoDir = DiscoverRepositoryRoot(start);
        if (repoDir is null)
        {
            throw new InvalidOperationException("This folder is not inside a git repository.");
        }

        using var repo = new Repository(repoDir);
        var root = NormalizeFullPath(repo.Info.WorkingDirectory);
        var scopeRoot = gitRoot ? root : NormalizeFullPath(start);
        if (!IsUnder(root, scopeRoot))
        {
            scopeRoot = root;
        }

        var prefix = ToRepoPath(root, scopeRoot);
        string? branch = null;
        try
        {
            branch = repo.Head?.FriendlyName;
        }
        catch
        {
            branch = null;
        }

        return new GitRepositoryScope(root, scopeRoot, prefix, gitRoot || prefix.Length == 0, branch);
    }

    public static IReadOnlyList<GitReferenceOption> GetReferenceOptions(string repositoryRoot)
    {
        return WithRepo(repositoryRoot, repo =>
        {
            var options = new List<GitReferenceOption>
            {
                new("WORKTREE", "Working tree", IsWorkingTree: true),
                new("HEAD", "HEAD"),
            };

            AddBranchOptions(repo, options);

            foreach (var commit in repo.Commits.Take(50))
            {
                var shortSha = commit.Sha.Length >= 7 ? commit.Sha[..7] : commit.Sha;
                options.Add(new GitReferenceOption(commit.Sha, $"{shortSha} {commit.MessageShort}"));
            }

            return (IReadOnlyList<GitReferenceOption>)options;
        }, Array.Empty<GitReferenceOption>());
    }

    // Branches only (plus HEAD) - the diff base picker. Head is always the working
    // tree or HEAD, so no commit or working-tree entries here.
    public static IReadOnlyList<GitReferenceOption> GetBranchOptions(string repositoryRoot)
    {
        return WithRepo(repositoryRoot, repo =>
        {
            var options = new List<GitReferenceOption> { new("HEAD", "HEAD") };
            AddBranchOptions(repo, options);
            return (IReadOnlyList<GitReferenceOption>)options;
        }, Array.Empty<GitReferenceOption>());
    }

    // Local branches first (current one labeled), then remotes, skipping the
    // symbolic */HEAD entries and anything already in the list.
    private static void AddBranchOptions(Repository repo, List<GitReferenceOption> options)
    {
        var currentBranch = repo.Head?.FriendlyName;
        foreach (var branch in repo.Branches
                     .Where(b => !b.FriendlyName.EndsWith("/HEAD", StringComparison.Ordinal))
                     .OrderBy(b => b.IsRemote)
                     .ThenBy(b => b.FriendlyName, StringComparer.OrdinalIgnoreCase))
        {
            var label = !branch.IsRemote && branch.FriendlyName == currentBranch
                ? branch.FriendlyName + " (current)"
                : branch.FriendlyName;
            if (options.All(o => !string.Equals(o.Id, branch.FriendlyName, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new GitReferenceOption(branch.FriendlyName, label));
            }
        }
    }

    public static ChangeSet ListChanges(GitRepositoryScope scope, DiffTarget target)
    {
        return WithRepo(scope.RepositoryRoot, repo =>
        {
            var files = target.HeadIsWorkingTree
                ? ListWorkingTreeChanges(repo, scope, target)
                : ListTreeChanges(repo, scope, target);

            var history = LoadHistory(repo, files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            var enriched = files
                .Select(f => history.TryGetValue(f.Path, out var h)
                    ? f with { AuthorCount = h.Authors.Count, CommitCount = h.Commits.Count }
                    : f)
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new ChangeSet(scope, target, enriched);
        }, new ChangeSet(scope, target, Array.Empty<ChangedFile>()));
    }

    // Blame for the BASE side of a diff (the left side is what blame describes;
    // working-tree lines on the right have no commit yet). Indexed by 0-based
    // line; entries the blame does not cover stay null. Empty when the base ref
    // is not a commit (e.g. the empty tree) or the file is not tracked there.
    public static IReadOnlyList<BlameLineInfo?> LoadBlame(GitRepositoryScope scope, string path, string baseRef)
    {
        return WithRepo<IReadOnlyList<BlameLineInfo?>>(scope.RepositoryRoot, repo =>
        {
            var commit = ResolveCommit(repo, baseRef);
            if (commit is null)
            {
                return Array.Empty<BlameLineInfo?>();
            }

            var lines = new List<BlameLineInfo?>();
            foreach (var hunk in repo.Blame(path.Replace('\\', '/'), new BlameOptions { StartingAt = commit }))
            {
                var start = hunk.FinalStartLineNumber;
                var end = start + hunk.LineCount;
                while (lines.Count < end)
                {
                    lines.Add(null);
                }

                var c = hunk.FinalCommit;
                if (c is null)
                {
                    continue;
                }

                var info = new BlameLineInfo(
                    c.Sha.Length >= 7 ? c.Sha[..7] : c.Sha,
                    c.Author?.Name ?? string.Empty,
                    c.Author?.Email ?? string.Empty,
                    c.Author?.When ?? default,
                    c.MessageShort ?? string.Empty);
                for (var i = start; i < end; i++)
                {
                    lines[i] = info;
                }
            }

            return lines;
        }, Array.Empty<BlameLineInfo?>());
    }

    public static DiffResult LoadDiff(GitRepositoryScope scope, ChangedFile file, DiffTarget target)
    {
        return WithRepo(scope.RepositoryRoot, repo =>
        {
            var oldBlob = ReadBlob(ResolveCommit(repo, target.BaseRef)?.Tree, (file.OldPath ?? file.Path).Replace('\\', '/'));
            BlobInfo newBlob;
            if (target.HeadIsWorkingTree)
            {
                newBlob = file.State == GitFileState.Deleted ? BlobInfo.Empty : DiffLoader.ReadWorkingFile(file.FullPath);
            }
            else
            {
                newBlob = ReadBlob(ResolveCommit(repo, target.HeadRef ?? "HEAD")?.Tree, file.Path.Replace('\\', '/'));
            }

            return DiffLoader.Build(oldBlob, newBlob);
        }, DiffResult.Message("Could not load this diff."));
    }

    // Line counts for root-relative paths as they are at a revision, not on
    // disk. A path absent there (deleted by that revision) counts as an empty
    // file, so it still lists - with its removals - in the bird's-eye.
    public static IReadOnlyList<FileStat> CountAtRevision(string root, string revision, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        // Not WithRepo: a missing revision must fail loudly, not read as "no files".
        var scope = Open(root, gitRoot: false);
        using var repo = new Repository(scope.RepositoryRoot);
        var tree = ResolveCommit(repo, revision)?.Tree
            ?? throw new InvalidOperationException($"Revision {revision} is not available locally.");
        var files = new List<FileStat>(paths.Count);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var repoPath = scope.ScopePrefix.Length == 0 ? path : scope.ScopePrefix + "/" + path;
            if (tree[repoPath]?.Target is not Blob blob)
            {
                files.Add(new FileStat { RelativePath = path, Extension = Path.GetExtension(path).ToLowerInvariant() });
                continue;
            }

            if (!CodeScanner.IsCountable(blob.Size))
            {
                files.Add(CodeScanner.Oversized(path));
                continue;
            }

            using var content = blob.GetContentStream();
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            files.Add(CodeScanner.CountBytes(path, buffer.ToArray()));
        }

        return files;
    }

    private static List<ChangedFile> ListWorkingTreeChanges(Repository repo, GitRepositoryScope scope, DiffTarget target)
    {
        var files = new List<ChangedFile>();
        var statusOptions = new StatusOptions { IncludeIgnored = false, RecurseUntrackedDirs = true };
        foreach (var entry in repo.RetrieveStatus(statusOptions))
        {
            var state = Map(entry.State);
            if (state == GitFileState.None)
            {
                continue;
            }

            var path = NormalizeRepoPath(entry.FilePath);
            if (!InScope(scope, path))
            {
                continue;
            }

            var fullPath = Path.Combine(scope.RepositoryRoot, path.Replace('/', Path.DirectorySeparatorChar));
            var oldBlob = ReadBlob(ResolveCommit(repo, target.BaseRef)?.Tree, path);
            var newBlob = state == GitFileState.Deleted ? BlobInfo.Empty : DiffLoader.ReadWorkingFile(fullPath);
            var diff = DiffLoader.Build(oldBlob, newBlob);
            var churn = diff.Rows is { } rows ? DiffMetrics.Churn(rows) : new DiffChurn(0, 0);
            files.Add(new ChangedFile(path, null, fullPath, state, churn, 0, 0, oldBlob.IsBinary || newBlob.IsBinary));
        }

        return files;
    }

    private static List<ChangedFile> ListTreeChanges(Repository repo, GitRepositoryScope scope, DiffTarget target)
    {
        var files = new List<ChangedFile>();
        var oldCommit = ResolveCommit(repo, target.BaseRef);
        var newCommit = ResolveCommit(repo, target.HeadRef ?? "HEAD");
        if (newCommit is null)
        {
            return files;
        }

        var compareOptions = new CompareOptions { Similarity = SimilarityOptions.Renames };
        foreach (var change in repo.Diff.Compare<TreeChanges>(oldCommit?.Tree, newCommit.Tree, compareOptions))
        {
            var path = NormalizeRepoPath(change.Path);
            if (!InScope(scope, path))
            {
                continue;
            }

            var oldPath = string.IsNullOrWhiteSpace(change.OldPath) ? null : NormalizeRepoPath(change.OldPath);
            var fullPath = Path.Combine(scope.RepositoryRoot, path.Replace('/', Path.DirectorySeparatorChar));
            var oldBlob = ReadBlob(oldCommit?.Tree, (oldPath ?? path).Replace('\\', '/'));
            var newBlob = ReadBlob(newCommit.Tree, path);
            var diff = DiffLoader.Build(oldBlob, newBlob);
            var churn = diff.Rows is { } rows ? DiffMetrics.Churn(rows) : new DiffChurn(0, 0);
            files.Add(new ChangedFile(path, oldPath, fullPath, MapChangeKind(change.Status), churn, 0, 0, oldBlob.IsBinary || newBlob.IsBinary));
        }

        return files;
    }

    private static Dictionary<string, PathHistory> LoadHistory(Repository repo, IReadOnlyCollection<string> paths)
    {
        var historyPaths = paths
            .Select(NormalizeRepoPath)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = historyPaths.ToDictionary(p => p, _ => new PathHistory(), StringComparer.OrdinalIgnoreCase);
        if (result.Count == 0)
        {
            return result;
        }

        var maxCommits = HistoryCommitLimit();
        if (maxCommits <= 0)
        {
            return result;
        }

        var compareOptions = new CompareOptions { Similarity = SimilarityOptions.Renames };
        var explicitPathsOptions = new ExplicitPathsOptions { ShouldFailOnUnmatchedPath = false };
        var commitIndex = -1;
        foreach (var commit in repo.Commits)
        {
            commitIndex++;
            if (commitIndex >= maxCommits)
            {
                break;
            }

            Tree? parentTree = commit.Parents.FirstOrDefault()?.Tree;
            TreeChanges changes;
            try
            {
                changes = repo.Diff.Compare<TreeChanges>(
                    parentTree,
                    commit.Tree,
                    historyPaths,
                    explicitPathsOptions,
                    compareOptions);
            }
            catch
            {
                continue;
            }

            var author = commit.Author?.Name ?? string.Empty;
            foreach (var change in changes)
            {
                var path = NormalizeRepoPath(change.Path);
                var oldPath = string.IsNullOrWhiteSpace(change.OldPath) ? null : NormalizeRepoPath(change.OldPath);
                var key = result.ContainsKey(path)
                    ? path
                    : oldPath is not null && result.ContainsKey(oldPath)
                        ? oldPath
                        : null;

                if (key is null)
                {
                    continue;
                }

                result[key].Commits.Add(commitIndex);
                if (author.Length > 0)
                {
                    result[key].Authors.Add(author);
                }
            }
        }

        return result;
    }

    internal static int HistoryCommitLimit()
    {
        var configured = Environment.GetEnvironmentVariable(HistoryMaxCommitsEnvironmentVariable);
        return int.TryParse(configured, out var limit)
            ? Math.Max(0, limit)
            : DefaultHistoryCommitLimit;
    }

    private static BlobInfo ReadBlob(Tree? tree, string path)
    {
        if (tree is null || tree[path.Replace('\\', '/')]?.Target is not Blob blob)
        {
            return BlobInfo.Empty;
        }

        if (blob.IsBinary)
        {
            return new BlobInfo(string.Empty, blob.Size, IsBinary: true);
        }

        // Oversized blobs never reach the inline diff (DiffLoader.Build shows a
        // placeholder), so do not materialize the whole text just to discard it.
        return blob.Size > DiffLoader.MaxInlineDiffBytes
            ? new BlobInfo(string.Empty, blob.Size, IsBinary: false)
            : new BlobInfo(blob.GetContentText(), blob.Size, IsBinary: false);
    }

    private static Commit? ResolveCommit(Repository repo, string refName)
    {
        if (string.IsNullOrWhiteSpace(refName))
        {
            return repo.Head?.Tip;
        }

        if (refName.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return repo.Head?.Tip;
        }

        if (repo.Branches[refName] is { } branch)
        {
            return branch.Tip;
        }

        return repo.Lookup<Commit>(refName);
    }

    // Repository.Discover silently returns null under NativeAOT (verified
    // empirically; its out-buffer marshaling path does not survive AOT), so
    // walk up for a .git entry ourselves. A .git FILE (worktree / submodule
    // pointer) also counts - new Repository(dir) resolves it natively.
    internal static string? DiscoverRepositoryRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var marker = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    // The path as the file system spells it - drive letter and every segment.
    // cmd.exe keeps a typed `g:\git\repo` as the working directory verbatim,
    // and libgit2 matches safe.directory entries case-sensitively, so a repo
    // that needs one (e.g. on a drive owned by another SID) fails its
    // ownership check unless libgit2 gets the canonical spelling. Only Windows
    // needs this; elsewhere (and on any IO error) the path passes through.
    internal static string CanonicalPath(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return fullPath;
            }

            var current = root.Length >= 2 && root[1] == ':'
                ? char.ToUpperInvariant(root[0]) + root[1..]
                : root;
            var segments = fullPath[root.Length..].Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            // Past the first segment that does not exist, the rest pass through.
            var exists = true;
            foreach (var segment in segments)
            {
                var actual = exists ? new DirectoryInfo(current).EnumerateFileSystemInfos(segment).FirstOrDefault() : null;
                exists = actual is not null;
                current = Path.Combine(current, actual?.Name ?? segment);
            }

            return current;
        }
        catch
        {
            return fullPath;
        }
    }

    private static T WithRepo<T>(string repositoryRoot, Func<Repository, T> body, T fallback)
    {
        try
        {
            var repoDir = DiscoverRepositoryRoot(repositoryRoot);
            if (repoDir is null)
            {
                return fallback;
            }

            using var repo = new Repository(repoDir);
            return body(repo);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool InScope(GitRepositoryScope scope, string path)
    {
        var prefix = scope.ScopePrefix;
        return prefix.Length == 0
            || path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToRepoPath(string repositoryRoot, string fullPath)
    {
        var rel = Path.GetRelativePath(repositoryRoot, fullPath);
        return rel == "." ? string.Empty : NormalizeRepoPath(rel);
    }

    private static string NormalizeRepoPath(string path) => RepoPaths.Normalize(path);

    private static string NormalizeFullPath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsUnder(string root, string candidate)
    {
        var rel = Path.GetRelativePath(root, candidate);
        return rel == "." || (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel));
    }

    private static GitFileState MapChangeKind(ChangeKind kind) => kind switch
    {
        ChangeKind.Added or ChangeKind.Copied => GitFileState.Added,
        ChangeKind.Deleted => GitFileState.Deleted,
        ChangeKind.Renamed => GitFileState.Renamed,
        ChangeKind.Conflicted => GitFileState.Conflicted,
        _ => GitFileState.Modified,
    };

    private static GitFileState Map(FileStatus status)
    {
        if (status.HasFlag(FileStatus.Conflicted))
        {
            return GitFileState.Conflicted;
        }

        if (status.HasFlag(FileStatus.NewInIndex))
        {
            return GitFileState.Added;
        }

        if (status.HasFlag(FileStatus.NewInWorkdir))
        {
            return GitFileState.Untracked;
        }

        if (status.HasFlag(FileStatus.ModifiedInWorkdir) || status.HasFlag(FileStatus.ModifiedInIndex))
        {
            return GitFileState.Modified;
        }

        if (status.HasFlag(FileStatus.DeletedFromWorkdir) || status.HasFlag(FileStatus.DeletedFromIndex))
        {
            return GitFileState.Deleted;
        }

        if (status.HasFlag(FileStatus.RenamedInWorkdir) || status.HasFlag(FileStatus.RenamedInIndex))
        {
            return GitFileState.Renamed;
        }

        return GitFileState.None;
    }
}
