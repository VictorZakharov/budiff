using System;
using System.Globalization;
using Budiff.CodeAnalysis;

namespace Budiff.App;

// One entry of the summary line's commit picker: a commit of the applied
// period (subject, short sha, author, age, lines), or the "All" entry that
// goes back to the whole period.
public sealed class CommitChoice
{
    public bool IsAll { get; init; }

    public string Sha { get; init; } = string.Empty;

    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;

    public string Subject { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    // How long ago, as the Recency column says it ("5min", "3hr", "2d").
    public string AgeText { get; init; } = string.Empty;

    // The commit's date and time, for the tooltip.
    public string WhenText { get; init; } = string.Empty;

    public string AddedText { get; init; } = string.Empty;

    public string RemovedText { get; init; } = string.Empty;

    // The view is scoped to this entry now (the commit, or All in the period).
    public bool IsCurrent { get; init; }

    public bool IsCommit => !IsAll;

    internal static CommitChoice From(HistoryCommit commit, DateTimeOffset now, bool current) => new()
    {
        Sha = commit.Sha,
        Subject = commit.Subject.Length > 0 ? commit.Subject : "(no message)",
        Author = commit.Author,
        AgeText = CodeAnalysisPanelViewModel.Age(commit.When, now),
        WhenText = commit.When > 0 ? DateTimeOffset.FromUnixTimeSeconds(commit.When).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) : string.Empty,
        AddedText = "+" + commit.Added.ToString("N0", CultureInfo.CurrentCulture),
        RemovedText = "\u2212" + commit.Removed.ToString("N0", CultureInfo.CurrentCulture),
        IsCurrent = current,
    };
}
