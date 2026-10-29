using System.Collections.Generic;
using global::Avalonia;
using global::Avalonia.Controls;
using InlineRun = global::Avalonia.Controls.Documents.Run;

namespace Budiff.Avalonia;

public static class DiffInlines
{
    public static readonly AttachedProperty<IReadOnlyList<DiffRun>?> RunsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<DiffRun>?>("Runs", typeof(DiffInlines));

    // The line's leading whitespace, taken out of the text: the code cell
    // (CodeClip) turns it into a hanging indent, so a wrapping line cannot
    // break right after its indent and restart at column 0, and its wrapped
    // lines continue under the code instead of under the line number.
    public static readonly AttachedProperty<string> IndentProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string>("Indent", typeof(DiffInlines), string.Empty);

    static DiffInlines() => RunsProperty.Changed.AddClassHandler<TextBlock>(OnRunsChanged);

    public static void SetRuns(TextBlock element, IReadOnlyList<DiffRun>? value) => element.SetValue(RunsProperty, value);

    public static IReadOnlyList<DiffRun>? GetRuns(TextBlock element) => element.GetValue(RunsProperty);

    public static string GetIndent(TextBlock element) => element.GetValue(IndentProperty);

    private static void OnRunsChanged(TextBlock textBlock, AvaloniaPropertyChangedEventArgs e)
    {
        textBlock.Inlines?.Clear();
        if (e.NewValue is not IReadOnlyList<DiffRun> runs)
        {
            textBlock.SetValue(IndentProperty, string.Empty);
            return;
        }

        var indent = new System.Text.StringBuilder();
        var leading = true;
        foreach (var run in runs)
        {
            var text = run.Text;
            if (leading)
            {
                var skip = 0;
                while (skip < text.Length && text[skip] is ' ' or '\t')
                {
                    skip++;
                }

                indent.Append(text, 0, skip);
                if (skip == text.Length)
                {
                    continue;
                }

                leading = false;
                text = text.Substring(skip);
            }

            textBlock.Inlines?.Add(new InlineRun(text) { Foreground = run.Brush });
        }

        textBlock.SetValue(IndentProperty, indent.ToString());
    }
}
