using System;
using System.IO;
using Budiff.Git;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Cli.Tests;

public sealed class GitWindowBaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("budiff-window-").FullName;

    [Fact]
    public void FindWindowBaseCommit_PicksLastCommitOlderThanWindow_OrEmptyTree()
    {
        Repository.Init(_root);
        using var repo = new Repository(_root);
        var baseline = CommitFile(repo, "a.txt", "one", daysAgo: 45);
        CommitFile(repo, "a.txt", "two", daysAgo: 2);

        // The last commit OLDER than the window is the churn base...
        Assert.Equal(baseline, GitHistory.FindWindowBaseCommit(_root, 30));

        // ...and the empty tree when the whole history sits inside the window.
        Assert.Equal(GitHistory.EmptyTreeSha, GitHistory.FindWindowBaseCommit(_root, 60));
    }

    private string CommitFile(Repository repo, string name, string content, int daysAgo)
    {
        File.WriteAllText(Path.Combine(_root, name), content);
        Commands.Stage(repo, name);
        var when = new DateTimeOffset(DateTime.UtcNow.AddDays(-daysAgo));
        var signature = new Signature("Test", "test@example.com", when);
        return repo.Commit($"edit {name}", signature, signature).Sha;
    }

    public void Dispose()
    {
        try
        {
            // Clear read-only .git objects so the temp folder actually deletes.
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
