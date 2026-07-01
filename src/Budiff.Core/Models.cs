namespace Budiff;

public enum DiffKind
{
    Unchanged,
    Added,
    Removed,
    Modified,
}

public enum GitFileState
{
    None,
    Untracked,
    Modified,
    Added,
    Deleted,
    Renamed,
    Conflicted,
}

public sealed record DiffRow(int? OldNo, string? OldText, int? NewNo, string? NewText, DiffKind Kind);

public sealed record DiffResult(string? Placeholder, IReadOnlyList<DiffRow>? Rows)
{
    public static DiffResult FromRows(IReadOnlyList<DiffRow> rows) => new(null, rows);

    public static DiffResult Message(string text) => new(text, null);
}

public sealed record BlobInfo(string Text, long Bytes, bool IsBinary)
{
    public static readonly BlobInfo Empty = new(string.Empty, 0, IsBinary: false);
}

public sealed record DiffChurn(int Added, int Removed)
{
    public int Total => Added + Removed;
}

public enum InlineChangeKind
{
    Unchanged,
    Added,
    Removed,
}

public sealed record InlineSegment(string Text, InlineChangeKind Kind);

public sealed record InlineDiffPair(IReadOnlyList<InlineSegment> Old, IReadOnlyList<InlineSegment> New);
