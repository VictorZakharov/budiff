using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Budiff.App;
using Budiff.Git;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The PR picker opened on one folder of a big repository lists only PRs that
// touch it - merged ones from the folder's own history (GitHub is asked only
// about those commits), open ones checked locally when their commits are
// here, else by their file list.
public sealed class ScopedPullRequestTests
{
    [Fact]
    public void AnyoneAll_ListsOpenPrsTouchingTheFolder_ThenItsMergedHistory()
    {
        var (root, c) = BuildRepo();
        var api = new FakeApi(c);
        var search = new ScopedPullRequestSearch(root, "proj", api);
        var query = new PullRequestQuery(null, PullRequestStateFilter.All, string.Empty);

        // A page returns once a request found something: the open PRs first,
        // checked locally, else by the files of PRs whose commits are not here.
        var open = search.Search(query, null, 10, CancellationToken.None);
        Assert.Equal(new[] { 10, 12 }, open.Items.Select(p => p.Number));
        Assert.True(open.HasNextPage);
        Assert.Equal(-1, open.TotalCount);
        Assert.Equal(PullRequestStateFilter.Open, Assert.Single(api.Queries).State);
        Assert.Equal(new[] { 12, 13 }, api.FilesAsked);
        Assert.Empty(api.CommitsAsked);

        // Then the merged history: GitHub hears only about the folder's
        // commits (not "PR 2"), newest first.
        var merged = search.Search(query, open.EndCursor, 10, CancellationToken.None);
        Assert.Equal(new[] { 3, 1 }, merged.Items.Select(p => p.Number));
        Assert.False(merged.HasNextPage);
        Assert.Equal(new[] { c["direct"], c["pr3"], c["pr1"], c["scaffold"] }, api.CommitsAsked);

        // Asked again: nothing new goes out but the search itself.
        search.Search(query, null, 10, CancellationToken.None);
        search.Search(query, open.EndCursor, 10, CancellationToken.None);
        Assert.Equal(4, api.CommitsAsked.Count);
        Assert.Equal(2, api.FilesAsked.Count);
    }

    // A request that finds nothing does not end the page: it goes on (within
    // a budget) until something turns up.
    [Fact]
    public void ARequestThatFindsNothing_KeepsLooking()
    {
        var (root, c) = BuildRepo();
        var api = new FakeApi(c) { OnlyOpen = 10 };
        var search = new ScopedPullRequestSearch(root, "Other", api);

        // The one open PR touches Proj only; Other's history has PR 2.
        var page = search.Search(new PullRequestQuery(null, PullRequestStateFilter.All, string.Empty), null, 10, CancellationToken.None);
        Assert.Equal(new[] { 2 }, page.Items.Select(p => p.Number));
        Assert.Equal(PullRequestStateFilter.Open, Assert.Single(api.Queries).State);
        Assert.Equal(new[] { c["pr2"], c["scaffold"] }, api.CommitsAsked);
    }

    [Fact]
    public void Paging_GoesFromTheOpenPrsToTheHistory()
    {
        var (root, c) = BuildRepo();
        var search = new ScopedPullRequestSearch(root, "Proj", new FakeApi(c));
        var query = new PullRequestQuery(null, PullRequestStateFilter.All, string.Empty);

        var first = search.Search(query, null, 1, CancellationToken.None);
        Assert.Equal(new[] { 10, 12 }, first.Items.Select(p => p.Number));
        Assert.True(first.HasNextPage);

        var second = search.Search(query, first.EndCursor, 1, CancellationToken.None);
        Assert.Equal(new[] { 3, 1 }, second.Items.Select(p => p.Number));
        Assert.False(second.HasNextPage);
        Assert.Null(second.EndCursor);
    }

    [Fact]
    public void Merged_ComesFromHistory_AnAuthorFromSearch()
    {
        var (root, c) = BuildRepo();
        var api = new FakeApi(c);
        var search = new ScopedPullRequestSearch(root, "proj", api);

        var merged = search.Search(new PullRequestQuery(null, PullRequestStateFilter.Merged, string.Empty), null, 10, CancellationToken.None);
        Assert.Equal(new[] { 3, 1 }, merged.Items.Select(p => p.Number));
        Assert.Empty(api.Queries);

        // By author (or text): GitHub narrows, the folder filters.
        var ada = search.Search(new PullRequestQuery("ada", PullRequestStateFilter.All, string.Empty), null, 10, CancellationToken.None);
        Assert.Equal(new[] { 3 }, ada.Items.Select(p => p.Number));
        Assert.Equal("ada", Assert.Single(api.Queries).Author);
    }

    [Fact]
    public void GitHubAnswers_Parse()
    {
        var pulls = PullRequestSearch.ParseCommitPulls(
            """{"data":{"repository":{"c0":{"associatedPullRequests":{"nodes":[{"number":7,"title":"T","state":"MERGED","author":{"login":"ada"}}]}},"c1":null}}}""",
            new[] { "aaa", "bbb" });
        Assert.Equal(7, Assert.Single(pulls["aaa"]).Number);
        Assert.False(pulls.ContainsKey("bbb"));

        var files = PullRequestSearch.ParseFiles(
            """{"data":{"repository":{"p0":{"files":{"totalCount":2,"nodes":[{"path":"a/x.cs"},{"path":"b/y.cs"}]}},"p1":{"files":{"totalCount":300,"nodes":[{"path":"a/z.cs"}]}}}}}""",
            new[] { 5, 6 });
        Assert.Equal(new[] { "a/x.cs", "b/y.cs" }, files[5]);
        Assert.Null(files[6]);
    }

    // The repository gh would use, read from the clone instead of asking gh
    // (~0.5 s): set-default's mark, else the only GitHub repository.
    [Theory]
    [InlineData("https://github.com/Ada/Engine.git", "Ada/Engine")]
    [InlineData("https://github.com/Ada/Engine", "Ada/Engine")]
    [InlineData("git@github.com:Ada/Engine.git", "Ada/Engine")]
    [InlineData("ssh://git@ssh.github.com:443/Ada/Engine.git", "Ada/Engine")]
    [InlineData("https://gitlab.com/Ada/Engine.git", null)]
    [InlineData("https://github.com/Ada", null)]
    [InlineData("C:/src/engine", null)]
    public void GitHubRemote_ParsesGitHubUrls(string url, string? repo)
        => Assert.Equal(repo, GitHubRemote.ParseUrl(url));

    [Fact]
    public void GitHubRemote_FollowsGh()
    {
        var (root, _) = BuildRepo();
        using (var repo = new Repository(root))
        {
            repo.Network.Remotes.Add("origin", "git@github.com:me/engine.git");
            repo.Refs.Add("refs/remotes/origin/trunk", repo.Head.Tip.Id);
            repo.Refs.Add("refs/remotes/origin/HEAD", "refs/remotes/origin/trunk");
        }

        Assert.Equal(("me/engine", "trunk"), GitHubRemote.Resolve(Path.Combine(root, "Proj")));
        Assert.True(GitHubRemote.HasGitHubRemote(root));

        // A fork's two repositories: only set-default's mark decides.
        using (var repo = new Repository(root))
        {
            repo.Network.Remotes.Add("upstream", "https://github.com/corp/engine.git");
        }

        Assert.Null(GitHubRemote.Resolve(root));
        using (var repo = new Repository(root))
        {
            repo.Config.Set("remote.upstream.gh-resolved", "base");
        }

        Assert.Equal(("corp/engine", (string?)null), GitHubRemote.Resolve(root));

        var plain = Directory.CreateTempSubdirectory("budiff-no-github-").FullName;
        Repository.Init(plain);
        using (var repo = new Repository(plain))
        {
            repo.Network.Remotes.Add("origin", "https://gitlab.com/me/engine.git");
        }

        Assert.Null(GitHubRemote.Resolve(plain));
        Assert.False(GitHubRemote.HasGitHubRemote(plain));
    }

    [Fact]
    public async Task Picker_SaysWhatTheListIsLimitedTo()
    {
        var source = new PagesSource("src/Shop",
            new PullRequestPage(Array.Empty<PullRequestInfo>(), "0:x", true, -1),
            new PullRequestPage(new[] { Pr(4, "MERGED", "", ""), Pr(5, "MERGED", "", "") }, "1:40", true, -1),
            new PullRequestPage(Array.Empty<PullRequestInfo>(), null, false, -1));
        var vm = new PullRequestPickerViewModel(source);

        await vm.InitializeAsync();
        Assert.Equal("Looking for pull requests touching src/Shop...", vm.StatusText);
        await vm.LoadMoreAsync();
        Assert.Equal("2 touching src/Shop - more as you scroll", vm.StatusText);
        await vm.LoadMoreAsync();
        Assert.Equal("2 touching src/Shop", vm.StatusText);
    }

    // master: scaffold (Proj + Other) -> PR 1 (Proj) -> PR 2 (Other) -> PR 3
    // (Proj) -> a direct push (Proj); open branches off it: "feature" (Proj),
    // "other" (Other).
    private static (string Root, Dictionary<string, string> Commits) BuildRepo()
    {
        var root = Directory.CreateTempSubdirectory("budiff-scoped-prs-").FullName;
        Repository.Init(root);
        using var repo = new Repository(root);
        var c = new Dictionary<string, string>
        {
            ["scaffold"] = Commit(repo, "Scaffold", ("Proj/App.cs", "v1"), ("Other/Lib.cs", "v1")),
            ["pr1"] = Commit(repo, "Shop: first change (#1)", ("Proj/App.cs", "v2")),
            ["pr2"] = Commit(repo, "Lib: change (#2)", ("Other/Lib.cs", "v2")),
            ["pr3"] = Commit(repo, "Shop: second change (#3)", ("Proj/App.cs", "v3")),
            ["direct"] = Commit(repo, "Notes", ("Proj/Notes.md", "n")),
        };
        var master = repo.Head;
        Commands.Checkout(repo, repo.CreateBranch("feature"));
        c["feature"] = Commit(repo, "Feature", ("Proj/App.cs", "v4"));
        Commands.Checkout(repo, master);
        Commands.Checkout(repo, repo.CreateBranch("other"));
        c["other"] = Commit(repo, "Other feature", ("Other/Lib.cs", "v3"));
        Commands.Checkout(repo, master);
        return (root, c);
    }

    private static string Commit(Repository repo, string message, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(repo.Info.WorkingDirectory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            Commands.Stage(repo, path);
        }

        var who = new Signature("Ada", "ada@example.com", DateTimeOffset.Now);
        return repo.Commit(message, who, who).Sha;
    }

    private static PullRequestInfo Pr(int number, string state, string head, string @base, string author = "ada")
        => new(number, "PR " + number, state, false, author, DateTimeOffset.Now, "master", "b" + number, @base, head, 1, 1, 1);

    private sealed class FakeApi(Dictionary<string, string> c) : IPullRequestApi
    {
        public List<PullRequestQuery> Queries { get; } = new();

        public List<string> CommitsAsked { get; } = new();

        public List<int> FilesAsked { get; } = new();

        // When set, the open search finds just this PR.
        public int? OnlyOpen { get; init; }

        public PullRequestPage Search(PullRequestQuery query, string? after, int pageSize, CancellationToken ct)
        {
            Queries.Add(query);
            var items = query.Author == "ada"
                ? new[] { Pr(3, "MERGED", c["pr3"], c["pr2"]), Pr(2, "MERGED", c["pr2"], c["pr1"]) }
                : new[]
                {
                    Pr(10, "OPEN", c["feature"], c["direct"]),
                    Pr(11, "OPEN", c["other"], c["direct"]),
                    Pr(12, "OPEN", new string('a', 40), c["direct"]),
                    Pr(13, "OPEN", new string('b', 40), c["direct"]),
                };
            items = OnlyOpen is { } only ? items.Where(p => p.Number == only).ToArray() : items;
            return new PullRequestPage(items, null, false, items.Length);
        }

        public IReadOnlyDictionary<string, IReadOnlyList<PullRequestInfo>> PullRequestsOf(IReadOnlyList<string> commits, CancellationToken ct)
        {
            CommitsAsked.AddRange(commits);
            var known = new Dictionary<string, PullRequestInfo>
            {
                [c["pr1"]] = Pr(1, "MERGED", c["pr1"], c["scaffold"]),
                [c["pr2"]] = Pr(2, "MERGED", c["pr2"], c["pr1"]),
                [c["pr3"]] = Pr(3, "MERGED", c["pr3"], c["pr2"]),
            };
            return commits.ToDictionary(sha => sha, sha => (IReadOnlyList<PullRequestInfo>)(known.TryGetValue(sha, out var pr) ? new[] { pr } : Array.Empty<PullRequestInfo>()));
        }

        public IReadOnlyDictionary<int, IReadOnlyList<string>?> FilesOf(IReadOnlyList<int> numbers, CancellationToken ct)
        {
            FilesAsked.AddRange(numbers);
            var files = new Dictionary<int, IReadOnlyList<string>?> { [12] = new[] { "Proj/x.cs" }, [13] = new[] { "Other/y.cs" } };
            return numbers.ToDictionary(n => n, n => files.GetValueOrDefault(n));
        }

        public string HistoryRef(CancellationToken ct) => "master";
    }

    private sealed class PagesSource(string scope, params PullRequestPage[] pages) : IPullRequestSource
    {
        private int _next;

        public string Scope => scope;

        public Task<PullRequestPage> SearchAsync(PullRequestQuery query, string? after, int pageSize, CancellationToken ct)
            => Task.FromResult(pages[Math.Min(_next++, pages.Length - 1)]);

        public Task<IReadOnlyList<RepoPerson>> GetPeopleAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RepoPerson>>(Array.Empty<RepoPerson>());

        public Task<string?> GetCurrentLoginAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<PullRequestRange> ResolveAsync(PullRequestInfo pullRequest, CancellationToken ct) => throw new NotSupportedException();
    }
}
