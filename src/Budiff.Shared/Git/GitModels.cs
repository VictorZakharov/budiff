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

// One commit, as the commit window shows it. ParentSha is the first parent
// (null for a root commit); the commit is shown against it.
public sealed record CommitDetails(string Sha, string? ParentSha, int ParentCount, string Author, string Email, System.DateTimeOffset When, string Message)
{
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;

    public string BaseSha => ParentSha ?? GitHistory.EmptyTreeSha;

    public string Subject => Message.Split('\n', 2)[0].Trim();

    public string Body => Message.Split('\n', 2) is [_, var rest] ? rest.Trim() : string.Empty;

    public DiffTarget Target => new(BaseSha, Sha, HeadIsWorkingTree: false);
}

public sealed record GitReferenceOption(string Id, string Display, bool IsWorkingTree = false);

// One blamed line: the commit that last touched it on the blamed revision.
public sealed record BlameLineInfo(string ShortSha, string Author, string Email, System.DateTimeOffset When, string Message);

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
