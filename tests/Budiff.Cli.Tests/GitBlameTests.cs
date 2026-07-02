using System;
using System.IO;
using Budiff.Git;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Cli.Tests;

public sealed class GitBlameTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("budiff-blame-").FullName;

    [Fact]
    public void LoadBlame_MapsLinesToTheirLastCommit()
    {
        Repository.Init(_root);
        using var repo = new Repository(_root);
        CommitFile(repo, "a.txt", "one\ntwo\nthree\n", "Ada Lovelace", "add three lines", daysAgo: 10);
        CommitFile(repo, "a.txt", "one\ntwo CHANGED\nthree\n", "Grace Hopper", "change line two", daysAgo: 2);

        var scope = GitRepositoryReader.Open(_root, gitRoot: true);
        var blame = GitRepositoryReader.LoadBlame(scope, "a.txt", "HEAD");

        Assert.Equal(3, blame.Count);
        Assert.Equal("Ada Lovelace", blame[0]!.Author);
        Assert.Equal("Grace Hopper", blame[1]!.Author);
        Assert.Equal("test@example.com", blame[1]!.Email);
        Assert.Equal("change line two", blame[1]!.Message);
        Assert.Equal("Ada Lovelace", blame[2]!.Author);
        Assert.Equal(7, blame[1]!.ShortSha.Length);
    }

    [Fact]
    public void BlameFile_WorkingTree_MarksUncommittedLines()
    {
        Repository.Init(_root);
        using var repo = new Repository(_root);
        CommitFile(repo, "a.txt", "one\ntwo\n", "Ada Lovelace", "add", daysAgo: 3);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one\ntwo CHANGED\n");

        var blame = GitHistory.BlameFile(_root, "a.txt", rev: null);

        Assert.True(blame.Count >= 2);
        Assert.Equal("Ada Lovelace", blame[0]!.Author);
        Assert.Equal("0000000", blame[1]!.ShortSha);
        Assert.Equal("Not committed yet", blame[1]!.Author);

        // At HEAD both lines belong to the commit.
        var atHead = GitHistory.BlameFile(_root, "a.txt", rev: "HEAD");
        Assert.Equal("Ada Lovelace", atHead[1]!.Author);
        Assert.Equal("add", atHead[1]!.Message);
    }

    [Fact]
    public void LoadBlame_EmptyForNonCommitBase()
    {
        Repository.Init(_root);
        using var repo = new Repository(_root);
        CommitFile(repo, "a.txt", "one\n", "Ada Lovelace", "add", daysAgo: 1);

        var scope = GitRepositoryReader.Open(_root, gitRoot: true);

        // The empty tree (a whole-history window's base) is not a commit.
        Assert.Empty(GitRepositoryReader.LoadBlame(scope, "a.txt", GitHistory.EmptyTreeSha));
        Assert.Empty(GitRepositoryReader.LoadBlame(scope, "missing.txt", "HEAD"));
    }

    private void CommitFile(Repository repo, string name, string content, string author, string message, int daysAgo)
    {
        File.WriteAllText(Path.Combine(_root, name), content);
        Commands.Stage(repo, name);
        var signature = new Signature(author, "test@example.com", DateTimeOffset.UtcNow.AddDays(-daysAgo));
        repo.Commit(message, signature, signature);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort - temp cleanup only
        }
    }
}
