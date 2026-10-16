using System;
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
    int Additions,
    int Deletions,
    int ChangedFiles)
{
    // GitHub reports OPEN / MERGED / CLOSED; a draft is an open PR.
    public string StateLabel => State switch
    {
        "OPEN" => IsDraft ? "Draft" : "Open",
        "MERGED" => "Merged",
        "CLOSED" => "Closed",
        _ => State,
    };
}

public sealed record PullRequestPage(IReadOnlyList<PullRequestInfo> Items, string? EndCursor, bool HasNextPage, int TotalCount)
{
    public static PullRequestPage Empty { get; } = new(Array.Empty<PullRequestInfo>(), null, false, 0);
}

// Author: null/empty = anyone, "@me" = the signed-in user, else a login.
// Text: free words, a "#123" number, or raw GitHub search qualifiers.
public sealed record PullRequestQuery(string? Author, PullRequestStateFilter State, string Text);

public sealed record RepoPerson(string Login, string? Name)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Login : $"{Name} ({Login})";
}

// What a PR changes: merge-base(base, head) -> head, the range GitHub's
// "Files changed" tab shows. Both ends are local commits once resolved.
public sealed record PullRequestRange(PullRequestInfo PullRequest, string BaseSha, string HeadSha);

public sealed class PullRequestSourceException(string message) : Exception(message);

public interface IPullRequestSource
{
    Task<PullRequestPage> SearchAsync(PullRequestQuery query, string? after, int pageSize, CancellationToken ct);

    Task<IReadOnlyList<RepoPerson>> GetPeopleAsync(CancellationToken ct);

    Task<string?> GetCurrentLoginAsync(CancellationToken ct);

    Task<PullRequestRange> ResolveAsync(PullRequestInfo pullRequest, CancellationToken ct);
}

public static class PullRequestSearch
{
    internal const string PullRequestFields =
        "number title state isDraft author { login } updatedAt baseRefName headRefName baseRefOid headRefOid additions deletions changedFiles";

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

    // data.repository.mentionableUsers.nodes [{ login, name }].
    public static IReadOnlyList<RepoPerson> ParsePeople(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var people = new List<RepoPerson>();
        foreach (var node in doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("mentionableUsers").GetProperty("nodes").EnumerateArray())
        {
            var login = String(node, "login");
            if (login.Length > 0)
            {
                var name = String(node, "name");
                people.Add(new RepoPerson(login, name.Length > 0 ? name : null));
            }
        }

        return people.OrderBy(p => p.Name ?? p.Login, StringComparer.OrdinalIgnoreCase).ToList();
    }

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
            Int(node, "additions"),
            Int(node, "deletions"),
            Int(node, "changedFiles"));
    }

    private static string String(JsonElement node, string name)
        => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int Int(JsonElement node, string name)
        => node.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
}

// Pull requests through the GitHub CLI: it already owns auth, enterprise
// hosts, and which remote is "the" GitHub repo (`gh repo set-default`).
public sealed class GitHubCliPullRequestSource(string root) : IPullRequestSource
{
    private const int TimeoutMs = 30_000;
    private const int FetchTimeoutMs = 180_000;

    private string? _repo;

    public Task<PullRequestPage> SearchAsync(PullRequestQuery query, string? after, int pageSize, CancellationToken ct)
        => Task.Run(() =>
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
        }, ct);

    public Task<IReadOnlyList<RepoPerson>> GetPeopleAsync(CancellationToken ct)
        => Task.Run(() =>
        {
            var (owner, name) = Split(Repo(ct));
            return PullRequestSearch.ParsePeople(Gh(
                ct,
                "api", "graphql",
                "-f", "query=query($owner:String!,$name:String!){repository(owner:$owner,name:$name){mentionableUsers(first:100){nodes{login name}}}}",
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
        => Task.Run(() => PullRequestRanges.Resolve(root, pullRequest, () => Repo(ct), FetchTimeoutMs, ct), ct);

    private string Repo(CancellationToken ct)
        => _repo ??= Gh(ct, "repo", "view", "--json", "nameWithOwner", "-q", ".nameWithOwner").Trim();

    private static (string Owner, string Name) Split(string repo)
    {
        var slash = repo.IndexOf('/');
        return slash > 0 ? (repo[..slash], repo[(slash + 1)..]) : (repo, repo);
    }

    private string Gh(CancellationToken ct, params string[] args)
    {
        if (ToolProcess.TryRun("gh", root, TimeoutMs, ct, args, out var stdout, out var stderr))
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
