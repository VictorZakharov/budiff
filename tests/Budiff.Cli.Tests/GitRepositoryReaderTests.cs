using Budiff.Git;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Cli.Tests;

[Collection("GitRepositoryReader environment")]
public sealed class GitRepositoryReaderTests
{
    [Fact]
    public void ListChanges_RespectsFolderScopeAndGitRootScope()
    {
        using var repo = TempRepo.Create();
        repo.Write("root.txt", "root\n");
        repo.Write("sub/a.txt", "a\n");
        repo.Commit("initial", "Alice");

        repo.Write("root.txt", "root changed\n");
        repo.Write("sub/a.txt", "a changed\n");

        var folderScope = GitRepositoryReader.Open(Path.Combine(repo.Root, "sub"), gitRoot: false);
        var folderChanges = GitRepositoryReader.ListChanges(folderScope, DiffTarget.WorkingTree);
        Assert.Collection(folderChanges.Files, file => Assert.Equal("sub/a.txt", file.Path));

        var rootScope = GitRepositoryReader.Open(Path.Combine(repo.Root, "sub"), gitRoot: true);
        var rootChanges = GitRepositoryReader.ListChanges(rootScope, DiffTarget.WorkingTree);
        Assert.Contains(rootChanges.Files, file => file.Path == "root.txt");
        Assert.Contains(rootChanges.Files, file => file.Path == "sub/a.txt");
    }

    // The markdown preview's pictures: a file's raw bytes as each revision
    // has it, size-capped, and whether a path is a file there at all.
    [Fact]
    public void ReadFileAtRevision_ReturnsThatRevisionsBytes()
    {
        using var repo = TempRepo.Create();
        repo.Write("docs/shot.png", "v1");
        var first = repo.Commit("initial", "Alice");
        repo.Write("docs/shot.png", "v2-longer");
        var second = repo.Commit("update", "Bob");

        var scope = GitRepositoryReader.Open(repo.Root, gitRoot: true);
        Assert.Equal("v1"u8.ToArray(), GitRepositoryReader.ReadFileAtRevision(scope, "docs/shot.png", first, 1024));
        Assert.Equal("v2-longer"u8.ToArray(), GitRepositoryReader.ReadFileAtRevision(scope, "docs/shot.png", second, 1024));
        Assert.Null(GitRepositoryReader.ReadFileAtRevision(scope, "docs/shot.png", second, maxBytes: 4));
        Assert.Null(GitRepositoryReader.ReadFileAtRevision(scope, "docs/missing.png", second, 1024));
        Assert.True(GitRepositoryReader.HasFileAtRevision(scope, "docs/shot.png", first));
        Assert.False(GitRepositoryReader.HasFileAtRevision(scope, "docs", first));
    }

    // cmd.exe keeps a typed `g:\...` cwd verbatim; git matches safe.directory
    // against the path it is given, so Open must hand it the on-disk spelling.
    [Fact]
    public void Open_UsesOnDiskSpellingForMisCasedStartPath()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Path casing only matters on Windows.");
        using var repo = TempRepo.Create();
        repo.Write("Sub/a.txt", "a\n");
        repo.Commit("initial", "Alice");

        var canonicalRoot = GitRepositoryReader.CanonicalPath(repo.Root);
        var misCased = Path.Combine(canonicalRoot, "Sub").ToLowerInvariant();
        var scope = GitRepositoryReader.Open(misCased, gitRoot: false);

        Assert.Equal(canonicalRoot, scope.RepositoryRoot);
        Assert.Equal(Path.Combine(canonicalRoot, "Sub"), scope.ScopeRoot);
        Assert.Equal("Sub", scope.ScopePrefix);
        Assert.True(char.IsUpper(scope.RepositoryRoot[0]));
    }

    [Fact]
    public void CanonicalPath_PassesUnknownSegmentsThrough()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Path casing only matters on Windows.");
        using var repo = TempRepo.Create();
        var canonicalRoot = GitRepositoryReader.CanonicalPath(repo.Root);

        Assert.Equal(
            Path.Combine(canonicalRoot, "missing", "deeper"),
            GitRepositoryReader.CanonicalPath(Path.Combine(canonicalRoot.ToLowerInvariant(), "missing", "deeper")));
    }

    [Fact]
    public void ListChanges_MapsWorkingTreeStatesAndHistoryCounts()
    {
        using var _ = HistoryLimit(null);
        using var repo = TempRepo.Create();
        repo.Write("tracked.txt", "one\n");
        repo.Write("deleted.txt", "gone\n");
        repo.Commit("initial", "Alice");

        repo.Write("tracked.txt", "one\ntwo\n");
        File.Delete(Path.Combine(repo.Root, "deleted.txt"));
        repo.Write("new.txt", "new\n");

        var scope = GitRepositoryReader.Open(repo.Root, gitRoot: true);
        var changes = GitRepositoryReader.ListChanges(scope, DiffTarget.WorkingTree);

        var tracked = Assert.Single(changes.Files, file => file.Path == "tracked.txt");
        Assert.Equal(GitFileState.Modified, tracked.State);
        Assert.Equal(1, tracked.AuthorCount);
        Assert.Equal(1, tracked.CommitCount);
        Assert.True(tracked.Churn.Total > 0);

        Assert.Equal(GitFileState.Deleted, Assert.Single(changes.Files, file => file.Path == "deleted.txt").State);
        Assert.Equal(GitFileState.Untracked, Assert.Single(changes.Files, file => file.Path == "new.txt").State);
    }

    [Fact]
    public void ListChanges_SkipsHistoryCountsWhenLimitIsZero()
    {
        using var _ = HistoryLimit("0");
        using var repo = TempRepo.Create();
        repo.Write("tracked.txt", "one\n");
        repo.Commit("initial", "Alice");

        repo.Write("tracked.txt", "one\ntwo\n");

        var scope = GitRepositoryReader.Open(repo.Root, gitRoot: true);
        var changes = GitRepositoryReader.ListChanges(scope, DiffTarget.WorkingTree);

        var tracked = Assert.Single(changes.Files, file => file.Path == "tracked.txt");
        Assert.Equal(0, tracked.AuthorCount);
        Assert.Equal(0, tracked.CommitCount);
        Assert.True(tracked.Churn.Total > 0);
    }

    [Fact]
    public void ListChanges_CapsHistoryCountsByRecentCommitWindow()
    {
        using var repo = TempRepo.Create();
        repo.Write("tracked.txt", "one\n");
        repo.Commit("initial", "Alice");
        repo.Write("other.txt", "other\n");
        repo.Commit("unrelated", "Bob");

        repo.Write("tracked.txt", "one\ntwo\n");

        using (HistoryLimit("1"))
        {
            var tracked = LoadTrackedChange(repo);
            Assert.Equal(0, tracked.AuthorCount);
            Assert.Equal(0, tracked.CommitCount);
        }

        using (HistoryLimit("2"))
        {
            var tracked = LoadTrackedChange(repo);
            Assert.Equal(1, tracked.AuthorCount);
            Assert.Equal(1, tracked.CommitCount);
        }
    }

    [Fact]
    public void ListChanges_DetectsRenamesForTreeTargets()
    {
        using var repo = TempRepo.Create();
        repo.Write("old.txt", "hello\n");
        var first = repo.Commit("initial", "Alice");

        File.Move(Path.Combine(repo.Root, "old.txt"), Path.Combine(repo.Root, "new.txt"));
        repo.Write("new.txt", "hello\nworld\n");
        var second = repo.Commit("rename", "Bob");

        var scope = GitRepositoryReader.Open(repo.Root, gitRoot: true);
        var target = new DiffTarget(first, second, HeadIsWorkingTree: false);
        var changes = GitRepositoryReader.ListChanges(scope, target);

        var file = Assert.Single(changes.Files);
        Assert.Equal("new.txt", file.Path);
        Assert.Equal("old.txt", file.OldPath);
        Assert.Equal(GitFileState.Renamed, file.State);
    }

    // A rename staged in the working tree keeps its old path, so the diff
    // compares the file with its old self rather than showing it all added.
    [Fact]
    public void ListChanges_WorkingTreeRename_ComparesWithTheOldPath()
    {
        using var repo = TempRepo.Create();
        repo.Write("old.txt", "one\ntwo\nthree\nfour\n");
        repo.Commit("initial", "Alice");
        File.Move(Path.Combine(repo.Root, "old.txt"), Path.Combine(repo.Root, "new.txt"));
        repo.Stage("old.txt", "new.txt");

        var scope = GitRepositoryReader.Open(repo.Root, gitRoot: true);
        var file = Assert.Single(GitRepositoryReader.ListChanges(scope, DiffTarget.WorkingTree).Files);
        Assert.Equal(("new.txt", "old.txt", GitFileState.Renamed), (file.Path, file.OldPath, file.State));
        Assert.Equal(0, file.Churn.Total);
    }

    // Binary is told from the start of a file, also past the inline-diff
    // limit (only that start is read).
    [Fact]
    public void ListChanges_SpotsBinaryFilesBeyondTheInlineLimit()
    {
        using var repo = TempRepo.Create();
        var bytes = new byte[(int)DiffLoader.MaxInlineDiffBytes + 4096];
        bytes[10] = 0;
        Array.Fill(bytes, (byte)'x', 11, bytes.Length - 11);
        File.WriteAllBytes(Path.Combine(repo.Root, "blob.bin"), bytes);
        var first = repo.Commit("initial", "Alice");
        bytes[^1] = (byte)'y';
        File.WriteAllBytes(Path.Combine(repo.Root, "blob.bin"), bytes);
        var second = repo.Commit("change", "Alice");

        var scope = GitRepositoryReader.Open(repo.Root, gitRoot: true);
        var file = Assert.Single(GitRepositoryReader.ListChanges(scope, new DiffTarget(first, second, HeadIsWorkingTree: false)).Files);
        Assert.True(file.IsBinary);
        Assert.Contains("Binary", GitRepositoryReader.LoadDiff(scope, file, new DiffTarget(first, second, HeadIsWorkingTree: false)).Placeholder, StringComparison.OrdinalIgnoreCase);
    }

    // Branches for the pickers: local first (the current one marked), then
    // remote ones, never the symbolic origin/HEAD; then recent commits.
    [Fact]
    public void ReferenceOptions_ListBranchesThenRecentCommits()
    {
        using var repo = TempRepo.Create();
        repo.Write("a.txt", "a\n");
        var tip = repo.Commit("First commit", "Alice");
        repo.Repo.CreateBranch("zeta");
        repo.Repo.Refs.Add("refs/remotes/origin/main", tip);
        repo.Repo.Refs.Add("refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        var current = repo.Repo.Head.FriendlyName;

        var branches = GitRepositoryReader.GetBranchOptions(repo.Root);
        Assert.Equal(new[] { "HEAD", current, "zeta", "origin/main" }, branches.Select(o => o.Id));
        Assert.Equal(current + " (current)", branches[1].Display);

        var all = GitRepositoryReader.GetReferenceOptions(repo.Root);
        Assert.Equal("WORKTREE", all[0].Id);
        Assert.Equal($"{tip[..7]} First commit", all[^1].Display);
        Assert.Equal(current, GitRepositoryReader.Open(repo.Root, gitRoot: true).Branch);
    }

    // When git cannot read the folder, Open says why in git's own words.
    [Fact]
    public void Open_PassesOnGitsReason()
    {
        var root = Path.Combine(Path.GetTempPath(), "budiff-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => GitRepositoryReader.Open(root, gitRoot: true));
            Assert.Contains("not a git repository", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParseStatus_ReadsPorcelainV2()
    {
        var output = string.Join('\0',
            "1 .M N... 100644 100644 100644 aaa bbb src/a b.cs",
            "1 A. N... 000000 100644 100644 000 ccc added.cs",
            "1 .A N... 000000 000000 100644 000 000 intent.cs",
            "1 D. N... 100644 000000 000000 ddd 000 gone.cs",
            "2 R. N... 100644 100644 100644 eee eee R100 moved.cs",
            "was.cs",
            "u UU N... 100644 100644 100644 100644 f1 f2 f3 both.cs",
            "1 .M S.M. 160000 160000 160000 g1 g2 vendor/lib",
            "? new file.txt",
            "");

        Assert.Equal(
            new (GitFileState, string, string?)[]
            {
                (GitFileState.Modified, "src/a b.cs", null),
                (GitFileState.Added, "added.cs", null),
                (GitFileState.Added, "intent.cs", null),
                (GitFileState.Deleted, "gone.cs", null),
                (GitFileState.Renamed, "moved.cs", "was.cs"),
                (GitFileState.Conflicted, "both.cs", null),
                (GitFileState.Untracked, "new file.txt", null),
            },
            GitRepositoryReader.ParseStatus(output));
    }

    [Fact]
    public void ParseNameStatus_ReadsRenamesAndCopies()
    {
        var output = string.Join('\0', "M", "a.cs", "R087", "old.cs", "new.cs", "C100", "src.cs", "copy.cs", "D", "gone.cs", "A", "added.cs", "");

        Assert.Equal(
            new (GitFileState, string, string?)[]
            {
                (GitFileState.Modified, "a.cs", null),
                (GitFileState.Renamed, "new.cs", "old.cs"),
                (GitFileState.Added, "copy.cs", null),
                (GitFileState.Deleted, "gone.cs", null),
                (GitFileState.Added, "added.cs", null),
            },
            GitRepositoryReader.ParseNameStatus(output));
    }

    private static ChangedFile LoadTrackedChange(TempRepo repo)
    {
        var scope = GitRepositoryReader.Open(repo.Root, gitRoot: true);
        var changes = GitRepositoryReader.ListChanges(scope, DiffTarget.WorkingTree);
        return Assert.Single(changes.Files, file => file.Path == "tracked.txt");
    }

    private static IDisposable HistoryLimit(string? value) => new HistoryLimitScope(value);

    private sealed class HistoryLimitScope : IDisposable
    {
        private readonly string? _previous;

        public HistoryLimitScope(string? value)
        {
            _previous = Environment.GetEnvironmentVariable(GitRepositoryReader.HistoryMaxCommitsEnvironmentVariable);
            Environment.SetEnvironmentVariable(GitRepositoryReader.HistoryMaxCommitsEnvironmentVariable, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(GitRepositoryReader.HistoryMaxCommitsEnvironmentVariable, _previous);
        }
    }

    private sealed class TempRepo : IDisposable
    {
        private readonly Repository _repo;

        private TempRepo(string root, Repository repo)
        {
            Root = root;
            _repo = repo;
        }

        public string Root { get; }

        public Repository Repo => _repo;

        public static TempRepo Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "budiff-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Repository.Init(root);
            var repo = new Repository(root);
            repo.Config.Set("core.autocrlf", false);
            return new TempRepo(root, repo);
        }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public string Commit(string message, string author)
        {
            Commands.Stage(_repo, "*");
            var removed = _repo.RetrieveStatus().Removed.Select(entry => entry.FilePath).ToArray();
            if (removed.Length > 0)
            {
                Commands.Stage(_repo, removed);
            }

            var sig = new Signature(author, author.ToLowerInvariant() + "@example.test", DateTimeOffset.UtcNow);
            return _repo.Commit(message, sig, sig).Sha;
        }

        public void Stage(params string[] paths) => Commands.Stage(_repo, paths);

        public void Dispose()
        {
            _repo.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temp repos locked by native handles.
            }
        }
    }
}

[CollectionDefinition("GitRepositoryReader environment", DisableParallelization = true)]
public sealed class GitRepositoryReaderEnvironmentCollection
{
}
