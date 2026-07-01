using System.Collections.Generic;
using global::Avalonia;
using global::Avalonia.Controls;
using InlineRun = global::Avalonia.Controls.Documents.Run;

namespace Budiff.Avalonia;

public static class DiffInlines
{
    public static readonly AttachedProperty<IReadOnlyList<DiffRun>?> RunsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<DiffRun>?>("Runs", typeof(DiffInlines));

    static DiffInlines() => RunsProperty.Changed.AddClassHandler<TextBlock>(OnRunsChanged);

    public static void SetRuns(TextBlock element, IReadOnlyList<DiffRun>? value) => element.SetValue(RunsProperty, value);

    public static IReadOnlyList<DiffRun>? GetRuns(TextBlock element) => element.GetValue(RunsProperty);

    private static void OnRunsChanged(TextBlock textBlock, AvaloniaPropertyChangedEventArgs e)
    {
        textBlock.Inlines?.Clear();
        if (e.NewValue is not IReadOnlyList<DiffRun> runs)
        {
            return;
        }

        foreach (var run in runs)
        {
            textBlock.Inlines?.Add(new InlineRun(run.Text) { Foreground = run.Brush });
        }
    }
}
