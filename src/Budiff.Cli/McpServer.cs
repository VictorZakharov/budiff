using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Budiff;
using Budiff.CodeAnalysis;
using Budiff.Git;

internal sealed class McpServer
{
    private const string ProtocolVersion = "2025-06-18";

    private static readonly JsonSerializerOptions LineJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ContentJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly string _defaultPath;

    public McpServer(TextReader input, TextWriter output, TextWriter error, string defaultPath)
    {
        _input = input;
        _output = output;
        _error = error;
        _defaultPath = Path.GetFullPath(defaultPath);
    }

    public int Run()
    {
        string? line;
        while ((line = _input.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ProcessLine(line);
        }

        return 0;
    }

    private void ProcessLine(string line)
    {
        JsonNode? id = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                WriteError(null, -32600, "Invalid Request");
                return;
            }

            id = TryCloneId(root);
            if (!root.TryGetProperty("method", out var methodElement))
            {
                if (id is not null)
                {
                    WriteError(id, -32600, "Invalid Request");
                }

                return;
            }

            if (methodElement.ValueKind != JsonValueKind.String)
            {
                if (id is not null)
                {
                    WriteError(id, -32600, "Invalid Request");
                }

                return;
            }

            if (id is null)
            {
                return;
            }

            var method = methodElement.GetString() ?? string.Empty;
            var result = method switch
            {
                "initialize" => Initialize(root),
                "ping" => new JsonObject(),
                "tools/list" => ToolsList(),
                "tools/call" => ToolsCall(root),
                _ => throw new JsonRpcException(-32601, "Method not found"),
            };

            WriteResult(id, result);
        }
        catch (JsonException)
        {
            WriteError(null, -32700, "Parse error");
        }
        catch (JsonRpcException ex)
        {
            WriteError(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            _error.WriteLine(ex.Message);
            WriteError(id, -32603, "Internal error");
        }
    }

    private JsonNode Initialize(JsonElement root)
    {
        var requested = TryGetObject(root, "params") is { } parameters
            ? GetString(parameters, "protocolVersion")
            : null;

        return new JsonObject
        {
            ["protocolVersion"] = string.IsNullOrWhiteSpace(requested) ? ProtocolVersion : requested,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject
                {
                    ["listChanged"] = false,
                },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "budiff",
                ["title"] = "budiff",
                ["version"] = Version(),
            },
            ["instructions"] = "Use list_changes first, then get_file_diff for selected files. Use repo_overview for a compact repository summary.",
        };
    }

    private static JsonNode ToolsList()
        => new JsonObject
        {
            ["tools"] = new JsonArray
            {
                Tool(
                    "list_changes",
                    "List Changed Files",
                    "Return budiff's structured changed-file listing for a folder scope or whole repository.",
                    new JsonObject
                    {
                        ["path"] = Schema("string", "Folder or file path used to discover the git repository. Defaults to the server working directory."),
                        ["gitRoot"] = Schema("boolean", "When true, widen scope to the repository root."),
                        ["baseRef"] = Schema("string", "Base git ref for comparison. Defaults to HEAD."),
                        ["headRef"] = NullableStringSchema("Head git ref. Omit or set null to compare against the working tree."),
                    }),
                Tool(
                    "get_file_diff",
                    "Get File Diff",
                    "Return budiff's structured side-by-side diff model for one changed file.",
                    new JsonObject
                    {
                        ["path"] = Schema("string", "Changed file path, either repository-relative, scope-relative, or absolute."),
                        ["root"] = Schema("string", "Folder or file path used to discover the git repository. Defaults to the server working directory."),
                        ["gitRoot"] = Schema("boolean", "When true, widen scope to the repository root before matching the file."),
                        ["baseRef"] = Schema("string", "Base git ref for comparison. Defaults to HEAD."),
                        ["headRef"] = NullableStringSchema("Head git ref. Omit or set null to compare against the working tree."),
                    },
                    "path"),
                Tool(
                    "repo_overview",
                    "Repository Overview",
                    "Return a compact Code Analysis bird's-eye summary with line counts, churn, commits, authors, and top rows.",
                    new JsonObject
                    {
                        ["path"] = Schema("string", "Folder or file path used to discover the git repository. Defaults to the server working directory."),
                        ["gitRoot"] = Schema("boolean", "When true, widen scope to the repository root."),
                        ["periodDays"] = NullableIntegerSchema("History window in days. Omit for 30 days; set null or 0 for all time."),
                        ["grouping"] = EnumSchema(new[] { "Folders", "Projects" }, "Tree grouping mode. Defaults to Folders."),
                        ["sort"] = EnumSchema(new[] { "Name", "Lines", "Code", "Blank", "Comment", "Files", "Added", "Removed", "Commits", "Authors" }, "Sort column. Defaults to Lines."),
                        ["ascending"] = Schema("boolean", "Sort ascending instead of descending."),
                        ["maxRows"] = Schema("integer", "Maximum tree rows to return. Defaults to 200."),
                    }),
            },
        };

    private JsonNode ToolsCall(JsonElement root)
    {
        var parameters = TryGetObject(root, "params")
            ?? throw new JsonRpcException(-32602, "tools/call requires params.");
        var name = GetString(parameters, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new JsonRpcException(-32602, "tools/call requires params.name.");
        }

        var arguments = TryGetObject(parameters, "arguments") ?? default;
        try
        {
            var text = name switch
            {
                "list_changes" => ListChanges(arguments),
                "get_file_diff" => GetFileDiff(arguments),
                "repo_overview" => RepoOverview(arguments),
                _ => throw new ToolException("Unknown tool '" + name + "'."),
            };

            return ToolResult(text, isError: false);
        }
        catch (ToolException ex)
        {
            return ToolResult(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            // Unexpected failure: keep internals (paths, libgit2 details) out
            // of the tool result; the detail goes to stderr for diagnosis.
            _error.WriteLine($"tools/call {name} failed: {ex}");
            return ToolResult("Tool '" + name + "' failed unexpectedly.", isError: true);
        }
    }

    private string ListChanges(JsonElement arguments)
    {
        var scope = OpenScope(arguments);
        var target = Target(arguments);
        var changes = GitRepositoryReader.ListChanges(scope, target);
        return JsonRenderer.SerializeList(changes);
    }

    private string GetFileDiff(JsonElement arguments)
    {
        var requestedPath = GetString(arguments, "path");
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            throw new ToolException("get_file_diff requires a path argument.");
        }

        var startPath = GetString(arguments, "root") ?? GetString(arguments, "scope");
        var scope = OpenScope(
            arguments,
            startPath ?? (Path.IsPathRooted(requestedPath) ? requestedPath : _defaultPath),
            usePathArgument: false);
        var target = Target(arguments);
        var changes = GitRepositoryReader.ListChanges(scope, target);
        var file = FindChangedFile(changes, scope, requestedPath);
        if (file is null)
        {
            throw new ToolException("No changed file matched '" + requestedPath + "'.");
        }

        var diff = GitRepositoryReader.LoadDiff(scope, file, target);
        return JsonRenderer.SerializeDiff(changes, file, diff);
    }

    private string RepoOverview(JsonElement arguments)
    {
        var periodDays = PeriodDays(arguments);
        var grouping = EnumArg(arguments, "grouping", AnalysisGrouping.Folders);
        var sort = EnumArg(arguments, "sort", AnalysisSortKey.Lines);
        var ascending = GetBool(arguments, "ascending") ?? false;
        var maxRows = Math.Clamp(GetInt(arguments, "maxRows") ?? 200, 1, 1000);
        var scope = OpenScope(arguments);
        var snapshot = CodeAnalysisRepositoryReader.Load(scope.ScopeRoot, periodDays);
        var root = AnalysisTreeBuilder.Build(
            snapshot.Scan,
            snapshot.History,
            grouping,
            sort,
            ascending,
            allTime: !periodDays.HasValue);

        var dto = new McpRepoOverviewDto(
            "budiff.repoOverview",
            scope.RepositoryRoot,
            JsonScopeDto.From(scope),
            periodDays,
            grouping.ToString(),
            sort.ToString(),
            new McpRepoOverviewSummaryDto(
                root.FileCount,
                McpLineCountsDto.From(root.Lines),
                snapshot.Scan.Truncated,
                snapshot.History is not null,
                root.Added,
                root.Removed,
                root.CommitCount,
                root.AuthorCount),
            AnalysisNode.Flatten(root.Children)
                .Take(maxRows)
                .Select(row => McpRepoOverviewRowDto.From(row.Node, row.Depth))
                .ToArray());

        return JsonSerializer.Serialize(dto, ContentJsonOptions);
    }

    private GitRepositoryScope OpenScope(JsonElement arguments, string? fallbackPath = null, bool usePathArgument = true)
    {
        var path = (usePathArgument ? GetString(arguments, "path") : null)
            ?? GetString(arguments, "root")
            ?? GetString(arguments, "scope")
            ?? fallbackPath
            ?? _defaultPath;
        return GitRepositoryReader.Open(path, GetBool(arguments, "gitRoot") ?? false);
    }

    private static DiffTarget Target(JsonElement arguments)
    {
        var baseRef = GetString(arguments, "baseRef") ?? GetString(arguments, "base") ?? "HEAD";
        var headRef = GetString(arguments, "headRef") ?? GetString(arguments, "head");
        if (string.IsNullOrWhiteSpace(headRef))
        {
            headRef = null;
        }

        return new DiffTarget(baseRef, headRef, headRef is null);
    }

    private int? PeriodDays(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("periodDays", out var period)
            && period.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var days = GetInt(arguments, "periodDays");
        if (days is null)
        {
            return 30;
        }

        return days > 0 ? days : null;
    }

    private ChangedFile? FindChangedFile(ChangeSet changes, GitRepositoryScope scope, string requestedPath)
    {
        var candidates = CandidateRepoPaths(scope, requestedPath)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return changes.Files.FirstOrDefault(file =>
            candidates.Any(candidate =>
                file.Path.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                || (file.OldPath?.Equals(candidate, StringComparison.OrdinalIgnoreCase) ?? false)
                || file.Path.EndsWith("/" + candidate, StringComparison.OrdinalIgnoreCase)));
    }

    private IEnumerable<string> CandidateRepoPaths(GitRepositoryScope scope, string requestedPath)
    {
        var normalized = NormalizeRepoPath(requestedPath);
        yield return normalized;

        if (scope.ScopePrefix.Length > 0 && !normalized.StartsWith(scope.ScopePrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            yield return scope.ScopePrefix + "/" + normalized;
        }

        var rootedPath = Path.IsPathRooted(requestedPath)
            ? requestedPath
            : Path.GetFullPath(requestedPath, _defaultPath);
        if (TryRepoRelative(scope.RepositoryRoot, rootedPath, out var repoRelative))
        {
            yield return repoRelative;
        }

        var scopeRelativePath = Path.IsPathRooted(requestedPath)
            ? requestedPath
            : Path.GetFullPath(requestedPath, scope.ScopeRoot);
        if (TryRepoRelative(scope.RepositoryRoot, scopeRelativePath, out var scopeRelative))
        {
            yield return scopeRelative;
        }
    }

    private static JsonNode Tool(string name, string title, string description, JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required.Select(r => JsonValue.Create(r)).ToArray<JsonNode?>());
        }

        return new JsonObject
        {
            ["name"] = name,
            ["title"] = title,
            ["description"] = description,
            ["inputSchema"] = schema,
        };
    }

    private static JsonObject Schema(string type, string description)
        => new()
        {
            ["type"] = type,
            ["description"] = description,
        };

    private static JsonObject NullableStringSchema(string description)
        => new()
        {
            ["type"] = new JsonArray("string", "null"),
            ["description"] = description,
        };

    private static JsonObject NullableIntegerSchema(string description)
        => new()
        {
            ["type"] = new JsonArray("integer", "null"),
            ["description"] = description,
        };

    private static JsonObject EnumSchema(IEnumerable<string> values, string description)
        => new()
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(values.Select(v => JsonValue.Create(v)).ToArray<JsonNode?>()),
            ["description"] = description,
        };

    private static JsonNode ToolResult(string text, bool isError)
        => new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            },
            ["isError"] = isError,
        };

    private void WriteResult(JsonNode id, JsonNode result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        };
        Write(response);
    }

    private void WriteError(JsonNode? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JsonValue.Create((string?)null),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        Write(response);
    }

    private void Write(JsonObject response)
    {
        _output.WriteLine(response.ToJsonString(LineJsonOptions));
        _output.Flush();
    }

    private static JsonNode? TryCloneId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var id))
        {
            return null;
        }

        return JsonNode.Parse(id.GetRawText());
    }

    private static JsonElement? TryGetObject(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return value;
    }

    private static string? GetString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool? GetBool(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static int? GetInt(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }

    private static TEnum EnumArg<TEnum>(JsonElement arguments, string name, TEnum fallback)
        where TEnum : struct
    {
        var value = GetString(arguments, name);
        return value is not null && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
    }

    private static bool TryRepoRelative(string repositoryRoot, string fullPath, out string relativePath)
    {
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(fullPath);
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = NormalizeRepoPath(Path.GetRelativePath(root, full));
        return relativePath != ".";
    }

    private static string NormalizeRepoPath(string path)
    {
        // User-supplied tool input: also shed whitespace and "./" prefixes
        // before the shared repo-path normalization.
        var normalized = path.Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return RepoPaths.Normalize(normalized);
    }

    private static string Version()
        => typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(McpServer).Assembly.GetName().Version?.ToString()
            ?? "0.1.0";

    private sealed class JsonRpcException : Exception
    {
        public JsonRpcException(int code, string message)
            : base(message)
        {
            Code = code;
        }

        public int Code { get; }
    }

    private sealed class ToolException : Exception
    {
        public ToolException(string message)
            : base(message)
        {
        }
    }
}

internal sealed record McpRepoOverviewDto(
    string Format,
    string RepositoryRoot,
    JsonScopeDto Scope,
    int? PeriodDays,
    string Grouping,
    string Sort,
    McpRepoOverviewSummaryDto Summary,
    IReadOnlyList<McpRepoOverviewRowDto> Rows);

internal sealed record McpRepoOverviewSummaryDto(
    int FileCount,
    McpLineCountsDto Lines,
    bool Truncated,
    bool HasHistory,
    long Added,
    long Removed,
    int CommitCount,
    int AuthorCount);

internal sealed record McpRepoOverviewRowDto(
    string Name,
    string Path,
    string Kind,
    int Depth,
    bool IsFile,
    bool IsBinary,
    int FileCount,
    McpLineCountsDto Lines,
    long Added,
    long Removed,
    int CommitCount,
    int AuthorCount)
{
    public static McpRepoOverviewRowDto From(AnalysisNode node, int depth)
        => new(
            node.Name,
            node.RelativePath,
            node.Kind.ToString(),
            depth,
            node.IsFile,
            node.IsBinary,
            node.FileCount,
            McpLineCountsDto.From(node.Lines),
            node.Added,
            node.Removed,
            node.CommitCount,
            node.AuthorCount);
}

internal sealed record McpLineCountsDto(int Total, int Code, int Blank, int Comment)
{
    public static McpLineCountsDto From(LineCounts counts)
        => new(counts.Total, counts.Code, counts.Blank, counts.Comment);
}
