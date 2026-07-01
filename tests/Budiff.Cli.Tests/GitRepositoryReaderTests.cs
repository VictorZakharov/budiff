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
