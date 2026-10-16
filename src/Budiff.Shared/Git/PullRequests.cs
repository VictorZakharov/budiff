using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Budiff.Git;

public enum PullRequestStateFilter
{
    All,
    Open,
    Merged,
    Closed,
}

public sealed record PullRequestInfo(
    int Number,
    string Title,
    string State,
    bool IsDraft,
    string Author,
    DateTimeOffset UpdatedAt,
    string BaseRefName,
    string HeadRefName,
    string BaseRefOid,
    string HeadRefOid,
    int? Additions,
    int? Deletions,
    int? ChangedFiles)
{
    // Null until fetched: lists come without them (see GetCountsAsync).
    public PullRequestCounts? Counts
        => Additions is { } added && Deletions is { } removed && ChangedFiles is { } files ? new(added, removed, files) : null;

    // GitHub reports OPEN / MERGED / CLOSED; a draft is an open PR.
    public string StateLabel => State switch
    {
        "OPEN" => IsDraft ? "Draft" : "Open",
        "MERGED" => "Merged",
        "CLOSED" => "Closed",
        _ => State,
    };
}

// A PR's size as GitHub shows it: lines added / removed, files changed.
public sealed record PullRequestCounts(int Additions, int Deletions, int ChangedFiles);

public sealed record PullRequestPage(IReadOnlyList<PullRequestInfo> Items, string? EndCursor, bool HasNextPage, int TotalCount)
{
    public static PullRequestPage Empty { get; } = new(Array.Empty<PullRequestInfo>(), null, false, 0);
}

// Author: null/empty = anyone, "@me" = the signed-in user, else a login.
// Text: free words, a "#123" number, or raw GitHub search qualifiers.
public sealed record PullRequestQuery(string? Author, PullRequestStateFilter State, string Text);

// Email: the public profile email, else one they commit with (see
// ParsePeople); AvatarUrl: their GitHub picture.
public sealed record RepoPerson(string Login, string? Name, string? Email = null, string? AvatarUrl = null)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Login : $"{Name} ({Login})";
}

// What a PR changes: merge-base(base, head) -> head, the range GitHub's
// "Files changed" tab shows. Both ends are local commits once resolved.
public sealed record PullRequestRange(PullRequestInfo PullRequest, string BaseSha, string HeadSha);

public sealed class PullRequestSourceException(string message) : Exception(message);

// The GitHub repository a clone points at, read locally - `gh repo view`
// takes ~0.5 s. Follows gh: the remote `gh repo set-default` marked, else the
// only GitHub repository among the remotes. Null when that is not clear
// (several, another host), and the caller asks gh.
internal static class GitHubRemote
{
    public static (string Repo, string? DefaultBranch)? Resolve(string root)
    {
        try
        {
            using var repo = OpenRepository(root);
            if (repo is null)
            {
                return null;
            }

            var remotes = repo.Network.Remotes
                .Select(r => (r.Name, Repo: ParseUrl(r.Url)))
                .Where(r => r.Repo is not null)
                .ToList();
            (string Name, string? Repo)? chosen = null;
            foreach (var remote in remotes)
            {
                // "base" on the remote gh set as default; older gh wrote owner/name.
                var resolved = repo.Config.Get<string>($"remote.{remote.Name}.gh-resolved")?.Value;
                if (resolved == "base")
                {
                    chosen = remote;
                    break;
                }

                if (resolved is not null && resolved.Contains('/'))
                {
                    chosen = (remote.Name, resolved);
                    break;
                }
            }

            if (chosen is null)
            {
                if (remotes.Select(r => r.Repo).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                {
                    return null;
                }

                chosen = remotes.OrderBy(r => r.Name switch { "upstream" => 0, "github" => 1, "origin" => 2, _ => 3 }).First();
            }

            var (name, owner) = chosen.Value;
            var prefix = $"refs/remotes/{name}/";
            var branch = repo.Refs[prefix + "HEAD"] is LibGit2Sharp.SymbolicReference { TargetIdentifier: var target } && target.StartsWith(prefix, StringComparison.Ordinal)
                ? target[prefix.Length..]
                : null;
            return (owner!, branch);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool HasGitHubRemote(string root)
    {
        try
        {
            using var repo = OpenRepository(root);
            return repo is not null && repo.Network.Remotes.Any(r => ParseUrl(r.Url) is not null);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // owner/name of a GitHub URL - https, ssh, or scp-like (git@host:o/n) -
    // on github.com or GH_HOST; null for anything else.
    internal static string? ParseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string host, path;
        var text = url.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "ssh" or "git")
        {
            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else if (text.IndexOf(':') is var colon and > 0 && !text.Contains("://", StringComparison.Ordinal))
        {
            var at = text.LastIndexOf('@', colon);
            host = text[(at + 1)..colon];
            path = text[(colon + 1)..];
        }
        else
        {
            return null;
        }

        var hosts = new[] { "github.com", "www.github.com", "ssh.github.com", Environment.GetEnvironmentVariable("GH_HOST") ?? string.Empty };
        if (!hosts.Any(h => h.Length > 0 && host.Equals(h, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var parts = path.Trim('/').Split('/');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return null;
        }

        var name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return name.Length > 0 ? parts[0] + "/" + name : null;
    }

    private static LibGit2Sharp.Repository? OpenRepository(string root)
        => LibGit2Sharp.Repository.Discover(root) is { } path ? new LibGit2Sharp.Repository(path) : null;
}

public interface IPullRequestSource
{
    Task<PullRequestPage> SearchAsync(PullRequestQuery query, string? after, int pageSize, CancellationToken ct);

    Task<IReadOnlyList<RepoPerson>> GetPeopleAsync(CancellationToken ct);

    Task<string?> GetCurrentLoginAsync(CancellationToken ct);

    Task<PullRequestRange> ResolveAsync(PullRequestInfo pullRequest, CancellationToken ct);

    // The folder the list is limited to (repository path), or empty for the
    // whole repository. A scoped page has no total (TotalCount -1).
    string Scope => string.Empty;

    // The +/- line and file counts of listed PRs, by number. GitHub computes
    // them per PR, which makes a list several times slower - so lists come
    // without them and the rows on screen ask afterwards.
    Task<IReadOnlyDictionary<int, PullRequestCounts>> GetCountsAsync(IReadOnlyList<PullRequestInfo> pullRequests, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<int, PullRequestCounts>>(new Dictionary<int, PullRequestCounts>());

    // Whether searching ahead is worthwhile (a pick may follow soon): the
    // repository has a GitHub remote.
    bool CanSearchAhead => false;
}

public static class PullRequestSearch
{
    // What a list row needs - not the counts (PullRequestCountFields).
    internal const string PullRequestFields =
        "number title state isDraft author { login } updatedAt baseRefName headRefName baseRefOid headRefOid";

    internal const string PullRequestCountFields = "additions deletions changedFiles";

    // GitHub search syntax for one picker query, newest activity first.
    public static string BuildQuery(string repo, PullRequestQuery query)
    {
        var parts = new List<string> { "repo:" + repo, "is:pr" };
        var author = query.Author?.Trim();
        if (!string.IsNullOrEmpty(author))
        {
            parts.Add("author:" + author);
        }

        switch (query.State)
        {
            case PullRequestStateFilter.Open:
                parts.Add("is:open");
                break;
            case PullRequestStateFilter.Merged:
                parts.Add("is:merged");
                break;
            case PullRequestStateFilter.Closed:
                parts.Add("is:closed");
                parts.Add("is:unmerged");
                break;
        }

        parts.Add("sort:updated-desc");
        var text = query.Text.Trim();
        if (text.Length > 0)
        {
            parts.Add(text);
        }

        return string.Join(' ', parts);
    }

    // "#123" or "123" asks for that PR directly - search would only match the
    // digits inside titles and bodies.
    public static int? ParseNumber(string text)
    {
        var trimmed = text.Trim().TrimStart('#');
        return trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit) && int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;
    }

    // The `gh api graphql` search response: data.search { issueCount, pageInfo, nodes }.
    public static PullRequestPage ParseSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var search = doc.RootElement.GetProperty("data").GetProperty("search");
        var items = new List<PullRequestInfo>();
        foreach (var node in search.GetProperty("nodes").EnumerateArray())
        {
            if (ParsePullRequest(node) is { } pr)
            {
                items.Add(pr);
            }
        }

        var pageInfo = search.GetProperty("pageInfo");
        return new PullRequestPage(
            items,
            pageInfo.TryGetProperty("endCursor", out var cursor) && cursor.ValueKind == JsonValueKind.String ? cursor.GetString() : null,
            pageInfo.TryGetProperty("hasNextPage", out var more) && more.ValueKind == JsonValueKind.True,
            search.TryGetProperty("issueCount", out var count) && count.TryGetInt32(out var total) ? total : items.Count);
    }

    // data.repository.pullRequest - null when the number does not exist.
    public static PullRequestPage ParseSingle(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var repository = doc.RootElement.GetProperty("data").GetProperty("repository");
        return repository.TryGetProperty("pullRequest", out var node) && ParsePullRequest(node) is { } pr
            ? new PullRequestPage(new[] { pr }, null, false, 1)
            : PullRequestPage.Empty;
    }

    // data.repository.c{i}.associatedPullRequests.nodes, for commits[i]; a
    // commit GitHub does not know comes back null and is left out.
    public static IReadOnlyDictionary<string, IReadOnlyList<PullRequestInfo>> ParseCommitPulls(string json, IReadOnlyList<string> commits)
    {
        using var doc = JsonDocument.Parse(json);
        var repository = doc.RootElement.GetProperty("data").GetProperty("repository");
        var result = new Dictionary<string, IReadOnlyList<PullRequestInfo>>(StringComparer.Ordinal);
        for (var i = 0; i < commits.Count; i++)
        {
            if (repository.TryGetProperty("c" + i.ToString(CultureInfo.InvariantCulture), out var commit)
                && commit.ValueKind == JsonValueKind.Object
                && commit.TryGetProperty("associatedPullRequests", out var pulls)
                && pulls.TryGetProperty("nodes", out var nodes))
            {
                result[commits[i]] = nodes.EnumerateArray().Select(ParsePullRequest).OfType<PullRequestInfo>().ToList();
            }
        }

        return result;
    }

    // data.repository.p{i} { additions deletions changedFiles }, for
    // numbers[i]; a PR GitHub did not return is left out.
    public static IReadOnlyDictionary<int, PullRequestCounts> ParseCounts(string json, IReadOnlyList<int> numbers)
    {
        using var doc = JsonDocument.Parse(json);
        var repository = doc.RootElement.GetProperty("data").GetProperty("repository");
        var result = new Dictionary<int, PullRequestCounts>();
        for (var i = 0; i < numbers.Count; i++)
        {
            if (repository.TryGetProperty("p" + i.ToString(CultureInfo.InvariantCulture), out var pr)
                && pr.ValueKind == JsonValueKind.Object
                && IntOrNull(pr, "additions") is { } added
                && IntOrNull(pr, "deletions") is { } removed
                && IntOrNull(pr, "changedFiles") is { } files)
            {
                result[numbers[i]] = new PullRequestCounts(added, removed, files);
            }
        }

        return result;
    }

    // data.repository.p{i}.files { totalCount, nodes [{ path }] }, for
    // numbers[i]; null paths when GitHub listed only some of them.
    public static IReadOnlyDictionary<int, IReadOnlyList<string>?> ParseFiles(string json, IReadOnlyList<int> numbers)
    {
        using var doc = JsonDocument.Parse(json);
        var repository = doc.RootElement.GetProperty("data").GetProperty("repository");
        var result = new Dictionary<int, IReadOnlyList<string>?>();
        for (var i = 0; i < numbers.Count; i++)
        {
            if (repository.TryGetProperty("p" + i.ToString(CultureInfo.InvariantCulture), out var pr)
                && pr.ValueKind == JsonValueKind.Object
                && pr.TryGetProperty("files", out var files)
                && files.ValueKind == JsonValueKind.Object)
            {
                var paths = files.GetProperty("nodes").EnumerateArray().Select(n => String(n, "path")).Where(p => p.Length > 0).ToList();
                var total = Int(files, "totalCount");
                result[numbers[i]] = total > paths.Count ? null : paths;
            }
        }

        return result;
    }

    // data.repository: mentionableUsers.nodes [{ login, name, email,
    // avatarUrl }], and the default branch's recent commits [{ author {
    // email, user { login } } }]. GitHub shows a profile email only when it
    // is public, so a person without one gets the newest address they
    // committed with (not a noreply one).
    public static IReadOnlyList<RepoPerson> ParsePeople(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var repository = doc.RootElement.GetProperty("data").GetProperty("repository");
        var commitEmails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (repository.TryGetProperty("defaultBranchRef", out var branch) && branch.ValueKind == JsonValueKind.Object
            && branch.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object
            && target.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Object)
        {
            foreach (var commit in history.GetProperty("nodes").EnumerateArray())
            {
                if (commit.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object
                    && author.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                    && Email(String(author, "email")) is { } email)
                {
                    commitEmails.TryAdd(String(user, "login"), email);
                }
            }
        }

        var people = new List<RepoPerson>();
        foreach (var node in repository.GetProperty("mentionableUsers").GetProperty("nodes").EnumerateArray())
        {
            var login = String(node, "login");
            if (login.Length > 0)
            {
                var name = String(node, "name");
                var avatar = String(node, "avatarUrl");
                people.Add(new RepoPerson(
                    login,
                    name.Length > 0 ? name : null,
                    Email(String(node, "email")) ?? commitEmails.GetValueOrDefault(login),
                    avatar.Length > 0 ? avatar : null));
            }
        }

        return people.OrderBy(p => p.Name ?? p.Login, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // An address worth showing: not empty, not GitHub's noreply stand-in.
    private static string? Email(string text)
        => text.Contains('@') && !text.Contains("noreply", StringComparison.OrdinalIgnoreCase) ? text : null;

    // Search nodes can be issues (empty objects under the PullRequest fragment).
    private static PullRequestInfo? ParsePullRequest(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("number", out var number) || !number.TryGetInt32(out var n))
        {
            return null;
        }

        var author = node.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? String(a, "login") : string.Empty;
        return new PullRequestInfo(
            n,
            String(node, "title"),
            String(node, "state"),
            node.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
            author.Length > 0 ? author : "ghost",
            node.TryGetProperty("updatedAt", out var updated) && updated.TryGetDateTimeOffset(out var when) ? when : default,
            String(node, "baseRefName"),
            String(node, "headRefName"),
            String(node, "baseRefOid"),
            String(node, "headRefOid"),
            IntOrNull(node, "additions"),
            IntOrNull(node, "deletions"),
            IntOrNull(node, "changedFiles"));
    }

    private static int? IntOrNull(JsonElement node, string name)
        => node.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static string String(JsonElement node, string name)
        => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int Int(JsonElement node, string name)
        => node.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
}

// Pull requests through the GitHub CLI: it already owns auth, enterprise
// hosts, and which remote is "the" GitHub repo (`gh repo set-default`).
//
// scopePrefix (a folder's repository path) limits the list to PRs that touch
// it - see ScopedPullRequestSearch; an explicit "#123" is shown regardless.
public sealed class GitHubCliPullRequestSource : IPullRequestSource, IPullRequestApi
{
    private const int TimeoutMs = 30_000;
    private const int FetchTimeoutMs = 180_000;

    private readonly string _root;
    private readonly ScopedPullRequestSearch? _scoped;
    private readonly object _repoGate = new();
    private readonly ConcurrentDictionary<(int Number, string Head), PullRequestCounts> _counts = new();
    private string? _repo;
    private string? _defaultBranch;
    private bool _viewed;
    private bool? _canSearchAhead;
    private string? _historyRef;

    public GitHubCliPullRequestSource(string root, string scopePrefix = "")
    {
        _root = root;
        if (scopePrefix.Trim('/', '\\').Length > 0)
        {
            _scoped = new ScopedPullRequestSearch(_root, scopePrefix, this);
        }
    }

    public string Scope => _scoped?.Scope ?? string.Empty;

    public bool CanSearchAhead => _canSearchAhead ??= GitHubRemote.HasGitHubRemote(_root);

    // One GraphQL request for the rows: an alias per PR. Kept per head
    // commit - a PR's counts change only when its head moves.
    public Task<IReadOnlyDictionary<int, PullRequestCounts>> GetCountsAsync(IReadOnlyList<PullRequestInfo> pullRequests, CancellationToken ct)
        => Task.Run<IReadOnlyDictionary<int, PullRequestCounts>>(() =>
        {
            var result = new Dictionary<int, PullRequestCounts>();
            var missing = new List<PullRequestInfo>();
            foreach (var pr in pullRequests)
            {
                if (_counts.TryGetValue((pr.Number, pr.HeadRefOid), out var known))
                {
                    result[pr.Number] = known;
                }
                else
                {
                    missing.Add(pr);
                }
            }

            if (missing.Count > 0)
            {
                var body = new StringBuilder();
                for (var i = 0; i < missing.Count; i++)
                {
                    body.Append('p').Append(i.ToString(CultureInfo.InvariantCulture))
                        .Append(":pullRequest(number:").Append(missing[i].Number.ToString(CultureInfo.InvariantCulture))
                        .Append("){").Append(PullRequestSearch.PullRequestCountFields).Append("} ");
                }

                var numbers = missing.Select(pr => pr.Number).ToList();
                var found = PullRequestSearch.ParseCounts(RepositoryQuery(ct, body.ToString()), numbers);
                foreach (var pr in missing)
                {
                    if (found.TryGetValue(pr.Number, out var counts))
                    {
                        result[pr.Number] = _counts[(pr.Number, pr.HeadRefOid)] = counts;
                    }
                }
            }

            return result;
        }, ct);

    public Task<PullRequestPage> SearchAsync(PullRequestQuery query, string? after, int pageSize, CancellationToken ct)
        => Task.Run(() =>
            _scoped is not null && PullRequestSearch.ParseNumber(query.Text) is null
                ? _scoped.Search(query, after, pageSize, ct)
                : ((IPullRequestApi)this).Search(query, after, pageSize, ct), ct);

    PullRequestPage IPullRequestApi.Search(PullRequestQuery query, string? after, int pageSize, CancellationToken ct)
    {
        var repo = Repo(ct);
        if (PullRequestSearch.ParseNumber(query.Text) is { } number)
        {
            var (owner, name) = Split(repo);
            return PullRequestSearch.ParseSingle(Gh(
                ct,
                "api", "graphql",
                "-f", "query=query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){" + PullRequestSearch.PullRequestFields + "}}}",
                "-f", "owner=" + owner,
                "-f", "name=" + name,
                "-F", "number=" + number.ToString(CultureInfo.InvariantCulture)));
        }

        var args = new List<string>
        {
            "api", "graphql",
            "-f", "query=query($q:String!,$n:Int!,$after:String){search(query:$q,type:ISSUE,first:$n,after:$after){issueCount pageInfo{endCursor hasNextPage} nodes{... on PullRequest{" + PullRequestSearch.PullRequestFields + "}}}}",
            "-f", "q=" + PullRequestSearch.BuildQuery(repo, query),
            "-F", "n=" + pageSize.ToString(CultureInfo.InvariantCulture),
        };
        if (after is not null)
        {
            args.Add("-f");
            args.Add("after=" + after);
        }

        return PullRequestSearch.ParseSearch(Gh(ct, args.ToArray()));
    }

    // One GraphQL request for the slice: an alias per commit (SHAs are hex).
    public IReadOnlyDictionary<string, IReadOnlyList<PullRequestInfo>> PullRequestsOf(IReadOnlyList<string> commits, CancellationToken ct)
    {
        var shas = commits.Where(IsSha).ToList();
        if (shas.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<PullRequestInfo>>();
        }

        var body = new StringBuilder();
        for (var i = 0; i < shas.Count; i++)
        {
            body.Append('c').Append(i.ToString(CultureInfo.InvariantCulture))
                .Append(":object(oid:\"").Append(shas[i]).Append("\"){...on Commit{associatedPullRequests(first:3){nodes{")
                .Append(PullRequestSearch.PullRequestFields).Append("}}}} ");
        }

        return PullRequestSearch.ParseCommitPulls(RepositoryQuery(ct, body.ToString()), shas);
    }

    public IReadOnlyDictionary<int, IReadOnlyList<string>?> FilesOf(IReadOnlyList<int> numbers, CancellationToken ct)
    {
        if (numbers.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<string>?>();
        }

        var body = new StringBuilder();
        for (var i = 0; i < numbers.Count; i++)
        {
            body.Append('p').Append(i.ToString(CultureInfo.InvariantCulture))
                .Append(":pullRequest(number:").Append(numbers[i].ToString(CultureInfo.InvariantCulture))
                .Append("){files(first:100){totalCount nodes{path}}} ");
        }

        return PullRequestSearch.ParseFiles(RepositoryQuery(ct, body.ToString()), numbers);
    }

    // The default branch on the remote that is the GitHub repo, else locally,
    // else HEAD.
    public string HistoryRef(CancellationToken ct)
    {
        if (_historyRef is not null)
        {
            return _historyRef;
        }

        var repo = Repo(ct);
        var branch = DefaultBranch(ct) ?? string.Empty;
        var candidates = new List<string>();
        if (branch.Length > 0)
        {
            candidates.Add($"refs/remotes/{PullRequestRanges.FindRemote(_root, repo)}/{branch}");
            candidates.Add("refs/heads/" + branch);
        }

        candidates.Add("HEAD");
        return _historyRef = candidates.First(c => c == "HEAD" || GitCli.TryRun(_root, out _, ct, "rev-parse", "--verify", "--quiet", c + "^{commit}"));
    }

    private string RepositoryQuery(CancellationToken ct, string body)
    {
        var (owner, name) = Split(Repo(ct));
        return Gh(
            ct,
            "api", "graphql",
            "-f", "query=query($owner:String!,$name:String!){repository(owner:$owner,name:$name){" + body + "}}",
            "-f", "owner=" + owner,
            "-f", "name=" + name);
    }

    private static bool IsSha(string text) => text.Length == 40 && text.All(char.IsAsciiHexDigit);

    public Task<IReadOnlyList<RepoPerson>> GetPeopleAsync(CancellationToken ct)
        => Task.Run(() =>
        {
            var (owner, name) = Split(Repo(ct));
            return PullRequestSearch.ParsePeople(Gh(
                ct,
                "api", "graphql",
                "-f", "query=query($owner:String!,$name:String!){repository(owner:$owner,name:$name){mentionableUsers(first:100){nodes{login name email avatarUrl(size:64)}} defaultBranchRef{target{...on Commit{history(first:100){nodes{author{email user{login}}}}}}}}}",
                "-f", "owner=" + owner,
                "-f", "name=" + name));
        }, ct);

    public Task<string?> GetCurrentLoginAsync(CancellationToken ct)
        => Task.Run(() =>
        {
            var login = Gh(ct, "api", "user", "-q", ".login").Trim();
            return login.Length > 0 ? login : null;
        }, ct);

    public Task<PullRequestRange> ResolveAsync(PullRequestInfo pullRequest, CancellationToken ct)
        => Task.Run(() => PullRequestRanges.Resolve(_root, pullRequest, () => Repo(ct), FetchTimeoutMs, ct), ct);

    // owner/name, once: from the clone's remotes when that is clear (instant),
    // else gh's answer (~0.5 s). Concurrent searches wait for the one answer.
    private string Repo(CancellationToken ct)
    {
        lock (_repoGate)
        {
            if (_repo is null)
            {
                if (GitHubRemote.Resolve(_root) is { } local)
                {
                    (_repo, _defaultBranch) = local;
                }
                else
                {
                    ViewRepo(ct);
                }
            }

            return _repo!;
        }
    }

    // The default branch: the remote's HEAD when the clone knows it, else gh's.
    private string? DefaultBranch(CancellationToken ct)
    {
        lock (_repoGate)
        {
            Repo(ct);
            if (_defaultBranch is null && !_viewed)
            {
                ViewRepo(ct);
            }

            return _defaultBranch;
        }
    }

    // gh's own resolution (`gh repo set-default`, hosts, forks): owner/name
    // and the default branch in one call.
    private void ViewRepo(CancellationToken ct)
    {
        var line = Gh(ct, "repo", "view", "--json", "nameWithOwner,defaultBranchRef", "-q", ".nameWithOwner + \" \" + .defaultBranchRef.name").Trim();
        var space = line.IndexOf(' ');
        _defaultBranch = space > 0 ? line[(space + 1)..].Trim() : null;
        _repo ??= space > 0 ? line[..space] : line;
        _viewed = true;
    }

    private static (string Owner, string Name) Split(string repo)
    {
        var slash = repo.IndexOf('/');
        return slash > 0 ? (repo[..slash], repo[(slash + 1)..]) : (repo, repo);
    }

    private string Gh(CancellationToken ct, params string[] args)
    {
        if (ToolProcess.TryRun("gh", _root, TimeoutMs, ct, args, out var stdout, out var stderr))
        {
            return stdout;
        }

        throw new PullRequestSourceException(Explain(stderr));
    }

    // gh's own words are usually right; the two setup problems get a next step.
    internal static string Explain(string stderr)
    {
        var text = stderr.Trim();
        if (text.Contains("not found on PATH", StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub CLI (gh) not found. Install it from https://cli.github.com and run `gh auth login`.";
        }

        if (text.Contains("gh auth login", StringComparison.OrdinalIgnoreCase) || text.Contains("not logged", StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub CLI is not signed in. Run `gh auth login`, then try again.";
        }

        if (text.Contains("git remote", StringComparison.OrdinalIgnoreCase))
        {
            return "This repository has no GitHub remote.";
        }

        var firstLine = text.Split('\n', 2)[0].Trim();
        return firstLine.Length > 0 ? firstLine : "GitHub CLI request failed.";
    }
}

public static class PullRequestRanges
{
    // Makes both ends of a PR local and returns its merge-base -> head range.
    // A PR that was never fetched (someone else's, or a deleted branch) comes
    // from refs/pull/N/head plus its base branch, fetched without writing any
    // ref (not even FETCH_HEAD): new objects only - no branch, remote-tracking
    // ref, or working file moves.
    public static PullRequestRange Resolve(string root, PullRequestInfo pr, Func<string?> repo, int fetchTimeoutMs, CancellationToken ct = default)
    {
        if (!HasCommit(root, pr.HeadRefOid, ct) || !HasCommit(root, pr.BaseRefOid, ct))
        {
            var remote = FindRemote(root, repo());
            var refspecs = new List<string> { $"refs/pull/{pr.Number}/head" };
            if (pr.BaseRefName.Length > 0)
            {
                refspecs.Add("refs/heads/" + pr.BaseRefName);
            }

            if (!Fetch(root, remote, refspecs, fetchTimeoutMs, ct, out var error) && refspecs.Count > 1)
            {
                // The base branch may be gone; the PR head alone often still has the base in its history.
                Fetch(root, remote, refspecs.Take(1).ToList(), fetchTimeoutMs, ct, out error);
            }

            if (!HasCommit(root, pr.HeadRefOid, ct))
            {
                throw new PullRequestSourceException($"Could not fetch PR #{pr.Number} from '{remote}'. {error}".Trim());
            }

            if (!HasCommit(root, pr.BaseRefOid, ct))
            {
                throw new PullRequestSourceException($"The base commit of PR #{pr.Number} ({Short(pr.BaseRefOid)}) is not available, even after fetching '{remote}'.");
            }
        }

        var mergeBase = GitCli.TryRun(root, out var output, ct, "merge-base", pr.BaseRefOid, pr.HeadRefOid) && output.Trim().Length >= 40
            ? output.Trim()
            : pr.BaseRefOid;
        return new PullRequestRange(pr, mergeBase, pr.HeadRefOid);
    }

    // The remote whose URL points at owner/name (https or ssh), else origin.
    public static string FindRemote(string root, string? repo)
    {
        if (!string.IsNullOrEmpty(repo) && GitCli.TryRun(root, out var output, "remote", "-v"))
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && UrlMatches(parts[1], repo))
                {
                    return parts[0];
                }
            }
        }

        return "origin";
    }

    internal static bool UrlMatches(string url, string repo)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return trimmed.EndsWith("/" + repo, StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(":" + repo, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCommit(string root, string sha, CancellationToken ct)
        => sha.Length > 0 && GitCli.TryRun(root, out _, ct, "cat-file", "-e", sha + "^{commit}");

    private static bool Fetch(string root, string remote, IReadOnlyList<string> refspecs, int timeoutMs, CancellationToken ct, out string error)
    {
        var args = new List<string> { "fetch", "--no-tags", "--no-write-fetch-head", "--quiet", remote };
        args.AddRange(refspecs);
        var ok = ToolProcess.TryRun("git", root, timeoutMs, ct, args, out _, out var stderr);
        error = ok ? string.Empty : stderr.Trim().Split('\n', 2)[0];
        return ok;
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
