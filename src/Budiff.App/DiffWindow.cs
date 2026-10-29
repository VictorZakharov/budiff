using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Budiff.Avalonia;

namespace Budiff.App;

// A floating host for the diff pane (e.g. on another monitor). It renders the same
// DiffViewModel as the docked pane; "Dock back" (in the header row, next to the
// other controls) or simply closing the window returns the diff to the split.
public sealed class DiffWindow : Window
{
    public DiffWindow(DiffViewModel diff, Action dockBack)
    {
        Width = 1100;
        Height = 720;
        MinWidth = 480;
        MinHeight = 320;
        Background = HexBrush.Parse("#1E1F22");
        Bind(TitleProperty, new Binding(nameof(DiffViewModel.Title))
        {
            Source = diff,
            StringFormat = "budiff - {0}",
            FallbackValue = "budiff",
        });

        // Icon button, mirroring the docked pane's OpenInNew undock icon.
        var dockButton = new Button
        {
            Content = new AppIcon
            {
                Kind = AppIconKind.DockWindow,
                Width = 16,
                Height = 16,
                Foreground = HexBrush.Parse("#9DA0A8"),
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(5, 3),
            Margin = new Thickness(4, 0, 0, 0),
        };
        ToolTip.SetTip(dockButton, "Dock the diff back into the main window");
        dockButton.Click += (_, _) => dockBack();

        Content = new DiffView { DataContext = diff, HeaderExtra = dockButton };

        // Ctrl+D (View / Diff) with the focus anywhere in this window; inside
        // the diff view, the view takes it first.
        KeyDown += (_, e) =>
        {
            if (ViewModeSwitch.IsToggleGesture(e) && diff.CanView)
            {
                diff.ToggleViewModeCommand.Execute(null);
                e.Handled = true;
            }
        };
    }
}
