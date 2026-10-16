using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Budiff;
using Budiff.CodeAnalysis;

namespace Budiff.Git;

// Reads a repository through the `git` command: status and tree diffs in one
// process each, file contents through GitObjects' long-running cat-file.
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

        var root = NormalizeFullPath(repoDir);
        var branch = CurrentBranch(root);
        var scopeRoot = gitRoot ? root : NormalizeFullPath(start);
        if (!IsUnder(root, scopeRoot))
        {
            scopeRoot = root;
        }

        var prefix = ToRepoPath(root, scopeRoot);
        return new GitRepositoryScope(root, scopeRoot, prefix, gitRoot || prefix.Length == 0, branch);
    }

    public static IReadOnlyList<GitReferenceOption> GetReferenceOptions(string repositoryRoot)
    {
        var options = new List<GitReferenceOption>
        {
            new("WORKTREE", "Working tree", IsWorkingTree: true),
            new("HEAD", "HEAD"),
        };

        AddBranchOptions(repositoryRoot, options);
        if (GitCli.TryRun(repositoryRoot, out var log, "log", "-n", "50", "--no-color", "--no-show-signature", "--format=%H%x1f%s", "HEAD"))
        {
            foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var sha = line.Split('\x1f', 2)[0];
                var subject = line.Length > sha.Length ? line[(sha.Length + 1)..] : string.Empty;
                options.Add(new GitReferenceOption(sha, $"{sha[..Math.Min(7, sha.Length)]} {subject}"));
            }
        }

        return options;
    }

    // Branches only (plus HEAD) - the diff base picker. Head is always the working
    // tree or HEAD, so no commit or working-tree entries here.
    public static IReadOnlyList<GitReferenceOption> GetBranchOptions(string repositoryRoot)
    {
        var options = new List<GitReferenceOption> { new("HEAD", "HEAD") };
        AddBranchOptions(repositoryRoot, options);
        return options;
    }

    // Local branches first (current one labeled), then remotes, skipping the
    // symbolic */HEAD entries and anything already in the list.
    private static void AddBranchOptions(string repositoryRoot, List<GitReferenceOption> options)
    {
        if (!GitCli.TryRun(repositoryRoot, out var refs, "for-each-ref", "--format=%(HEAD)%(refname)", "refs/heads", "refs/remotes"))
        {
            return;
        }

        var branches = new List<(string Name, bool IsRemote, bool IsCurrent)>();
        foreach (var line in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var current = line[0] == '*';
            var refName = line[1..].Trim();
            var (name, isRemote) = refName.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? (refName["refs/heads/".Length..], false)
                : (refName["refs/remotes/".Length..], true);
            if (!name.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                branches.Add((name, isRemote, current));
            }
        }

        foreach (var branch in branches.OrderBy(b => b.IsRemote).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (options.All(o => !string.Equals(o.Id, branch.Name, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new GitReferenceOption(branch.Name, branch.IsCurrent ? branch.Name + " (current)" : branch.Name));
            }
        }
    }

    public static ChangeSet ListChanges(GitRepositoryScope scope, DiffTarget target)
    {
        return WithGit(scope.RepositoryRoot, git =>
        {
            // The history log does not depend on the changes: it runs alongside.
            var log = Task.Run(() => ReadHistoryLog(scope.RepositoryRoot));
            var files = target.HeadIsWorkingTree
                ? ListWorkingTreeChanges(git, scope, target)
                : ListTreeChanges(git, scope, target);

            var history = CountHistory(log.GetAwaiter().GetResult(), files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
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
        var commit = WithGit(scope.RepositoryRoot, git => git.ResolveCommit(baseRef), null);
        return commit is null
            ? Array.Empty<BlameLineInfo?>()
            : GitHistory.BlameFile(scope.RepositoryRoot, path.Replace('\\', '/'), commit);
    }

    public static DiffResult LoadDiff(GitRepositoryScope scope, ChangedFile file, DiffTarget target)
    {
        return WithGit(scope.RepositoryRoot, git =>
        {
            var oldBlob = git.ReadBlob(git.ResolveCommit(target.BaseRef), RepoPath(file.OldPath ?? file.Path));
            var newBlob = target.HeadIsWorkingTree
                ? file.State == GitFileState.Deleted ? BlobInfo.Empty : DiffLoader.ReadWorkingFile(file.FullPath)
                : git.ReadBlob(git.ResolveCommit(target.HeadRef ?? "HEAD"), RepoPath(file.Path));
            return DiffLoader.Build(oldBlob, newBlob);
        }, DiffResult.Message("Could not load this diff."));
    }

    // A repository file's raw bytes as it is at a revision (for the markdown
    // preview's pictures); null when absent there or bigger than maxBytes.
    public static byte[]? ReadFileAtRevision(GitRepositoryScope scope, string path, string revision, long maxBytes)
    {
        return WithGit<byte[]?>(scope.RepositoryRoot, git =>
        {
            if (git.ResolveCommit(revision) is not { } commit
                || git.Info($"{commit}:{RepoPath(path)}") is not { Type: "blob" } info
                || info.Size > maxBytes)
            {
                return null;
            }

            return git.Read(info.Sha)?.Bytes;
        }, null);
    }

    // Whether a repository path is a file at a revision.
    public static bool HasFileAtRevision(GitRepositoryScope scope, string path, string revision)
        => WithGit(
            scope.RepositoryRoot,
            git => git.ResolveCommit(revision) is { } commit && git.Info($"{commit}:{RepoPath(path)}") is { Type: "blob" },
            false);

    // Line counts for root-relative paths as they are at a revision, not on
    // disk. A path absent there (deleted by that revision) counts as an empty
    // file, so it still lists - with its removals - in the bird's-eye.
    public static IReadOnlyList<FileStat> CountAtRevision(string root, string revision, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        // Not WithGit: a missing revision must fail loudly, not read as "no files".
        var scope = Open(root, gitRoot: false);
        using var git = new GitObjects(scope.RepositoryRoot);
        var commit = git.ResolveCommit(revision)
            ?? throw new InvalidOperationException($"Revision {revision} is not available locally.");
        var files = new List<FileStat>(paths.Count);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var repoPath = scope.ScopePrefix.Length == 0 ? path : scope.ScopePrefix + "/" + path;
            if (git.Info($"{commit}:{repoPath}") is not { Type: "blob" } info)
            {
                files.Add(new FileStat { RelativePath = path, Extension = Path.GetExtension(path).ToLowerInvariant() });
                continue;
            }

            if (!CodeScanner.IsCountable(info.Size))
            {
                files.Add(CodeScanner.Oversized(path));
                continue;
            }

            files.Add(CodeScanner.CountBytes(path, git.Read(info.Sha)?.Bytes ?? Array.Empty<byte>()));
        }

        return files;
    }

    // `git status`, limited to the folder: what changed against HEAD, the
    // index included, untracked files listed one by one.
    private static List<ChangedFile> ListWorkingTreeChanges(GitObjects git, GitRepositoryScope scope, DiffTarget target)
    {
        var args = new List<string> { "status", "--porcelain=v2", "-z", "--untracked-files=all" };
        if (scope.ScopePrefix.Length > 0)
        {
            args.Add("--");
            args.Add(":(top,literal,icase)" + scope.ScopePrefix);
        }

        if (!GitCli.TryRun(scope.RepositoryRoot, out var output, args.ToArray()))
        {
            throw new InvalidOperationException("git status failed.");
        }

        var entries = ParseStatus(output).Where(e => InScope(scope, e.Path)).ToList();
        var oldBlobs = git.ReadBlobs(git.ResolveCommit(target.BaseRef), entries.Select(e => e.OldPath ?? e.Path).ToList());
        var files = new List<ChangedFile>();
        for (var i = 0; i < entries.Count; i++)
        {
            var (state, path, oldPath) = entries[i];
            var fullPath = Path.Combine(scope.RepositoryRoot, path.Replace('/', Path.DirectorySeparatorChar));
            var oldBlob = oldBlobs[i];
            var newBlob = state == GitFileState.Deleted ? BlobInfo.Empty : DiffLoader.ReadWorkingFile(fullPath);
            var diff = DiffLoader.Build(oldBlob, newBlob);
            var churn = diff.Rows is { } rows ? DiffMetrics.Churn(rows) : new DiffChurn(0, 0);
            files.Add(new ChangedFile(path, oldPath, fullPath, state, churn, 0, 0, oldBlob.IsBinary || newBlob.IsBinary));
        }

        return files;
    }

    // `git status --porcelain=v2 -z` entries: "1 XY ... path", "2 XY ...
    // path\0origPath" (renamed in the index), "u XY ... path" (conflict),
    // "? path" (untracked). Submodules are skipped - no file to diff.
    internal static IEnumerable<(GitFileState State, string Path, string? OldPath)> ParseStatus(string output)
    {
        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 2)
            {
                continue;
            }

            switch (record[0])
            {
                case '?':
                    yield return (GitFileState.Untracked, RepoPaths.Normalize(record[2..]), null);
                    break;
                case 'u':
                {
                    var fields = record.Split(' ', 11);
                    if (fields.Length == 11)
                    {
                        yield return (GitFileState.Conflicted, RepoPaths.Normalize(fields[10]), null);
                    }

                    break;
                }

                case '1':
                case '2':
                {
                    var renamed = record[0] == '2';
                    var fields = record.Split(' ', renamed ? 10 : 9);
                    var oldPath = renamed && i + 1 < records.Length ? records[++i] : null;
                    if (fields.Length != (renamed ? 10 : 9) || fields[2][0] == 'S')
                    {
                        break;
                    }

                    var state = MapStatus(fields[1]);
                    if (state != GitFileState.None)
                    {
                        yield return (state, RepoPaths.Normalize(fields[^1]), oldPath is null ? null : RepoPaths.Normalize(oldPath));
                    }

                    break;
                }
            }
        }
    }

    // XY: the index's change, then the working tree's ('.' = none; a
    // working-tree 'A' is `git add -N`). An add wins, then a modification, a
    // deletion, a rename.
    private static GitFileState MapStatus(string xy)
    {
        var (index, work) = (xy[0], xy[1]);
        return index is 'A' or 'C' || work == 'A' ? GitFileState.Added
            : index is 'M' or 'T' || work is 'M' or 'T' ? GitFileState.Modified
            : index == 'D' || work == 'D' ? GitFileState.Deleted
            : index == 'R' ? GitFileState.Renamed
            : GitFileState.None;
    }

    // Two commits' trees, renames detected. Without a base (the empty tree,
    // an unknown ref) every file of the head counts as added.
    private static List<ChangedFile> ListTreeChanges(GitObjects git, GitRepositoryScope scope, DiffTarget target)
    {
        var files = new List<ChangedFile>();
        var oldCommit = git.ResolveCommit(target.BaseRef);
        var newCommit = git.ResolveCommit(target.HeadRef ?? "HEAD");
        if (newCommit is null)
        {
            return files;
        }

        IEnumerable<(GitFileState State, string Path, string? OldPath)> changes;
        if (oldCommit is null)
        {
            if (!GitCli.TryRun(scope.RepositoryRoot, out var tree, "ls-tree", "-r", "-z", "--name-only", newCommit))
            {
                throw new InvalidOperationException("git ls-tree failed.");
            }

            changes = tree.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(p => (GitFileState.Added, RepoPaths.Normalize(p), (string?)null));
        }
        else
        {
            if (!GitCli.TryRun(scope.RepositoryRoot, out var diff, "diff", "--name-status", "-z", "-M", "--no-color", "--no-ext-diff", oldCommit, newCommit))
            {
                throw new InvalidOperationException("git diff failed.");
            }

            changes = ParseNameStatus(diff);
        }

        var inScope = changes.Where(c => InScope(scope, c.Path)).ToList();
        var oldBlobs = git.ReadBlobs(oldCommit, inScope.Select(c => c.OldPath ?? c.Path).ToList());
        var newBlobs = git.ReadBlobs(newCommit, inScope.Select(c => c.Path).ToList());
        for (var i = 0; i < inScope.Count; i++)
        {
            var (state, path, oldPath) = inScope[i];
            var fullPath = Path.Combine(scope.RepositoryRoot, path.Replace('/', Path.DirectorySeparatorChar));
            var oldBlob = oldBlobs[i];
            var newBlob = newBlobs[i];
            var diff = DiffLoader.Build(oldBlob, newBlob);
            var churn = diff.Rows is { } rows ? DiffMetrics.Churn(rows) : new DiffChurn(0, 0);
            files.Add(new ChangedFile(path, oldPath, fullPath, state, churn, 0, 0, oldBlob.IsBinary || newBlob.IsBinary));
        }

        return files;
    }

    // `git diff --name-status -z`: "M\0path\0", "R087\0old\0new\0".
    internal static IEnumerable<(GitFileState State, string Path, string? OldPath)> ParseNameStatus(string output)
    {
        var fields = output.Split('\0');
        for (var i = 0; i + 1 < fields.Length; i++)
        {
            var status = fields[i];
            if (status.Length == 0)
            {
                continue;
            }

            if (status[0] is 'R' or 'C' && i + 2 < fields.Length)
            {
                var oldPath = RepoPaths.Normalize(fields[i + 1]);
                var newPath = RepoPaths.Normalize(fields[i + 2]);
                i += 2;
                yield return status[0] == 'R' ? (GitFileState.Renamed, newPath, oldPath) : (GitFileState.Added, newPath, null);
                continue;
            }

            var path = RepoPaths.Normalize(fields[++i]);
            yield return (status[0] switch
            {
                'A' => GitFileState.Added,
                'D' => GitFileState.Deleted,
                'U' => GitFileState.Conflicted,
                _ => GitFileState.Modified,
            }, path, null);
        }
    }

    // The last N commits (BUDIFF_HISTORY_MAX_COMMITS, default 1000), each
    // with its author and the paths it changed; null when not wanted or not
    // readable.
    private static string? ReadHistoryLog(string root)
    {
        var maxCommits = HistoryCommitLimit();
        return maxCommits > 0
            && GitCli.TryRun(root, out var log, "log", "-n", maxCommits.ToString(System.Globalization.CultureInfo.InvariantCulture), "--no-color", "--no-show-signature", "--no-renames", "--name-only", "-z", "--format=%x1e%aN", "HEAD")
            ? log
            : null;
    }

    // How many of those commits touched each path, and by how many authors.
    // Merges count through the commits they bring in, not again themselves.
    private static Dictionary<string, PathHistory> CountHistory(string? log, IReadOnlyCollection<string> paths)
    {
        var result = paths
            .Select(RepoPaths.Normalize)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p, _ => new PathHistory(), StringComparer.OrdinalIgnoreCase);
        if (result.Count == 0 || log is null)
        {
            return result;
        }

        // Per commit: \x1e author \0, then (unless a merge) \n and paths, each \0-ended.
        var commitIndex = -1;
        foreach (var commit in log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            commitIndex++;
            var fields = commit.Split('\0');
            var author = fields[0];
            foreach (var field in fields.Skip(1))
            {
                var path = RepoPaths.Normalize(field.TrimStart('\n'));
                if (path.Length > 0 && result.TryGetValue(path, out var history))
                {
                    history.Commits.Add(commitIndex);
                    if (author.Length > 0)
                    {
                        history.Authors.Add(author);
                    }
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

    // The checked-out branch; "(no branch)" when HEAD is detached. Also the
    // check that git runs here at all - its own message says why not (not a
    // repository, dubious ownership, git not installed).
    private static string? CurrentBranch(string root)
    {
        var code = GitCli.Run(root, out var stdout, out var stderr, "symbolic-ref", "-q", "--short", "HEAD");
        return code switch
        {
            0 => stdout.Trim(),
            1 => "(no branch)",
            _ => throw new InvalidOperationException(GitFailure(stderr)),
        };
    }

    private static string GitFailure(string stderr)
    {
        var reason = string.Join(' ', stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
        return reason.Length > 0 ? reason : "git could not read this repository.";
    }

    // Walks up for a .git entry: a folder, or a FILE (worktree / submodule
    // pointer). Its folder is the working tree's top.
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
    // and git matches safe.directory entries against the path it is given, so
    // a repo that needs one (e.g. on a drive owned by another SID) fails its
    // ownership check unless git gets the canonical spelling. Only Windows
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

    private static T WithGit<T>(string repositoryRoot, Func<GitObjects, T> body, T fallback)
    {
        try
        {
            using var git = new GitObjects(repositoryRoot);
            return body(git);
        }
        catch
        {
            return fallback;
        }
    }

    private static string RepoPath(string path) => path.Replace('\\', '/');

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
        return rel == "." ? string.Empty : RepoPaths.Normalize(rel);
    }

    private static string NormalizeFullPath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsUnder(string root, string candidate)
    {
        var rel = Path.GetRelativePath(root, candidate);
        return rel == "." || (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel));
    }
}
