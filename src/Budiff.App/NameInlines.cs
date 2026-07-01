using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using InlineRun = Avalonia.Controls.Documents.Run;

namespace Budiff.App;

// One piece of a bird's-eye row name; IsMatch marks the searched substring.
public sealed record NameRun(string Text, bool IsMatch);

// Renders a row name as inline runs with the matched substring highlighted
// (amber pill, dark text for contrast; no horizontal padding so toggling the
// highlight causes zero layout shift).
public static class NameInlines
{
    private static readonly IBrush MatchBackground = new ImmutableSolidColorBrush(Color.Parse("#FFD166"));
    private static readonly IBrush MatchForeground = new ImmutableSolidColorBrush(Color.Parse("#202020"));

    public static readonly AttachedProperty<IReadOnlyList<NameRun>?> RunsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<NameRun>?>("Runs", typeof(NameInlines));

    static NameInlines() => RunsProperty.Changed.AddClassHandler<TextBlock>(OnRunsChanged);

    public static void SetRuns(TextBlock element, IReadOnlyList<NameRun>? value) => element.SetValue(RunsProperty, value);

    public static IReadOnlyList<NameRun>? GetRuns(TextBlock element) => element.GetValue(RunsProperty);

    private static void OnRunsChanged(TextBlock textBlock, AvaloniaPropertyChangedEventArgs e)
    {
        textBlock.Inlines ??= new InlineCollection();
        textBlock.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<NameRun> runs)
        {
            return;
        }

        foreach (var run in runs)
        {
            var inline = new InlineRun(run.Text);
            if (run.IsMatch)
            {
                inline.Background = MatchBackground;
                inline.Foreground = MatchForeground;
            }

            textBlock.Inlines.Add(inline);
        }
    }
}
