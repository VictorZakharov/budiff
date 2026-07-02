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
            "--format=%x1f%H%x1f%aN",
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
            ApplyWindowNetDiff(root, windowDays, result, ct);
        }

        return result;
    }

    public static HistoryResult Parse(string log)
    {
        var result = new HistoryResult();
        var commitIndex = -1;
        var author = string.Empty;

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

            var tab1 = line.IndexOf('\t');
            if (tab1 < 0)
            {
                continue;
            }

            var tab2 = line.IndexOf('\t', tab1 + 1);
            if (tab2 < 0)
            {
                continue;
            }

            var path = ResolveRenamePath(line[(tab2 + 1)..].Trim());
            if (path.Length == 0)
            {
                continue;
            }

            var entry = result.ForPath(path);
            entry.Added += ParseCount(line[..tab1]);
            entry.Removed += ParseCount(line[(tab1 + 1)..tab2]);
            entry.Commits.Add(commitIndex);
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
    // revision - used for the diff's NEW side when the base has nothing to
    // blame (empty-tree base / file absent there). LibGit2Sharp cannot blame
    // the working tree, so this shells out to `git blame --porcelain`, which
    // also marks uncommitted lines (all-zero sha). Indexed by 0-based line.
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
            else if (line.StartsWith("summary ", StringComparison.Ordinal))
            {
                meta[sha] = current with { Summary = line[8..] };
            }
        }

        return lines;
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

    private static void ApplyWindowNetDiff(string root, int days, HistoryResult result, CancellationToken ct)
    {
        var baseRef = FindWindowBaseCommit(root, days);
        if (!GitCli.TryRun(root, out var diff, ct, "-c", "core.quotePath=false", "diff", "--relative", "--numstat", "--no-renames", baseRef, "HEAD", "--", "."))
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
            var tab1 = line.IndexOf('\t');
            if (tab1 < 0)
            {
                continue;
            }

            var tab2 = line.IndexOf('\t', tab1 + 1);
            if (tab2 < 0)
            {
                continue;
            }

            var path = NormalizePath(line[(tab2 + 1)..].Trim());
            if (path.Length == 0)
            {
                continue;
            }

            var entry = result.ForPath(path);
            entry.Added = ParseCount(line[..tab1]);
            entry.Removed = ParseCount(line[(tab1 + 1)..tab2]);
        }
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

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/').Trim();
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
}

internal static class GitCli
{
    private const int TimeoutMs = 60_000;

    public static bool TryRun(string workingDir, out string stdout, params string[] args)
        => TryRun(workingDir, out stdout, CancellationToken.None, args);

    public static bool TryRun(string workingDir, out string stdout, CancellationToken ct, params string[] args)
    {
        stdout = string.Empty;
        var psi = new ProcessStartInfo("git")
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

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            // Drain BOTH pipes off-thread: reading stdout inline would block
            // forever when git fills the un-read stderr pipe (and that block
            // would also keep the timeout below from ever firing).
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(TimeoutMs))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort process cleanup.
                }

                return false;
            }

            var output = stdoutTask.GetAwaiter().GetResult();
            stderrTask.GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();
            if (proc.ExitCode != 0)
            {
                return false;
            }

            stdout = output;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}
