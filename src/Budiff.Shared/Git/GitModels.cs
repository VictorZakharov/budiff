using Budiff;

namespace Budiff.Git;

public sealed record GitRepositoryScope(
    string RepositoryRoot,
    string ScopeRoot,
    string ScopePrefix,
    bool IsGitRootScope,
    string? Branch);

public sealed record DiffTarget(string BaseRef, string? HeadRef, bool HeadIsWorkingTree)
{
    public static DiffTarget WorkingTree { get; } = new("HEAD", null, HeadIsWorkingTree: true);

    public string Display => HeadIsWorkingTree ? $"{BaseRef} vs working tree" : $"{BaseRef} vs {HeadRef}";
}

public sealed record GitReferenceOption(string Id, string Display, bool IsWorkingTree = false);

public sealed record ChangedFile(
    string Path,
    string? OldPath,
    string FullPath,
    GitFileState State,
    DiffChurn Churn,
    int AuthorCount,
    int CommitCount,
    bool IsBinary)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string StateText => State switch
    {
        GitFileState.Untracked => "??",
        GitFileState.Added => "A",
        GitFileState.Deleted => "D",
        GitFileState.Renamed => "R",
        GitFileState.Conflicted => "!",
        GitFileState.Modified => "M",
        _ => " ",
    };
}

public sealed record ChangeSet(
    GitRepositoryScope Scope,
    DiffTarget Target,
    IReadOnlyList<ChangedFile> Files);

internal sealed class PathHistory
{
    public HashSet<int> Commits { get; } = new();

    public HashSet<string> Authors { get; } = new(StringComparer.OrdinalIgnoreCase);
}
