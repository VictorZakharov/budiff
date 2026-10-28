namespace Budiff.App;

// What the open diff pane shows for a folder picked in the bird's-eye: the
// folder's change counts for the applied period and its changed files, from
// the numbers the bird's-eye already has (no git call). Public with plain
// properties: the window binds to it by reflection (CompiledBindings off).
public sealed class FolderSummary
{
    // Changed files listed at most; the rest are counted in MoreText.
    internal const int MaxListed = 200;

    public required string Path { get; init; }

    // "Last month", "PR #12 ...", "Commit abc1234 ...".
    public required string Period { get; init; }

    public int Files { get; init; }

    public int Changed { get; init; }

    public int New { get; init; }

    public int Modified { get; init; }

    public int Deleted { get; init; }

    public long LinesAdded { get; init; }

    public long LinesRemoved { get; init; }

    // False outside git: there is nothing to count changes from.
    public bool HasHistory { get; init; }

    public IReadOnlyList<FolderSummaryFile> ChangedFiles { get; init; } = new List<FolderSummaryFile>();

    public string FilesText => Count(Files, "file");

    public string ChangedText => Changed == 1 ? "1 file changed" : $"{Changed:N0} files changed";

    public string NewText => $"{New:N0} new";

    public string ModifiedText => $"{Modified:N0} modified";

    public string DeletedText => $"{Deleted:N0} deleted";

    public string LinesAddedText => "+" + LinesAdded.ToString("N0");

    public string LinesRemovedText => "-" + LinesRemoved.ToString("N0");

    public bool HasChanges => Changed > 0;

    public bool IsEmpty => !HasChanges;

    public string EmptyText => HasHistory
        ? "Nothing changed here in this period."
        : "No git history: nothing to compare.";

    public string MoreText => Changed > ChangedFiles.Count ? $"and {Changed - ChangedFiles.Count:N0} more" : string.Empty;

    public bool HasMore => Changed > ChangedFiles.Count;

    // The same counts as plain lines: the placeholder a floating diff window
    // shows (it has no room for the panel).
    public string PlainText => !HasHistory || !HasChanges
        ? Path + "\n\n" + EmptyText
        : $"{Path}\n\n{ChangedText} of {FilesText}: {NewText}, {ModifiedText}, {DeletedText}\n{LinesAddedText} {LinesRemovedText} lines";

    private static string Count(int n, string noun) => n == 1 ? "1 " + noun : $"{n:N0} {noun}s";
}

// One changed file of a folder summary; Path is relative to the folder.
public sealed class FolderSummaryFile
{
    public required string Path { get; init; }

    public required FolderFileChange Change { get; init; }

    public long Added { get; init; }

    public long Removed { get; init; }

    // N / M / D, as git status letters them.
    public string Letter => Change switch
    {
        FolderFileChange.New => "N",
        FolderFileChange.Deleted => "D",
        _ => "M",
    };

    public string ChangeTip => Change switch
    {
        FolderFileChange.New => "New",
        FolderFileChange.Deleted => "Deleted",
        _ => "Modified",
    };

    public bool IsNew => Change == FolderFileChange.New;

    public bool IsDeleted => Change == FolderFileChange.Deleted;

    public bool IsModified => Change == FolderFileChange.Modified;

    public string AddedText => Added > 0 ? "+" + Added.ToString("N0") : string.Empty;

    public string RemovedText => Removed > 0 ? "-" + Removed.ToString("N0") : string.Empty;
}

public enum FolderFileChange
{
    Modified,
    New,
    Deleted,
}
