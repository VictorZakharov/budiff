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

    // The selected part of the line, in the line's own characters (indent
    // included); (0, 0) for none. A selectable line shows it.
    public static readonly AttachedProperty<(int From, int To)> SelectionProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, (int From, int To)>("Selection", typeof(DiffInlines));

    // Find matches on the line, in the line's own characters; the code cell
    // (CodeClip) marks them behind the text.
    public static readonly AttachedProperty<IReadOnlyList<(int From, int To)>?> MarksProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<(int From, int To)>?>("Marks", typeof(DiffInlines));

    public static void SetMarks(TextBlock element, IReadOnlyList<(int From, int To)>? value) => element.SetValue(MarksProperty, value);

    public static IReadOnlyList<(int From, int To)>? GetMarks(TextBlock element) => element.GetValue(MarksProperty);

    static DiffInlines()
    {
        RunsProperty.Changed.AddClassHandler<TextBlock>(OnRunsChanged);
        SelectionProperty.Changed.AddClassHandler<TextBlock>((text, _) => ShowSelection(text));
    }

    public static void SetSelection(TextBlock element, (int From, int To) value) => element.SetValue(SelectionProperty, value);

    public static (int From, int To) GetSelection(TextBlock element) => element.GetValue(SelectionProperty);

    public static void SetRuns(TextBlock element, IReadOnlyList<DiffRun>? value) => element.SetValue(RunsProperty, value);

    public static IReadOnlyList<DiffRun>? GetRuns(TextBlock element) => element.GetValue(RunsProperty);

    public static string GetIndent(TextBlock element) => element.GetValue(IndentProperty);

    private static void OnRunsChanged(TextBlock textBlock, AvaloniaPropertyChangedEventArgs e)
    {
        textBlock.Inlines?.Clear();
        if (e.NewValue is not IReadOnlyList<DiffRun> runs)
        {
            textBlock.SetValue(IndentProperty, string.Empty);
            ShowSelection(textBlock);
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
        ShowSelection(textBlock);
    }

    private static void ShowSelection(TextBlock textBlock)
    {
        if (textBlock is not SelectableTextBlock selectable)
        {
            return;
        }

        var (from, to) = GetSelection(textBlock);
        var indent = GetIndent(textBlock).Length;
        var length = 0;
        if (textBlock.Inlines is { } inlines)
        {
            foreach (var inline in inlines)
            {
                length += (inline as InlineRun)?.Text?.Length ?? 0;
            }
        }

        var start = System.Math.Clamp(from - indent, 0, length);
        var end = System.Math.Clamp(to - indent, 0, length);
        selectable.SelectionStart = start;
        selectable.SelectionEnd = end;
    }
}
