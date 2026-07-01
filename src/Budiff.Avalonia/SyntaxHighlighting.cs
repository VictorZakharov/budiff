using System.Collections.Generic;
using global::Avalonia.Media;

namespace Budiff.Avalonia;

public sealed record DiffRun(string Text, IBrush Brush);

public sealed record SyntaxHighlightResult(IReadOnlyList<DiffRun> Runs, object? State);

public interface ISyntaxHighlightSession
{
    SyntaxHighlightResult HighlightLine(string text, object? state);
}

public interface ISyntaxHighlighter
{
    ISyntaxHighlightSession? CreateSession(string extension, int lineCount);
}
