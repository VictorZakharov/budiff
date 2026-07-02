using System.Text.Json;
using System.Text.Json.Nodes;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Cli.Tests;

[Collection("GitRepositoryReader environment")]
public sealed class McpServerTests
{
    [Fact]
    public void Parse_AcceptsMcpSubcommand()
    {
        var options = CliOptions.Parse(new[] { "mcp" });

        Assert.True(options.Mcp);
        Assert.Null(options.Error);
    }

    [Fact]
    public void Run_InitializesAndListsTools()
    {
        var input = string.Join('\n', new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
        });

        var responses = Run(input, Directory.GetCurrentDirectory());

        Assert.Equal("2025-06-18", responses[0].RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        var tools = responses[1].RootElement.GetProperty("result").GetProperty("tools");
        Assert.Contains(tools.EnumerateArray(), tool => tool.GetProperty("name").GetString() == "list_changes");
        Assert.Contains(tools.EnumerateArray(), tool => tool.GetProperty("name").GetString() == "get_file_diff");
        Assert.Contains(tools.EnumerateArray(), tool => tool.GetProperty("name").GetString() == "repo_overview");
    }

    [Fact]
    public void CliRun_McpSubcommandUsesStdioServer()
    {
        var input = new StringReader("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = BudiffCli.Run(new[] { "mcp" }, input, output, error, inputRedirected: true);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal("budiff", response.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public void ListChanges_ReturnsStructuredChangeList()
    {
        using var repo = TempRepo.Create();
        repo.Write("tracked.txt", "one\n");
        repo.Commit("initial", "Alice");
        repo.Write("tracked.txt", "one\ntwo\n");

        var input = ToolCall(1, "list_changes", $$"""
            {
              "path": {{JsonSerializer.Serialize(repo.Root)}},
              "gitRoot": true
            }
            """);

        var response = Assert.Single(Run(input, repo.Root));
        var content = ToolText(response);
        using var doc = JsonDocument.Parse(content);

        Assert.Equal("budiff.changeList", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal("tracked.txt", doc.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void GetFileDiff_ReturnsStructuredFileDiff()
    {
        using var repo = TempRepo.Create();
        repo.Write("tracked.txt", "one\n");
        repo.Commit("initial", "Alice");
        repo.Write("tracked.txt", "one\ntwo\n");

        var input = ToolCall(1, "get_file_diff", $$"""
            {
              "root": {{JsonSerializer.Serialize(repo.Root)}},
              "path": "tracked.txt",
              "gitRoot": true
            }
            """);

        var response = Assert.Single(Run(input, repo.Root));
        var content = ToolText(response);
        using var doc = JsonDocument.Parse(content);

        Assert.Equal("budiff.fileDiff", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal("tracked.txt", doc.RootElement.GetProperty("file").GetProperty("path").GetString());
        Assert.Contains(doc.RootElement.GetProperty("rows").EnumerateArray(), row => row.GetProperty("newText").GetString() == "two");
    }

    [Fact]
    public void GetFileDiff_CollapsesUnchangedRunsByDefault()
    {
        using var repo = TempRepo.Create();
        var lines = Enumerable.Range(1, 21).Select(i => "line" + i);
        repo.Write("big.txt", string.Join('\n', lines) + "\n");
        repo.Commit("initial", "Alice");
        repo.Write("big.txt", "changed\n" + string.Join('\n', lines.Skip(1)) + "\n");

        var collapsed = ToolCall(1, "get_file_diff", $$"""
            {
              "root": {{JsonSerializer.Serialize(repo.Root)}},
              "path": "big.txt",
              "gitRoot": true
            }
            """);

        var response = Assert.Single(Run(collapsed, repo.Root));
        using var doc = JsonDocument.Parse(ToolText(response));

        // 22 source rows (the trailing newline adds an empty final row):
        // Modified(1) + 3 context + one gap standing in for the other 18.
        Assert.Equal(3, doc.RootElement.GetProperty("contextLines").GetInt32());
        var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(5, rows.Length);
        var gap = Assert.Single(rows, r => r.GetProperty("kind").GetString() == "Gap");
        Assert.Equal(18, gap.GetProperty("hiddenLines").GetInt32());

        var full = ToolCall(2, "get_file_diff", $$"""
            {
              "root": {{JsonSerializer.Serialize(repo.Root)}},
              "path": "big.txt",
              "gitRoot": true,
              "contextLines": null
            }
            """);

        var fullResponse = Assert.Single(Run(full, repo.Root));
        using var fullDoc = JsonDocument.Parse(ToolText(fullResponse));
        Assert.Equal(22, fullDoc.RootElement.GetProperty("rows").GetArrayLength());
        Assert.DoesNotContain(
            fullDoc.RootElement.GetProperty("rows").EnumerateArray(),
            r => r.GetProperty("kind").GetString() == "Gap");
    }

    [Fact]
    public void RepoOverview_ReturnsCodeAnalysisSummary()
    {
        using var repo = TempRepo.Create();
        repo.Write("src/App.cs", "class App\n{\n}\n");
        repo.Commit("initial", "Alice");
        repo.Write("src/App.cs", "class App\n{\n    void Run() {}\n}\n");
        repo.Commit("update", "Bob");

        var input = ToolCall(1, "repo_overview", $$"""
            {
              "path": {{JsonSerializer.Serialize(repo.Root)}},
              "gitRoot": true,
              "periodDays": null,
              "maxRows": 20
            }
            """);

        var response = Assert.Single(Run(input, repo.Root));
        var content = ToolText(response);
        using var doc = JsonDocument.Parse(content);

        Assert.Equal("budiff.repoOverview", doc.RootElement.GetProperty("format").GetString());
        Assert.True(doc.RootElement.GetProperty("summary").GetProperty("fileCount").GetInt32() >= 1);
        Assert.Contains(doc.RootElement.GetProperty("rows").EnumerateArray(), row => row.GetProperty("path").GetString() == "src/App.cs");
    }

    private static List<JsonDocument> Run(string input, string defaultPath)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var server = new McpServer(new StringReader(input), output, error, defaultPath);

        Assert.Equal(0, server.Run());
        Assert.Equal(string.Empty, error.ToString());

        return output.ToString()
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToList();
    }

    private static string ToolCall(int id, string name, string arguments)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = JsonNode.Parse(arguments),
            },
        }.ToJsonString();

    private static string ToolText(JsonDocument response)
    {
        var result = response.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        return result.GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private sealed class TempRepo : IDisposable
    {
        private readonly Repository _repo;

        private TempRepo(string root, Repository repo)
        {
            Root = root;
            _repo = repo;
        }

        public string Root { get; }

        public static TempRepo Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "budiff-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Repository.Init(root);
            var repo = new Repository(root);
            repo.Config.Set("core.autocrlf", false);
            return new TempRepo(root, repo);
        }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public string Commit(string message, string author)
        {
            Commands.Stage(_repo, "*");
            var removed = _repo.RetrieveStatus().Removed.Select(entry => entry.FilePath).ToArray();
            if (removed.Length > 0)
            {
                Commands.Stage(_repo, removed);
            }

            var sig = new Signature(author, author.ToLowerInvariant() + "@example.test", DateTimeOffset.UtcNow);
            return _repo.Commit(message, sig, sig).Sha;
        }

        public void Dispose()
        {
            _repo.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temp repos locked by native handles.
            }
        }
    }
}
