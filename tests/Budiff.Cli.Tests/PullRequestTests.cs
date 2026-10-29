using System;
using System.IO;
using System.Linq;
using Budiff.Git;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Cli.Tests;

public sealed class PullRequestTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("budiff-pr-").FullName;

    [Fact]
    public void BuildQuery_ComposesRepoAuthorStateAndText()
    {
        Assert.Equal(
            "repo:o/r is:pr author:@me sort:updated-desc",
            PullRequestSearch.BuildQuery("o/r", new PullRequestQuery("@me", PullRequestStateFilter.All, "  ")));
        Assert.Equal(
            "repo:o/r is:pr is:merged sort:updated-desc label:bug fix",
            PullRequestSearch.BuildQuery("o/r", new PullRequestQuery(null, PullRequestStateFilter.Merged, " label:bug fix ")));
        Assert.Equal(
            "repo:o/r is:pr author:ada is:closed is:unmerged sort:updated-desc",
            PullRequestSearch.BuildQuery("o/r", new PullRequestQuery("ada", PullRequestStateFilter.Closed, string.Empty)));
        Assert.Equal(
            "repo:o/r is:pr is:open sort:updated-desc",
            PullRequestSearch.BuildQuery("o/r", new PullRequestQuery(string.Empty, PullRequestStateFilter.Open, string.Empty)));
    }

    [Theory]
    [InlineData("#71", 71)]
    [InlineData(" 42 ", 42)]
    [InlineData("71 fix", null)]
    [InlineData("#", null)]
    [InlineData("0", null)]
    [InlineData("fix", null)]
    public void ParseNumber_AcceptsOnlyABarePrNumber(string text, int? expected)
        => Assert.Equal(expected, PullRequestSearch.ParseNumber(text));

    [Fact]
    public void ParseSearch_ReadsPullRequestsAndPaging_SkippingNonPrNodes()
    {
        const string json = """
            {"data":{"search":{"issueCount":23,"pageInfo":{"endCursor":"Y3Vyc29yOjEw","hasNextPage":true},"nodes":[
              {"number":71,"title":"Detect git","state":"OPEN","isDraft":true,"author":{"login":"ada"},"updatedAt":"2026-10-15T23:32:51Z",
               "baseRefName":"main","headRefName":"feature","baseRefOid":"11c0757","headRefOid":"0c42203","additions":351,"deletions":13,"changedFiles":11},
              {},
              {"number":70,"title":"Ghost","state":"MERGED","isDraft":false,"author":null,"updatedAt":"2026-07-23T00:59:29Z",
               "baseRefName":"main","headRefName":"x","baseRefOid":"a","headRefOid":"b","additions":1,"deletions":0,"changedFiles":1}
            ]}}}
            """;
        var page = PullRequestSearch.ParseSearch(json);

        Assert.Equal(23, page.TotalCount);
        Assert.True(page.HasNextPage);
        Assert.Equal("Y3Vyc29yOjEw", page.EndCursor);
        Assert.Equal(new[] { 71, 70 }, page.Items.Select(p => p.Number));
        var first = page.Items[0];
        Assert.Equal("Draft", first.StateLabel);
        Assert.Equal("ada", first.Author);
        Assert.Equal(new PullRequestCounts(351, 13, 11), first.Counts);
        Assert.Equal("ghost", page.Items[1].Author);
        Assert.Equal("Merged", page.Items[1].StateLabel);
    }

    // Lists come without counts (GitHub computes them per PR, slowly); the
    // rows on screen fetch them afterwards, an alias per PR.
    [Fact]
    public void Counts_AreAbsentFromLists_AndParsedPerPr()
    {
        var page = PullRequestSearch.ParseSingle("""{"data":{"repository":{"pullRequest":{"number":5,"title":"t","state":"OPEN"}}}}""");
        Assert.Null(Assert.Single(page.Items).Counts);

        var counts = PullRequestSearch.ParseCounts("""{"data":{"repository":{"p0":{"additions":3,"deletions":1,"changedFiles":2},"p1":null}}}""", new[] { 8, 9 });
        Assert.Equal(new PullRequestCounts(3, 1, 2), counts[8]);
        Assert.False(counts.ContainsKey(9));
    }

    [Fact]
    public void ParseSingle_ReturnsEmptyPageForAMissingNumber()
    {
        Assert.Empty(PullRequestSearch.ParseSingle("""{"data":{"repository":{"pullRequest":null}}}""").Items);
        var page = PullRequestSearch.ParseSingle("""{"data":{"repository":{"pullRequest":{"number":5,"title":"t","state":"CLOSED"}}}}""");
        Assert.Equal("Closed", Assert.Single(page.Items).StateLabel);
    }

    [Fact]
    public void ParsePeople_SortsByNameAndShowsLogin()
    {
        var people = PullRequestSearch.ParsePeople("""
            {"data":{"repository":{"mentionableUsers":{"nodes":[
              {"login":"zed","name":"Ada Lovelace"},{"login":"bob","name":null},{"login":"","name":"skip"}
            ]}}}}
            """);

        Assert.Equal(new[] { "Ada Lovelace (zed)", "bob" }, people.Select(p => p.Display));
    }

    // A card's details: the public profile email, else the newest address
    // the person committed with - never GitHub's noreply one.
    [Fact]
    public void ParsePeople_TakesEmailsAndAvatars()
    {
        var people = PullRequestSearch.ParsePeople("""
            {"data":{"repository":{
              "mentionableUsers":{"nodes":[
                {"login":"ada","name":"Ada Lovelace","email":"ada@example.com","avatarUrl":"https://avatars.example/ada"},
                {"login":"bob","name":null,"email":"","avatarUrl":"https://avatars.example/bob"},
                {"login":"cy","name":"Cy","email":""},
                {"login":"dee","name":"Dee","email":""}
              ]},
              "defaultBranchRef":{"target":{"history":{"nodes":[
                {"author":{"email":"ada@work.example","user":{"login":"ada"}}},
                {"author":{"email":"1+cy@users.noreply.github.com","user":{"login":"cy"}}},
                {"author":{"email":"bob@new.example","user":{"login":"bob"}}},
                {"author":{"email":"bob@old.example","user":{"login":"bob"}}},
                {"author":{"email":"stranger@example.com","user":null}}
              ]}}}
            }}}
            """);

        Assert.Equal(
            new[] { ("ada", "ada@example.com"), ("bob", "bob@new.example"), ("cy", null), ("dee", null) },
            people.Select(p => (p.Login, p.Email)));
        Assert.Equal("https://avatars.example/ada", people[0].AvatarUrl);
        Assert.Null(people[2].AvatarUrl);

        // No default branch (an empty repository): profile details only.
        var bare = PullRequestSearch.ParsePeople("""{"data":{"repository":{"mentionableUsers":{"nodes":[{"login":"ada","email":""}]},"defaultBranchRef":null}}}""");
        Assert.Null(Assert.Single(bare).Email);
    }

    // A big organisation's repository may refuse the mentionable users (gh
    // exits non-zero, the data still comes): the recent commit authors make
    // the list. With neither part there is no list.
    [Fact]
    public void ParsePeople_FallsBackToCommitAuthors_OnAPartialAnswer()
    {
        var partial = """
            {"data":{"repository":{
              "mentionableUsers":null,
              "defaultBranchRef":{"target":{"history":{"nodes":[
                {"author":{"name":"Grace H","email":"grace@example.com","user":{"login":"grace","name":"Grace Hopper","avatarUrl":"https://avatars.example/grace"}}},
                {"author":{"name":"Ada","email":"ada@example.com","user":{"login":"ada","name":null}}},
                {"author":{"name":"Grace H","email":"old@example.com","user":{"login":"grace"}}},
                null
              ]}}}
            }},
            "errors":[{"type":"FORBIDDEN","path":["repository","mentionableUsers"],"message":"Resource not accessible"}]}
            """;
        Assert.True(PullRequestSearch.HasData(partial));
        var people = PullRequestSearch.ParsePeople(partial);
        Assert.Equal(
            new (string, string?, string?)[] { ("ada", "Ada", "ada@example.com"), ("grace", "Grace Hopper", "grace@example.com") },
            people.Select(p => (p.Login, p.Name, p.Email)));
        Assert.Equal("https://avatars.example/grace", people[1].AvatarUrl);

        Assert.False(PullRequestSearch.HasData("""{"data":null,"errors":[{"message":"no"}]}"""));
        Assert.False(PullRequestSearch.HasData("gh: not json"));
        Assert.Throws<PullRequestSourceException>(() => PullRequestSearch.ParsePeople("""{"data":{"repository":{"mentionableUsers":null,"defaultBranchRef":null}}}"""));
    }

    [Theory]
    [InlineData("https://github.com/Owner/Repo.git", true)]
    [InlineData("git@github.com:owner/repo.git", true)]
    [InlineData("https://github.com/owner/repo/", true)]
    [InlineData("https://github.com/owner/other-repo.git", false)]
    [InlineData("https://github.com/someone/owner/repo-fork", false)]
    public void UrlMatches_RecognizesHttpsAndSshRemotes(string url, bool expected)
        => Assert.Equal(expected, PullRequestRanges.UrlMatches(url, "owner/repo"));

    [Fact]
    public void Explain_TurnsSetupFailuresIntoNextSteps()
    {
        Assert.Contains("cli.github.com", GitHubCliPullRequestSource.Explain("'gh' was not found on PATH."));
        Assert.Contains("gh auth login", GitHubCliPullRequestSource.Explain("To get started with GitHub CLI, please run:  gh auth login"));
        Assert.Equal("HTTP 502: Bad Gateway", GitHubCliPullRequestSource.Explain("HTTP 502: Bad Gateway\nmore detail"));
    }

    // main: A -> C (C touches only sub/keep.txt); feature off A: B1 -> B2.
    // The PR (base = main tip C, head = B2) must diff from merge-base A, so C's
    // change stays out and only the branch's own work shows.
    [Fact]
    public void LoadPullRequest_ShowsOnlyTheBranchsNetChanges_FromTheMergeBase()
    {
        var (pr, forkPoint) = BuildPullRequestRepo();

        var range = PullRequestRanges.Resolve(_root, pr, () => throw new InvalidOperationException("no fetch expected"), 10_000, TestContext.Current.CancellationToken);
        Assert.Equal(forkPoint, range.BaseSha);
        Assert.Equal(pr.HeadRefOid, range.HeadSha);

        var snapshot = CodeAnalysisRepositoryReader.LoadPullRequest(_root, range, TestContext.Current.CancellationToken);
        var files = snapshot.Scan.Files.ToDictionary(f => f.RelativePath);
        Assert.Equal(new[] { "a.txt", "gone.txt", "new.cs", "sub/feature.txt" }, files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(4, files["a.txt"].Lines.Total);
        Assert.Equal(0, files["gone.txt"].Lines.Total);
        Assert.True(files["new.cs"].Lines.Code > 0);

        var history = snapshot.History!;
        Assert.Equal(2, history.CommitCount);
        Assert.Equal(new[] { "Ada", "Grace" }, history.AuthorCommits.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((1L, 0L), (history.ByPath["a.txt"].Added, history.ByPath["a.txt"].Removed));
        Assert.Equal((0L, 2L), (history.ByPath["gone.txt"].Added, history.ByPath["gone.txt"].Removed));
        Assert.False(history.ByPath.ContainsKey("sub/keep.txt"));

        // A folder scope sees only its part of the PR, with scope-relative paths.
        var scoped = CodeAnalysisRepositoryReader.LoadPullRequest(Path.Combine(_root, "sub"), range, TestContext.Current.CancellationToken);
        Assert.Equal("feature.txt", Assert.Single(scoped.Scan.Files).RelativePath);
    }

    [Fact]
    public void Resolve_FailsClearlyWhenTheHeadCannotBeFetched()
    {
        var (pr, _) = BuildPullRequestRepo();
        var missing = pr with { HeadRefOid = new string('1', 40) };

        var ex = Assert.Throws<PullRequestSourceException>(() => PullRequestRanges.Resolve(_root, missing, () => null, 10_000, TestContext.Current.CancellationToken));
        Assert.Contains("Could not fetch PR #7", ex.Message);
    }

    private (PullRequestInfo Pr, string ForkPoint) BuildPullRequestRepo()
    {
        Repository.Init(_root);
        using var repo = new Repository(_root);
        repo.Config.Set("core.autocrlf", false);
        var forkPoint = Commit(repo, "Ada", "scaffold", ("a.txt", "1\n2\n3\n"), ("gone.txt", "x\ny\n"), ("sub/keep.txt", "keep\n"));
        var main = repo.Head.FriendlyName;

        Commands.Checkout(repo, repo.CreateBranch("feature"));
        Commit(repo, "Ada", "feature work", ("a.txt", "1\n2\n3\n4\n"), ("new.cs", "// new\nclass A { }\n"), ("sub/feature.txt", "f\n"));
        Delete(repo, "gone.txt");
        var head = Commit(repo, "Grace", "drop gone");

        Commands.Checkout(repo, repo.Branches[main]);
        var baseTip = Commit(repo, "Ada", "main moves on", ("sub/keep.txt", "keep\nmore\n"));

        var pr = new PullRequestInfo(7, "Feature", "OPEN", false, "ada", DateTimeOffset.Now, main, "feature", baseTip, head, 0, 0, 0);
        return (pr, forkPoint);
    }

    private string Commit(Repository repo, string author, string message, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            Commands.Stage(repo, path);
        }

        var signature = new Signature(author, author.ToLowerInvariant() + "@example.com", DateTimeOffset.Now);
        return repo.Commit(message, signature, signature).Sha;
    }

    private void Delete(Repository repo, string path)
    {
        File.Delete(Path.Combine(_root, path));
        Commands.Stage(repo, path);
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
