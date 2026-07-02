using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Budiff;
using LibGit2Sharp;

namespace Budiff.Git;

public static class GitRepositoryReader
{
    internal const int DefaultHistoryCommitLimit = 1_000;
    internal const string HistoryMaxCommitsEnvironmentVariable = "BUDIFF_HISTORY_MAX_COMMITS";

    public static GitRepositoryScope Open(string startPath, bool gitRoot)
    {
        var start = Path.GetFullPath(string.IsNullOrWhiteSpace(startPath) ? Environment.CurrentDirectory : startPath);
        if (File.Exists(start))
        {
            start = Path.GetDirectoryName(start) ?? start;
        }

        var gitDir = Repository.Discover(start);
        if (string.IsNullOrEmpty(gitDir))
        {
            throw new InvalidOperationException("This folder is not inside a git repository.");
        }

        using var repo = new Repository(gitDir);
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

            return (IReadOnlyList<GitReferenceOption>)options;
        }, Array.Empty<GitReferenceOption>());
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

        return blob.IsBinary
            ? new BlobInfo(string.Empty, blob.Size, IsBinary: true)
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

    private static T WithRepo<T>(string repositoryRoot, Func<Repository, T> body, T fallback)
    {
        try
        {
            var gitDir = Repository.Discover(repositoryRoot);
            if (string.IsNullOrEmpty(gitDir))
            {
                return fallback;
            }

            using var repo = new Repository(gitDir);
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

    private static string NormalizeRepoPath(string path) => path.Replace('\\', '/').TrimStart('/');

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
