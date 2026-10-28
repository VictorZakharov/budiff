using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Budiff.CodeAnalysis;

namespace Budiff.Git;

public static class GitHistory
{
    private const char Sep = (char)0x1f;

    // git's well-known empty tree: the window base when the whole history sits
    // inside the analysis window (diffing against it shows everything as added).
    public const string EmptyTreeSha = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    public static bool IsRepository(string root)
        => GitCli.TryRun(root, out var output, "rev-parse", "--is-inside-work-tree")
            && output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    public static int CountCommits(string root, int? sinceDays)
    {
        var args = new List<string> { "rev-list", "--count" };
        if (sinceDays is { } days and > 0)
        {
            args.Add("--since=" + days + " days ago");
        }

        args.AddRange(new[] { "HEAD", "--", "." });
        return GitCli.TryRun(root, out var output, args.ToArray()) && int.TryParse(output.Trim(), out var count)
            ? count
            : 0;
    }

    public static IReadOnlyList<string>? ListFiles(string root)
    {
        if (!GitCli.TryRun(root, out var output, "-c", "core.quotePath=false", "ls-files", "-c", "-o", "--exclude-standard", "-z", "--", "."))
        {
            return null;
        }

        return output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizePath)
            .Where(path => path.Length > 0)
            .ToList();
    }

    public static HistoryResult? Load(string root, int? sinceDays, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-c", "core.quotePath=false",
            "log",
            "--relative",
            "--no-renames",
            "--numstat",
            "--no-color",
            "--format=%x1f%H%x1f%aN%x1f%ct",
        };
        if (sinceDays is { } days and > 0)
        {
            args.Add("--since=" + days + " days ago");
        }

        args.AddRange(new[] { "--", "." });
        if (!GitCli.TryRun(root, out var output, ct, args.ToArray()))
        {
            return null;
        }

        var result = Parse(output);
        if (sinceDays is { } windowDays and > 0)
        {
            ApplyNetDiff(root, FindWindowBaseCommit(root, windowDays), "HEAD", result, ct);
        }

        return result;
    }

    // History of a commit range (a pull request: merge-base..head). Commits and
    // authors come from the range's log; Added/Removed are the NET diff between
    // the two ends - what the PR changes, as GitHub's "Files changed" shows it.
    public static HistoryResult? LoadRange(string root, string baseRev, string headRev, CancellationToken ct = default)
    {
        if (!GitCli.TryRun(
                root,
                out var output,
                ct,
                "-c", "core.quotePath=false",
                "log",
                "--relative",
                "--no-renames",
                "--numstat",
                "--no-color",
                "--format=%x1f%H%x1f%aN%x1f%ct",
                // A root commit's base is the empty tree, which log cannot range from.
                baseRev == EmptyTreeSha ? headRev : baseRev + ".." + headRev,
                "--",
                "."))
        {
            return null;
        }

        var result = Parse(output);
        ApplyNetDiff(root, baseRev, headRev, result, ct);
        return result;
    }

    // Paths (relative to root) whose content differs between two revisions -
    // binary changes included, which numstat only reports as "-".
    public static IReadOnlyList<string>? ListChangedFiles(string root, string baseRev, string headRev, CancellationToken ct = default)
    {
        if (!GitCli.TryRun(root, out var output, ct, "-c", "core.quotePath=false", "diff", "--relative", "--name-only", "--no-renames", "-z", baseRev, headRev, "--", "."))
        {
            return null;
        }

        return output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizePath)
            .Where(path => path.Length > 0)
            .ToList();
    }

    public static HistoryResult Parse(string log)
    {
        var result = new HistoryResult();
        var commitIndex = -1;
        var author = string.Empty;
        long when = 0;

        foreach (var line in LineCounter.SplitLines(log))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == Sep)
            {
                var parts = line.Split(Sep);
                commitIndex++;
                author = parts.Length > 2 ? parts[2] : string.Empty;
                when = parts.Length > 3 && long.TryParse(parts[3], out var seconds) ? seconds : 0;
                result.CommitCount = commitIndex + 1;
                if (author.Length > 0)
                {
                    result.AuthorCommits[author] = result.AuthorCommits.GetValueOrDefault(author) + 1;
                }

                continue;
            }

            if (commitIndex < 0)
            {
                continue;
            }

            if (!TryParseNumstat(line, out var added, out var removed, out var path))
            {
                continue;
            }

            var entry = result.ForPath(path);
            entry.Added += added;
            entry.Removed += removed;
            entry.Commits.Add(commitIndex);
            entry.LastChanged = Math.Max(entry.LastChanged, when);
            if (author.Length > 0)
            {
                entry.Authors.Add(author);
            }
        }

        return result;
    }

    public static string ResolveRenamePath(string path)
    {
        var arrow = path.IndexOf(" => ", StringComparison.Ordinal);
        if (arrow < 0)
        {
            return NormalizePath(path);
        }

        var open = path.IndexOf('{');
        var close = path.IndexOf('}');
        if (open >= 0 && close > open && arrow > open && arrow < close)
        {
            var prefix = path[..open];
            var newPart = path[(arrow + 4)..close];
            var suffix = path[(close + 1)..];
            return Collapse(prefix + newPart + suffix);
        }

        return NormalizePath(path[(arrow + 4)..]);
    }

    // The commit the windowed churn diffs from: the last commit OLDER than the
    // window, or the empty tree when the whole history is inside the window.
    // The GUI's period-aligned drill-in uses the same boundary so the diff pane
    // shows exactly what the Added/Removed columns counted.
    public static string FindWindowBaseCommit(string root, int days)
    {
        if (GitCli.TryRun(root, out var shaOutput, "rev-list", "-1", "--before=" + days + " days ago", "HEAD", "--", "."))
        {
            var sha = shaOutput.Trim();
            if (sha.Length > 0)
            {
                return sha;
            }
        }

        return EmptyTreeSha;
    }

    // Blame of the file as it exists in the WORKING TREE (rev = null) or at a
    // revision, from `git blame --porcelain`, which marks uncommitted lines
    // with an all-zero sha. Indexed by 0-based line. path is relative to root.
    public static IReadOnlyList<BlameLineInfo?> BlameFile(string root, string path, string? rev)
    {
        var args = new List<string> { "-c", "core.quotePath=false", "blame", "--porcelain" };
        if (!string.IsNullOrEmpty(rev))
        {
            args.Add(rev);
        }

        args.AddRange(new[] { "--", path });
        return GitCli.TryRun(root, out var output, args.ToArray())
            ? ParsePorcelainBlame(output)
            : Array.Empty<BlameLineInfo?>();
    }

    public static IReadOnlyList<BlameLineInfo?> ParsePorcelainBlame(string output)
    {
        // Porcelain: every line gets a "<sha> <orig> <final> [count]" header;
        // author/summary tags appear only the FIRST time a commit shows up.
        var meta = new Dictionary<string, (string Author, string Email, DateTimeOffset When, string Summary)>();
        var lines = new List<BlameLineInfo?>();
        string? sha = null;
        var finalLine = 0;

        foreach (var line in LineCounter.SplitLines(output))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '\t')
            {
                if (sha is not null && finalLine >= 1)
                {
                    var m = meta.GetValueOrDefault(sha);
                    while (lines.Count < finalLine)
                    {
                        lines.Add(null);
                    }

                    var uncommitted = IsAllZeros(sha);
                    lines[finalLine - 1] = new BlameLineInfo(
                        uncommitted ? "0000000" : sha[..7],
                        uncommitted ? "Not committed yet" : m.Author,
                        uncommitted ? string.Empty : m.Email,
                        m.When,
                        uncommitted ? "uncommitted changes" : m.Summary);
                }

                sha = null;
                continue;
            }

            var space = line.IndexOf(' ');
            var head = space < 0 ? line : line[..space];
            if (head.Length == 40 && IsHex(head))
            {
                sha = head;
                var parts = line.Split(' ');
                finalLine = parts.Length >= 3 && int.TryParse(parts[2], out var fl) ? fl : 0;
                if (!meta.ContainsKey(sha))
                {
                    meta[sha] = (string.Empty, string.Empty, default, string.Empty);
                }

                continue;
            }

            if (sha is null || space < 0)
            {
                continue;
            }

            var current = meta.GetValueOrDefault(sha);
            if (line.StartsWith("author ", StringComparison.Ordinal))
            {
                meta[sha] = current with { Author = line[7..] };
            }
            else if (line.StartsWith("author-mail ", StringComparison.Ordinal))
            {
                meta[sha] = current with { Email = line[12..].Trim('<', '>') };
            }
            else if (line.StartsWith("author-time ", StringComparison.Ordinal) && long.TryParse(line[12..], out var time))
            {
                meta[sha] = current with { When = DateTimeOffset.FromUnixTimeSeconds(time) };
            }
            else if (line.StartsWith("author-tz ", StringComparison.Ordinal) && TryParseZone(line[10..], out var zone))
            {
                // After author-time: the same moment, in the author's zone.
                meta[sha] = current with { When = current.When.ToOffset(zone) };
            }
            else if (line.StartsWith("summary ", StringComparison.Ordinal))
            {
                meta[sha] = current with { Summary = line[8..] };
            }
        }

        return lines;
    }

    // "+0200" / "-0530".
    private static bool TryParseZone(string text, out TimeSpan zone)
    {
        zone = default;
        if (text.Length != 5 || (text[0] != '+' && text[0] != '-')
            || !int.TryParse(text.AsSpan(1, 2), out var hours) || !int.TryParse(text.AsSpan(3, 2), out var minutes))
        {
            return false;
        }

        zone = new TimeSpan(hours, minutes, 0) * (text[0] == '-' ? -1 : 1);
        return true;
    }

    private static bool IsAllZeros(string sha)
    {
        foreach (var c in sha)
        {
            if (c != '0')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHex(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    // Replaces the summed per-commit churn with the net diff between two
    // revisions: a line added then removed inside the range counts as nothing.
    private static void ApplyNetDiff(string root, string baseRev, string headRev, HistoryResult result, CancellationToken ct)
    {
        if (!GitCli.TryRun(root, out var diff, ct, "-c", "core.quotePath=false", "diff", "--relative", "--numstat", "--no-renames", baseRev, headRev, "--", "."))
        {
            return;
        }

        foreach (var history in result.ByPath.Values)
        {
            history.Added = 0;
            history.Removed = 0;
        }

        foreach (var line in LineCounter.SplitLines(diff))
        {
            if (!TryParseNumstat(line, out var added, out var removed, out var path))
            {
                continue;
            }

            var entry = result.ForPath(path);
            entry.Added = added;
            entry.Removed = removed;
        }
    }

    // One "added<TAB>removed<TAB>path" numstat line ("-" counts for binary
    // files parse as 0). Both git invocations pass --no-renames, but tolerate
    // rename arrows anyway so a caller without the flag still resolves.
    private static bool TryParseNumstat(string line, out long added, out long removed, out string path)
    {
        added = 0;
        removed = 0;
        path = string.Empty;
        var tab1 = line.IndexOf('\t');
        if (tab1 < 0)
        {
            return false;
        }

        var tab2 = line.IndexOf('\t', tab1 + 1);
        if (tab2 < 0)
        {
            return false;
        }

        path = ResolveRenamePath(line[(tab2 + 1)..].Trim());
        if (path.Length == 0)
        {
            return false;
        }

        added = ParseCount(line[..tab1]);
        removed = ParseCount(line[(tab1 + 1)..tab2]);
        return true;
    }

    private static long ParseCount(string text) => long.TryParse(text, out var count) ? count : 0;

    private static string Collapse(string path)
    {
        while (path.Contains("//", StringComparison.Ordinal))
        {
            path = path.Replace("//", "/", StringComparison.Ordinal);
        }

        return NormalizePath(path);
    }

    private static string NormalizePath(string path) => RepoPaths.Normalize(path);
}

public sealed record CodeAnalysisSnapshot(ScanResult Scan, HistoryResult? History);

public static class CodeAnalysisRepositoryReader
{
    public static CodeAnalysisSnapshot Load(string root, int? sinceDays, CancellationToken ct = default)
    {
        var hasGit = GitHistory.IsRepository(root);
        var files = hasGit ? GitHistory.ListFiles(root) : null;
        var scan = CodeScanner.Scan(root, files, hasGit, ct: ct);
        var history = scan.HasGit ? GitHistory.Load(root, sinceDays, ct) : null;
        return new CodeAnalysisSnapshot(scan, history);
    }

    // A pull request's view: only the files it changes, counted as they are
    // at the PR head (a file the PR deletes lists with zero lines), with the
    // range's commits/authors and net churn. Independent of the working tree,
    // so an un-checked-out PR reads the same as the current branch.
    public static CodeAnalysisSnapshot LoadPullRequest(string root, PullRequestRange range, CancellationToken ct = default)
        => LoadRange(root, range.BaseSha, range.HeadSha, $"PR #{range.PullRequest.Number}", ct);

    // One commit: what it changed against its (first) parent.
    public static CodeAnalysisSnapshot LoadCommit(string root, CommitDetails commit, CancellationToken ct = default)
        => LoadRange(root, commit.BaseSha, commit.Sha, "commit " + commit.ShortSha, ct);

    private static CodeAnalysisSnapshot LoadRange(string root, string baseSha, string headSha, string what, CancellationToken ct)
    {
        var history = GitHistory.LoadRange(root, baseSha, headSha, ct)
            ?? throw new InvalidOperationException($"Could not read the history of {what}.");
        var changed = GitHistory.ListChangedFiles(root, baseSha, headSha, ct) ?? Array.Empty<string>();
        var files = GitRepositoryReader.CountAtRevision(root, headSha, changed, ct);
        var scan = new ScanResult { Root = root, Files = files, HasGit = true };
        return new CodeAnalysisSnapshot(scan, history);
    }
}

internal static class GitCli
{
    private const int TimeoutMs = 60_000;

    public static bool TryRun(string workingDir, out string stdout, params string[] args)
        => TryRun(workingDir, out stdout, CancellationToken.None, args);

    public static bool TryRun(string workingDir, out string stdout, CancellationToken ct, params string[] args)
        => ToolProcess.TryRun("git", workingDir, TimeoutMs, ct, args, out stdout, out _);

    // For commands whose exit code means something (symbolic-ref: 1 =
    // detached HEAD, 128 = not a repository).
    public static int Run(string workingDir, out string stdout, out string stderr, params string[] args)
        => ToolProcess.Run("git", workingDir, TimeoutMs, CancellationToken.None, args, out stdout, out stderr);

    // Just the exit code (diff --quiet: 0 = same, 1 = differs).
    public static int ExitCode(string workingDir, CancellationToken ct, params string[] args)
        => ToolProcess.Run("git", workingDir, TimeoutMs, ct, args, out _, out _);
}

// One hardened child-process runner for the external tools budiff shells out
// to (git, gh): both pipes drained, a timeout that actually fires, the whole
// tree killed on timeout. False on a missing tool, timeout, or non-zero exit;
// stderr comes back either way so callers can surface the tool's own reason.
internal static class ToolProcess
{
    public static bool TryRun(
        string fileName,
        string workingDir,
        int timeoutMs,
        CancellationToken ct,
        IReadOnlyList<string> args,
        out string stdout,
        out string stderr)
    {
        var ok = Run(fileName, workingDir, timeoutMs, ct, args, out stdout, out stderr) == 0;
        if (!ok)
        {
            stdout = string.Empty;
        }

        return ok;
    }

    // The tool's exit code, or -1 when it could not start or timed out.
    public static int Run(
        string fileName,
        string workingDir,
        int timeoutMs,
        CancellationToken ct,
        IReadOnlyList<string> args,
        out string stdout,
        out string stderr)
    {
        stdout = string.Empty;
        stderr = string.Empty;
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = Directory.Exists(workingDir) ? workingDir : Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // A viewer skips git's optional locks (status refreshing the index),
        // so it never holds index.lock while the user runs git.
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return -1;
            }

            // Drain BOTH pipes off-thread: reading stdout inline would block
            // forever when the tool fills the un-read stderr pipe (and that block
            // would also keep the timeout below from ever firing).
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            // Cancelled: nobody wants the answer - stop the tool now rather
            // than wait it out (a superseded search's gh, say).
            using var cancel = ct.Register(() =>
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // It may have just exited.
                }
            });
            if (!proc.WaitForExit(timeoutMs))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort process cleanup.
                }

                stderr = $"{fileName} timed out after {timeoutMs / 1000}s.";
                return -1;
            }

            stdout = stdoutTask.GetAwaiter().GetResult();
            stderr = stderrTask.GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();
            return proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception)
        {
            stderr = $"'{fileName}' was not found on PATH.";
            return -1;
        }
        catch (Exception ex)
        {
            stderr = ex.Message;
            return -1;
        }
    }
}
