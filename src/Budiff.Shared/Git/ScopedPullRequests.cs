using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using LibGit2Sharp;

namespace Budiff.Git;

// What the scoped PR search needs from GitHub; the GitHub CLI source
// implements it (tests fake it).
public interface IPullRequestApi
{
    // Plain GitHub search (state, author, text): the PRs, not their files.
    PullRequestPage Search(PullRequestQuery query, string? after, int pageSize, CancellationToken ct);

    // The PRs each commit came from, by SHA (none for a direct push or a
    // commit GitHub does not know).
    IReadOnlyDictionary<string, IReadOnlyList<PullRequestInfo>> PullRequestsOf(IReadOnlyList<string> commits, CancellationToken ct);

    // The files each PR changes (repository paths); null when it changes too
    // many to list.
    IReadOnlyDictionary<int, IReadOnlyList<string>?> FilesOf(IReadOnlyList<int> numbers, CancellationToken ct);

    // The branch PRs merge into, as something git can walk ("origin/main").
    string HistoryRef(CancellationToken ct);
}

// The PR picker scoped to one folder (project) of a big repository: only PRs
// that change something under it, found without reading every PR the
// repository ever had.
// - Merged PRs (anyone, no search text) come from the folder's own history:
//   the default branch's first-parent commits that touch it, local and fast.
//   GitHub is only asked which PR each of those commits was, a slice at a
//   time, as the list scrolls - the cost follows the folder, not the repo.
// - Open and closed PRs, and any search by author or text, page through
//   GitHub's search (which narrows them already) and keep the PRs whose
//   merge-base -> head range changes the folder. That is decided locally when
//   both ends are here (the folder's tree id at each end), else from the PR's
//   file list.
// With anyone and no text, All lists the open PRs first, then the merged
// ones; closed-unmerged PRs only under Closed. A page returns as soon as one
// request found something - the picker asks for more while its list is short,
// so rows show after one round trip rather than after a full page's worth.
public sealed class ScopedPullRequestSearch
{
    private const int ScanPageSize = 50;
    private const int HistorySlice = 40;

    // GitHub requests one page may make while it finds nothing (the picker
    // asks again as the list scrolls, or while it is still short).
    private const int RequestBudget = 4;

    private readonly string _root;
    private readonly string _prefix;
    private readonly IPullRequestApi _api;

    // Searches may overlap (one superseded while it finishes, a search
    // ahead), so what they learn is kept in thread-safe places.
    private readonly ConcurrentDictionary<string, IReadOnlyList<PullRequestInfo>> _pullsOfCommit = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(int Number, string Head), bool> _touches = new();
    private volatile string? _canonicalPrefix;
    private volatile FolderHistory? _history;

    // root: anywhere in the repository; scopePrefix: the folder, from the
    // repository root ('/'-separated, any letter case).
    public ScopedPullRequestSearch(string root, string scopePrefix, IPullRequestApi api)
    {
        _root = root;
        _prefix = scopePrefix.Replace('\\', '/').Trim('/');
        _api = api;
    }

    public string Scope => _prefix;

    public PullRequestPage Search(PullRequestQuery query, string? after, int pageSize, CancellationToken ct)
    {
        var phases = Phases(query);
        var (phase, inner) = ParseCursor(after);
        var items = new List<PullRequestInfo>();
        var seen = new HashSet<int>();
        var budget = RequestBudget;
        while (phase < phases.Count && items.Count == 0 && budget-- > 0)
        {
            ct.ThrowIfCancellationRequested();
            string? next;
            if (phases[phase] is { } scan)
            {
                var page = _api.Search(scan, inner, ScanPageSize, ct);
                foreach (var pr in Touching(page.Items, ct).Where(pr => seen.Add(pr.Number)))
                {
                    items.Add(pr);
                }

                next = page.HasNextPage ? page.EndCursor : null;
            }
            else
            {
                var commits = History(ct);
                var start = inner is null ? 0 : int.Parse(inner, CultureInfo.InvariantCulture);
                var slice = commits.Skip(start).Take(HistorySlice).ToList();
                var pulls = PullRequestsOf(slice, ct);
                foreach (var sha in slice)
                {
                    foreach (var pr in pulls.GetValueOrDefault(sha) ?? Array.Empty<PullRequestInfo>())
                    {
                        if (pr.State == "MERGED" && seen.Add(pr.Number))
                        {
                            items.Add(pr);
                        }
                    }
                }

                var end = start + slice.Count;
                next = end < commits.Count ? end.ToString(CultureInfo.InvariantCulture) : null;
            }

            if (next is null)
            {
                phase++;
                inner = null;
            }
            else
            {
                inner = next;
            }
        }

        var more = phase < phases.Count;
        return new PullRequestPage(items, more ? $"{phase}:{inner}" : null, more, TotalCount: -1);
    }

    internal bool InScope(string path)
        => path.Equals(_prefix, StringComparison.OrdinalIgnoreCase)
            || (path.Length > _prefix.Length && path[_prefix.Length] == '/' && path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase));

    // Which lists make up the answer, in order; null = the folder's merged
    // history, else a GitHub search to filter.
    private static IReadOnlyList<PullRequestQuery?> Phases(PullRequestQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Author) || query.Text.Trim().Length > 0)
        {
            return new PullRequestQuery?[] { query };
        }

        return query.State switch
        {
            PullRequestStateFilter.Open => new PullRequestQuery?[] { query },
            PullRequestStateFilter.Closed => new PullRequestQuery?[] { query },
            PullRequestStateFilter.Merged => new PullRequestQuery?[] { null },
            _ => new PullRequestQuery?[] { query with { State = PullRequestStateFilter.Open }, null },
        };
    }

    // "phase:inner" - inner is a GitHub cursor or a history index.
    private static (int Phase, string? Inner) ParseCursor(string? cursor)
    {
        if (cursor is null)
        {
            return (0, null);
        }

        var colon = cursor.IndexOf(':');
        var phase = int.Parse(cursor[..colon], CultureInfo.InvariantCulture);
        var inner = cursor[(colon + 1)..];
        return (phase, inner.Length > 0 ? inner : null);
    }

    private IEnumerable<PullRequestInfo> Touching(IReadOnlyList<PullRequestInfo> prs, CancellationToken ct)
    {
        var verdicts = new Dictionary<int, bool>();
        var unknown = new List<PullRequestInfo>();
        using (var repo = OpenRepository())
        {
            foreach (var pr in prs)
            {
                if (_touches.TryGetValue((pr.Number, pr.HeadRefOid), out var known))
                {
                    verdicts[pr.Number] = known;
                }
                else if (repo is not null && TouchesLocally(repo, pr) is { } local)
                {
                    verdicts[pr.Number] = _touches[(pr.Number, pr.HeadRefOid)] = local;
                }
                else
                {
                    unknown.Add(pr);
                }
            }
        }

        if (unknown.Count > 0)
        {
            var files = _api.FilesOf(unknown.Select(pr => pr.Number).ToList(), ct);
            foreach (var pr in unknown)
            {
                // Too many files to list (or no answer): keep it rather than lose it.
                var touches = files.GetValueOrDefault(pr.Number) is not { } paths || paths.Any(InScope);
                verdicts[pr.Number] = _touches[(pr.Number, pr.HeadRefOid)] = touches;
            }
        }

        return prs.Where(pr => verdicts.GetValueOrDefault(pr.Number));
    }

    // Did merge-base -> head change the folder? Null when either end is not
    // in this clone (a fork's PR, or not fetched yet).
    private bool? TouchesLocally(Repository repo, PullRequestInfo pr)
    {
        var head = Commit(repo, pr.HeadRefOid);
        var start = Commit(repo, pr.BaseRefOid) ?? Commit(repo, HistoryRefOrHead(repo));
        if (head is null || start is null)
        {
            return null;
        }

        var mergeBase = repo.ObjectDatabase.FindMergeBase(start, head) ?? start;
        var prefix = CanonicalPrefix(repo);
        return !Equals(head[prefix]?.Target.Id, mergeBase[prefix]?.Target.Id);
    }

    private string HistoryRefOrHead(Repository repo)
    {
        try
        {
            return _api.HistoryRef(CancellationToken.None);
        }
        catch (Exception)
        {
            return repo.Head.Tip?.Sha ?? string.Empty;
        }
    }

    // The folder's merged history: first-parent commits of the default branch
    // that touch it, newest first; kept until that branch moves.
    private IReadOnlyList<string> History(CancellationToken ct)
    {
        var reference = _api.HistoryRef(ct);
        using var repo = OpenRepository();
        if (repo is null || Commit(repo, reference) is not { } tip)
        {
            return Array.Empty<string>();
        }

        if (_history is { } cached && cached.Tip == tip.Sha)
        {
            return cached.Commits;
        }

        var commits = GitCli.TryRun(repo.Info.WorkingDirectory, out var log, ct, "log", "--first-parent", "--format=%H", tip.Sha, "--", CanonicalPrefix(repo))
            ? log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        _history = new FolderHistory(tip.Sha, commits);
        return commits;
    }

    private sealed record FolderHistory(string Tip, IReadOnlyList<string> Commits);

    private IReadOnlyDictionary<string, IReadOnlyList<PullRequestInfo>> PullRequestsOf(IReadOnlyList<string> commits, CancellationToken ct)
    {
        var missing = commits.Where(c => !_pullsOfCommit.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            var found = _api.PullRequestsOf(missing, ct);
            foreach (var sha in missing)
            {
                _pullsOfCommit[sha] = found.GetValueOrDefault(sha) ?? Array.Empty<PullRequestInfo>();
            }
        }

        return _pullsOfCommit;
    }

    // The folder as the repository spells it: the scope may come from a path
    // typed in another letter case (Windows), and git paths are exact.
    private string CanonicalPrefix(Repository repo)
    {
        if (_canonicalPrefix is not null)
        {
            return _canonicalPrefix;
        }

        var tree = repo.Head.Tip?.Tree;
        var parts = new List<string>();
        foreach (var segment in _prefix.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = tree?.FirstOrDefault(e => e.Name.Equals(segment, StringComparison.Ordinal))
                ?? tree?.FirstOrDefault(e => e.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            parts.Add(entry?.Name ?? segment);
            tree = entry?.Target as Tree;
        }

        return _canonicalPrefix = string.Join('/', parts);
    }

    private Repository? OpenRepository()
    {
        try
        {
            return Repository.Discover(_root) is { } path ? new Repository(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Commit? Commit(Repository repo, string spec)
    {
        if (spec.Length == 0)
        {
            return null;
        }

        try
        {
            return repo.Lookup<Commit>(spec);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
