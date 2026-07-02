using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Budiff.Avalonia;

namespace Budiff.App;

// A floating host for the diff pane (e.g. on another monitor). It renders the same
// DiffViewModel as the docked pane; "Dock back" (or just closing the window)
// returns the diff to the docked split.
public sealed class DiffWindow : Window
{
    public DiffWindow(DiffViewModel diff, Action dockBack)
    {
        Width = 1100;
        Height = 720;
        MinWidth = 480;
        MinHeight = 320;
        Background = new ImmutableSolidColorBrush(Color.Parse("#1E1F22"));
        Bind(TitleProperty, new Binding(nameof(DiffViewModel.Title))
        {
            Source = diff,
            StringFormat = "budiff - {0}",
            FallbackValue = "budiff",
        });

        var dockButton = new Button
        {
            Content = "Dock back",
            FontSize = 12,
            Padding = new Thickness(8, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        ToolTip.SetTip(dockButton, "Return the diff to the split in the main window");
        dockButton.Click += (_, _) => dockBack();

        var bar = new Border
        {
            Background = new ImmutableSolidColorBrush(Color.Parse("#3C3F41")),
            BorderBrush = new ImmutableSolidColorBrush(Color.Parse("#1E1F22")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 3),
            Child = dockButton,
        };
        DockPanel.SetDock(bar, Dock.Top);

        var panel = new DockPanel();
        panel.Children.Add(bar);
        panel.Children.Add(new DiffView { DataContext = diff });
        Content = panel;
    }
}
