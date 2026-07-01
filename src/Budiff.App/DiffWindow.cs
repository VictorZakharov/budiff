using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Budiff.Avalonia;

namespace Budiff.App;

// A floating host for the diff pane (e.g. on another monitor). It renders the same
// DiffViewModel as the docked pane; closing the window re-docks the diff.
public sealed class DiffWindow : Window
{
    public DiffWindow(DiffViewModel diff)
    {
        Width = 1100;
        Height = 720;
        MinWidth = 480;
        MinHeight = 320;
        Background = new ImmutableSolidColorBrush(Color.Parse("#1E1F22"));
        Content = new DiffView { DataContext = diff };
        Bind(TitleProperty, new Binding(nameof(DiffViewModel.Title))
        {
            Source = diff,
            StringFormat = "budiff - {0}",
            FallbackValue = "budiff",
        });
    }
}
