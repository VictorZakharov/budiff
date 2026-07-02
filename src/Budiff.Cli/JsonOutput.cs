using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Budiff;
using Budiff.Git;

internal static class JsonRenderer
{
    private static readonly BudiffJsonContext JsonContext = new(new JsonSerializerOptions(BudiffJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static void RenderList(ChangeSet changeSet, TextWriter output)
        => output.WriteLine(SerializeList(changeSet));

    public static void RenderDiff(ChangeSet changeSet, ChangedFile file, DiffResult diff, TextWriter output)
        => output.WriteLine(SerializeDiff(changeSet, file, diff));

    public static string SerializeList(ChangeSet changeSet)
    {
        var dto = JsonChangeListDto.From(changeSet);
        return JsonSerializer.Serialize(dto, JsonContext.JsonChangeListDto);
    }

    public static string SerializeDiff(ChangeSet changeSet, ChangedFile file, DiffResult diff)
    {
        var dto = JsonFileDiffDto.From(changeSet, file, diff);
        return JsonSerializer.Serialize(dto, JsonContext.JsonFileDiffDto);
    }
}

internal sealed record JsonChangeListDto(
    string Format,
    string RepositoryRoot,
    JsonScopeDto Scope,
    JsonTargetDto Target,
    IReadOnlyList<JsonChangedFileDto> Files)
{
    public static JsonChangeListDto From(ChangeSet changeSet)
        => new(
            "budiff.changeList",
            changeSet.Scope.RepositoryRoot,
            JsonScopeDto.From(changeSet.Scope),
            JsonTargetDto.From(changeSet.Target),
            changeSet.Files.Select(JsonChangedFileDto.From).ToArray());
}

internal sealed record JsonFileDiffDto(
    string Format,
    string RepositoryRoot,
    JsonScopeDto Scope,
    JsonTargetDto Target,
    JsonChangedFileDto File,
    string? Placeholder,
    IReadOnlyList<JsonDiffRowDto>? Rows)
{
    public static JsonFileDiffDto From(ChangeSet changeSet, ChangedFile file, DiffResult diff)
        => new(
            "budiff.fileDiff",
            changeSet.Scope.RepositoryRoot,
            JsonScopeDto.From(changeSet.Scope),
            JsonTargetDto.From(changeSet.Target),
            JsonChangedFileDto.From(file),
            diff.Rows is null ? diff.Placeholder ?? "Nothing to show." : null,
            diff.Rows?.Select(JsonDiffRowDto.From).ToArray());
}

internal sealed record JsonScopeDto(
    string Root,
    string Prefix,
    bool IsGitRootScope,
    string? Branch)
{
    public static JsonScopeDto From(GitRepositoryScope scope)
        => new(scope.ScopeRoot, scope.ScopePrefix, scope.IsGitRootScope, scope.Branch);
}

internal sealed record JsonTargetDto(
    string BaseRef,
    string? HeadRef,
    bool HeadIsWorkingTree,
    string Display)
{
    public static JsonTargetDto From(DiffTarget target)
        => new(target.BaseRef, target.HeadRef, target.HeadIsWorkingTree, target.Display);
}

internal sealed record JsonChangedFileDto(
    string Path,
    string? OldPath,
    string State,
    JsonChurnDto Churn,
    int AuthorCount,
    int CommitCount,
    bool IsBinary)
{
    public static JsonChangedFileDto From(ChangedFile file)
        => new(
            file.Path,
            file.OldPath,
            file.State.ToString(),
            JsonChurnDto.From(file.Churn),
            file.AuthorCount,
            file.CommitCount,
            file.IsBinary);
}

internal sealed record JsonChurnDto(int Added, int Removed, int Total)
{
    public static JsonChurnDto From(DiffChurn churn) => new(churn.Added, churn.Removed, churn.Total);
}

internal sealed record JsonDiffRowDto(
    int? OldNo,
    string? OldText,
    int? NewNo,
    string? NewText,
    string Kind,
    IReadOnlyList<JsonInlineSegmentDto>? OldInline,
    IReadOnlyList<JsonInlineSegmentDto>? NewInline)
{
    public static JsonDiffRowDto From(DiffRow row)
    {
        var inline = row.Kind == DiffKind.Modified
            ? InlineDiff.Compute(row.OldText, row.NewText)
            : null;

        return new JsonDiffRowDto(
            row.OldNo,
            row.OldText,
            row.NewNo,
            row.NewText,
            row.Kind.ToString(),
            inline?.Old.Select(JsonInlineSegmentDto.From).ToArray(),
            inline?.New.Select(JsonInlineSegmentDto.From).ToArray());
    }
}

internal sealed record JsonInlineSegmentDto(string Text, string Kind)
{
    public static JsonInlineSegmentDto From(InlineSegment segment)
        => new(segment.Text, segment.Kind.ToString());
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonChangeListDto))]
[JsonSerializable(typeof(JsonFileDiffDto))]
[JsonSerializable(typeof(McpRepoOverviewDto))]
internal sealed partial class BudiffJsonContext : JsonSerializerContext;
