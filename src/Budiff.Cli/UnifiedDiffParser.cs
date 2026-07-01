using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Budiff;
using Budiff.Git;

internal static partial class UnifiedDiffParser
{
    private static readonly Regex HunkHeaderPattern = BuildHunkHeaderPattern();

    public static IReadOnlyList<UnifiedDiffFile> Parse(string text)
    {
        var files = new List<UnifiedDiffFile>();
        FileBuilder? current = null;

        foreach (var line in SplitLines(text))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                AddCurrent();
                current = new FileBuilder(ParseDiffGitPath(line));
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                current ??= new FileBuilder("stdin.diff");
                current.SetOldPath(ParseFileMarker(line[4..]));
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                current ??= new FileBuilder("stdin.diff");
                current.SetNewPath(ParseFileMarker(line[4..]));
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line.StartsWith("new file mode ", StringComparison.Ordinal))
            {
                current.State = GitFileState.Added;
                continue;
            }

            if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
            {
                current.State = GitFileState.Deleted;
                continue;
            }

            if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                current.SetOldPath(NormalizeDiffPath(line["rename from ".Length..]));
                current.State = GitFileState.Renamed;
                continue;
            }

            if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                current.SetNewPath(NormalizeDiffPath(line["rename to ".Length..]));
                current.State = GitFileState.Renamed;
                continue;
            }

            if (line.StartsWith("Binary files ", StringComparison.Ordinal))
            {
                current.MarkBinary();
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                current.StartHunk(line);
                continue;
            }

            current.AddHunkLine(line);
        }

        AddCurrent();
        return files;

        void AddCurrent()
        {
            if (current is null)
            {
                return;
            }

            var parsed = current.Build();
            if (parsed is not null)
            {
                files.Add(parsed);
            }
        }
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }

    private static string ParseDiffGitPath(string line)
    {
        var rest = line["diff --git ".Length..].Trim();
        var newPathMarker = " b/";
        var markerIndex = rest.IndexOf(newPathMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            return NormalizeDiffPath(rest[(markerIndex + 1)..]);
        }

        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? NormalizeDiffPath(parts[^1]) : "stdin.diff";
    }

    private static string ParseFileMarker(string text)
    {
        var path = text.Trim();
        var tabIndex = path.IndexOf('\t');
        if (tabIndex >= 0)
        {
            path = path[..tabIndex];
        }

        return NormalizeDiffPath(path);
    }

    private static string NormalizeDiffPath(string path)
    {
        path = path.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            path = path[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (path.Equals("/dev/null", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)
            ? path[2..]
            : path;
    }

    [GeneratedRegex(@"^@@ -(?<old>\d+)(?:,\d+)? \+(?<new>\d+)(?:,\d+)? @@", RegexOptions.CultureInvariant)]
    private static partial Regex BuildHunkHeaderPattern();

    private sealed class FileBuilder
    {
        private readonly List<DiffRow> _rows = new();
        private readonly List<PendingLine> _removed = new();
        private readonly List<PendingLine> _added = new();
        private int _oldLine;
        private int _newLine;
        private bool _hasMetadata;
        private bool _inHunk;
        private bool _isBinary;

        public FileBuilder(string path)
        {
            Path = path;
        }

        public string Path { get; private set; }

        public string? OldPath { get; private set; }

        public GitFileState State { get; set; } = GitFileState.Modified;

        public void SetOldPath(string path)
        {
            _hasMetadata = true;
            if (path.Length == 0)
            {
                State = GitFileState.Added;
                return;
            }

            OldPath = path;
            if (Path.Length == 0 || Path == "stdin.diff")
            {
                Path = path;
            }
        }

        public void SetNewPath(string path)
        {
            _hasMetadata = true;
            if (path.Length == 0)
            {
                State = GitFileState.Deleted;
                Path = OldPath ?? Path;
                return;
            }

            Path = path;
            if (OldPath is not null && !OldPath.Equals(Path, StringComparison.OrdinalIgnoreCase))
            {
                State = GitFileState.Renamed;
            }
        }

        public void MarkBinary()
        {
            _hasMetadata = true;
            _isBinary = true;
        }

        public void StartHunk(string header)
        {
            FlushPending();

            var match = HunkHeaderPattern.Match(header);
            if (!match.Success)
            {
                _inHunk = false;
                return;
            }

            _oldLine = int.Parse(match.Groups["old"].Value);
            _newLine = int.Parse(match.Groups["new"].Value);
            _inHunk = true;
        }

        public void AddHunkLine(string line)
        {
            if (!_inHunk || line.Length == 0)
            {
                return;
            }

            if (line[0] == '\\')
            {
                return;
            }

            var content = line.Length == 1 ? string.Empty : line[1..];
            switch (line[0])
            {
                case ' ':
                    FlushPending();
                    _rows.Add(new DiffRow(_oldLine++, content, _newLine++, content, DiffKind.Unchanged));
                    break;
                case '-':
                    _removed.Add(new PendingLine(_oldLine++, content));
                    break;
                case '+':
                    _added.Add(new PendingLine(_newLine++, content));
                    break;
            }
        }

        public UnifiedDiffFile? Build()
        {
            FlushPending();
            if (!_isBinary && _rows.Count == 0)
            {
                if (!_hasMetadata)
                {
                    return null;
                }

                var placeholderFile = BuildChangedFile(isBinary: false, new DiffChurn(0, 0));
                return new UnifiedDiffFile(placeholderFile, DiffResult.Message("No text changes in unified diff."));
            }

            var diff = _isBinary ? DiffResult.Message("Binary file not shown.") : DiffResult.FromRows(_rows);
            var churn = diff.Rows is { } rows ? DiffMetrics.Churn(rows) : new DiffChurn(0, 0);
            var file = BuildChangedFile(_isBinary, churn);
            return new UnifiedDiffFile(file, diff);
        }

        private ChangedFile BuildChangedFile(bool isBinary, DiffChurn churn)
        {
            var oldPath = OldPath is not null && !OldPath.Equals(Path, StringComparison.OrdinalIgnoreCase) ? OldPath : null;
            return new ChangedFile(Path, oldPath, Path, State, churn, AuthorCount: 0, CommitCount: 0, IsBinary: isBinary);
        }

        private void FlushPending()
        {
            var paired = Math.Min(_removed.Count, _added.Count);
            for (var i = 0; i < paired; i++)
            {
                _rows.Add(new DiffRow(_removed[i].LineNumber, _removed[i].Text, _added[i].LineNumber, _added[i].Text, DiffKind.Modified));
            }

            foreach (var line in _removed.Skip(paired))
            {
                _rows.Add(new DiffRow(line.LineNumber, line.Text, null, null, DiffKind.Removed));
            }

            foreach (var line in _added.Skip(paired))
            {
                _rows.Add(new DiffRow(null, null, line.LineNumber, line.Text, DiffKind.Added));
            }

            _removed.Clear();
            _added.Clear();
        }
    }

    private sealed record PendingLine(int LineNumber, string Text);
}

internal sealed record UnifiedDiffFile(ChangedFile File, DiffResult Diff);
