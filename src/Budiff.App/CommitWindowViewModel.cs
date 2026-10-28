using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Budiff.Git;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Budiff.App;

// The small commit window a blame click opens: who, when and why on the left,
// the files it changed (as a folder tree) on the right. Expanding opens a full
// budiff window on the commit.
public sealed partial class CommitWindowViewModel : ObservableObject
{
    private readonly IPullRequestApi? _pullRequests;

    // pullRequests: where to ask which PR the commit came from (GitHub);
    // null shows none.
    public CommitWindowViewModel(GitRepositoryScope scope, CommitDetails commit, IPullRequestApi? pullRequests = null)
    {
        Scope = scope;
        Commit = commit;
        _pullRequests = pullRequests;
    }

    public GitRepositoryScope Scope { get; }

    public CommitDetails Commit { get; }

    public string Title => $"{Commit.ShortSha} {Commit.Subject}";

    public string AuthorText => Commit.Email.Length > 0 ? $"{Commit.Author} <{Commit.Email}>" : Commit.Author;

    public string DateText => Commit.When.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) + "  (" + Ago(Commit.When) + ")";

    public string ParentText => Commit.ParentCount switch
    {
        0 => "First commit: everything in it is new.",
        1 => "Parent " + Short(Commit.ParentSha!),
        _ => $"Merge of {Commit.ParentCount} parents - shown against the first, {Short(Commit.ParentSha!)}",
    };

    // The body without its trailing "Co-authored-by: Name <email>" lines,
    // which read better as people (Trailers) than as wrapped raw text.
    public string MessageBody => SplitTrailers(Commit.Body).Body;

    public bool HasBody => MessageBody.Length > 0;

    // The body reflowed to the window: messages are hard-wrapped at ~72
    // columns, which wraps again (raggedly) in a narrower pane.
    public IReadOnlyList<CommitParagraph> Paragraphs => Reflow(MessageBody);

    // Lines of a paragraph join into one; "- ", "* " and "1. " start a list
    // item, whose indented lines continue it (shown with a hanging indent);
    // a paragraph indented 4+ spaces is code and keeps its lines.
    internal static IReadOnlyList<CommitParagraph> Reflow(string body)
    {
        var result = new List<CommitParagraph>();
        string? bullet = null;
        var indent = 0;
        var text = new List<string>();
        var code = false;
        var afterBlank = true;

        void Close()
        {
            if (text.Count > 0)
            {
                result.Add(new CommitParagraph(string.Join(code ? "\n" : " ", text), bullet, indent, code, afterBlank && result.Count > 0));
                afterBlank = false;
            }

            text.Clear();
            bullet = null;
            indent = 0;
            code = false;
        }

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                Close();
                afterBlank = true;
                continue;
            }

            if (ListItem().Match(line) is { Success: true } item)
            {
                Close();
                bullet = item.Groups[2].Value is "-" or "*" or "+" ? "•" : item.Groups[2].Value;
                indent = item.Groups[1].Value.Length;
                text.Add(item.Groups[3].Value);
            }
            else if (text.Count == 0)
            {
                code = line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t');
                text.Add(code ? line : line.Trim());
            }
            else
            {
                text.Add(code ? line : line.Trim());
            }
        }

        Close();
        return result;
    }

    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    public IReadOnlyList<CommitTrailer> Trailers => SplitTrailers(Commit.Body).Trailers;

    public bool HasTrailers => Trailers.Count > 0;

    // Trailers are the message's last paragraph when every line in it reads
    // "Key: Name <email>"; anything else stays in the body as written.
    internal static (string Body, IReadOnlyList<CommitTrailer> Trailers) SplitTrailers(string body)
    {
        var text = body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var cut = text.LastIndexOf("\n\n", StringComparison.Ordinal);
        var last = cut < 0 ? text : text[(cut + 2)..];
        var trailers = new List<CommitTrailer>();
        foreach (var line in last.Split('\n'))
        {
            if (CommitTrailer.TryParse(line.Trim()) is not { } trailer)
            {
                return (text, Array.Empty<CommitTrailer>());
            }

            trailers.Add(trailer);
        }

        return (cut < 0 ? string.Empty : text[..cut].Trim(), trailers);
    }

    [ObservableProperty]
    private IReadOnlyList<CommitFileNode> _files = Array.Empty<CommitFileNode>();

    [ObservableProperty]
    private string _filesSummary = "Loading files...";

    // The PR the commit is part of (any state), once GitHub answers; null
    // for a direct push, no GitHub remote, or no gh.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPullRequest), nameof(PullRequestText), nameof(PullRequestTip), nameof(PullRequestBrush))]
    private PullRequestInfo? _pullRequest;

    public bool HasPullRequest => PullRequest is not null;

    // Just the number: the pill's colour is the state (GitHub's colours).
    public string PullRequestText => PullRequest is { } pr ? "#" + pr.Number : string.Empty;

    public string PullRequestTip => PullRequest is { } pr ? $"{pr.StateLabel} - {pr.Title}\n{pr.Url}" : string.Empty;

    public IBrush? PullRequestBrush => PullRequest is { } pr ? PullRequestRowVm.BrushFor(pr.StateLabel) : null;

    public async Task LoadAsync()
    {
        var pullRequest = LoadPullRequestAsync();
        var changes = await Task.Run(() => GitRepositoryReader.ListCommitChanges(Scope, Commit));
        var files = changes.Files;
        Files = BuildTree(files);
        var added = files.Sum(f => f.Churn.Added);
        var removed = files.Sum(f => f.Churn.Removed);
        var churn = (added > 0 ? $"   +{added:N0}" : string.Empty) + (removed > 0 ? $"  −{removed:N0}" : string.Empty);
        FilesSummary = files.Count == 0
            ? "No file changes."
            : $"{files.Count:N0} {(files.Count == 1 ? "file" : "files")} changed{churn}";
        await pullRequest;
    }

    private async Task LoadPullRequestAsync()
    {
        if (_pullRequests is not { } api)
        {
            return;
        }

        try
        {
            var found = await Task.Run(() => api.PullRequestsOf(new[] { Commit.Sha }, CancellationToken.None));
            PullRequest = found.TryGetValue(Commit.Sha, out var pulls) ? Pick(pulls) : null;
        }
        catch (Exception)
        {
            // No gh, not signed in, offline: the window just shows no PR.
        }
    }

    // The PR that landed it when one did; else an open PR it is still
    // waiting in; else the most recently active (closed) one.
    internal static PullRequestInfo? Pick(IReadOnlyList<PullRequestInfo> pulls)
        => pulls.FirstOrDefault(p => p.State == "MERGED")
           ?? pulls.OrderByDescending(p => p.State == "OPEN").ThenByDescending(p => p.UpdatedAt).FirstOrDefault();

    // Folders first, then files, each by name; a folder with a single folder
    // inside reads as one row ("src/app/reports").
    internal static IReadOnlyList<CommitFileNode> BuildTree(IReadOnlyList<ChangedFile> files)
    {
        var root = new CommitFileNode(string.Empty, null, string.Empty);
        foreach (var file in files)
        {
            var parts = file.Path.Split('/');
            var node = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                node = node.Folder(parts[i]);
            }

            node.Children.Add(new CommitFileNode(parts[^1], file, file.Path));
        }

        root.Finish();
        return root.Children;
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        return span.TotalDays >= 365 ? Plural((int)(span.TotalDays / 365), "year")
            : span.TotalDays >= 30 ? Plural((int)(span.TotalDays / 30), "month")
            : span.TotalDays >= 1 ? Plural((int)span.TotalDays, "day")
            : span.TotalHours >= 1 ? Plural((int)span.TotalHours, "hour")
            : "just now";
    }

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? string.Empty : "s")} ago";
}

// One row of the changed-files tree: a folder, or a file with its churn.
public sealed class CommitFileNode
{
    internal CommitFileNode(string name, ChangedFile? file, string path)
    {
        Name = name;
        File = file;
        Path = path;
    }

    // Repository-relative: the file's path, or the folder's.
    public string Path { get; }

    public string Name { get; private set; }

    public ChangedFile? File { get; }

    public bool IsFile => File is not null;

    public List<CommitFileNode> Children { get; } = new();

    public AppIconKind Icon => File is null ? AppIconKind.FolderOutline : FileIcons.For(Name).Kind;

    public IBrush? IconTint => File is null ? null : FileIcons.For(Name).Tint;

    public string AddedText => File is { Churn.Added: > 0 } f ? "+" + f.Churn.Added.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;

    public string RemovedText => File is { Churn.Removed: > 0 } f ? "−" + f.Churn.Removed.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;

    // New / deleted / renamed; plain edits say nothing.
    public string StateText => File?.State switch
    {
        GitFileState.Added => "new",
        GitFileState.Deleted => "deleted",
        GitFileState.Renamed => "renamed",
        _ => string.Empty,
    };

    public string Tip => File is { } f
        ? f.OldPath is { } old && old != f.Path ? $"{old} → {f.Path}" : f.Path
        : Name;

    internal CommitFileNode Folder(string name)
    {
        var folder = Children.FirstOrDefault(c => !c.IsFile && c.Name == name);
        if (folder is null)
        {
            folder = new CommitFileNode(name, null, Path.Length == 0 ? name : Path + "/" + name);
            Children.Add(folder);
        }

        return folder;
    }

    internal void Finish()
    {
        foreach (var child in Children)
        {
            child.Finish();
        }

        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            while (!child.IsFile && child.Children is [{ IsFile: false } only])
            {
                only.Name = child.Name + "/" + only.Name;
                child = only;
            }

            Children[i] = child;
        }

        Children.Sort((a, b) => a.IsFile != b.IsFile
            ? a.IsFile ? 1 : -1
            : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
    }
}

// One "Key: Name <email>" trailer line, e.g. "Co-authored-by: Ada <ada@x.org>".
// One reflowed block of a commit message: a paragraph, a list item (Bullet
// set, Indent in leading spaces) or a code block kept as written.
public sealed record CommitParagraph(string Text, string? Bullet, int Indent, bool IsCode, bool AfterBlank)
{
    public bool HasBullet => Bullet is not null;

    // A blank line above reads as paragraph spacing; list items sit closer.
    public Thickness Margin => new(Indent * 8, AfterBlank ? 10 : 3, 0, 0);
}

public sealed record CommitTrailer(string Label, string Name, string Email)
{
    public string EmailText => "<" + Email + ">";

    internal static CommitTrailer? TryParse(string line)
    {
        var colon = line.IndexOf(':');
        var open = line.LastIndexOf('<');
        if (colon <= 0 || open <= colon || !line.EndsWith('>') || line[..colon].Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }

        var name = line[(colon + 1)..open].Trim();
        var email = line[(open + 1)..^1].Trim();
        return name.Length == 0 || email.Length == 0 ? null : new CommitTrailer(Readable(line[..colon]), name, email);
    }

    // "Co-Authored-By" -> "Co-authored by".
    private static string Readable(string key)
    {
        var words = key.Replace('-', ' ').ToLowerInvariant()
            .Replace("co authored", "co-authored", StringComparison.Ordinal)
            .Replace("signed off", "signed-off", StringComparison.Ordinal);
        return char.ToUpperInvariant(words[0]) + words[1..];
    }
}
