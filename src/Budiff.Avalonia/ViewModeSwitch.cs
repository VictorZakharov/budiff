using System.ComponentModel;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Layout;
using global::Avalonia.Media;

namespace Budiff.Avalonia;

// The View / Diff switch at the top of the diff pane: two segments, the active
// one highlighted, one click (or Ctrl+D) flips. Styled like the bird's-eye's
// Tree / City switch. Its DataContext is the DiffViewModel.
public sealed class ViewModeSwitch : Border
{
    public const string ShortcutText = "Ctrl+D";

    private static readonly IBrush ActiveBackground = HexBrush.Parse("#2D5A9E");
    private static readonly IBrush ActiveText = HexBrush.Parse("#FFFFFF");
    private static readonly IBrush IdleText = HexBrush.Parse("#9DA0A8");

    private readonly Button _view;
    private readonly Button _diff;
    private DiffViewModel? _model;

    public ViewModeSwitch()
    {
        BorderBrush = HexBrush.Parse("#1E1F22");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        ClipToBounds = true;
        VerticalAlignment = VerticalAlignment.Center;

        _view = Segment("ViewModeButton", "View", new CornerRadius(3, 0, 0, 3),
            $"The file as it is now, without the change marks ({ShortcutText})", () => _model?.ShowViewCommand.Execute(null));
        _diff = Segment("DiffModeButton", "Diff", new CornerRadius(0, 3, 3, 0),
            $"The side-by-side comparison ({ShortcutText})", () => _model?.ShowDiffCommand.Execute(null));
        Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _view, _diff } };

        DataContextChanged += (_, _) =>
        {
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelPropertyChanged;
            }

            _model = DataContext as DiffViewModel;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelPropertyChanged;
            }

            UpdateSegments();
        };
        UpdateSegments();
    }

    // Ctrl+D, the shortcut both hosts (the main window, the floating one) listen for.
    public static bool IsToggleGesture(KeyEventArgs e) => e.Key == Key.D && e.KeyModifiers == KeyModifiers.Control;

    private static Button Segment(string name, string text, CornerRadius corners, string tip, System.Action click)
    {
        var button = new Button
        {
            Name = name,
            Content = text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = corners,
            Padding = new Thickness(9, 2),
            MinHeight = 0,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiffViewModel.IsViewActive) or nameof(DiffViewModel.IsViewMode) or nameof(DiffViewModel.CanView))
        {
            UpdateSegments();
        }
    }

    private void UpdateSegments()
    {
        var view = _model?.IsViewActive == true;
        Paint(_view, view);
        Paint(_diff, !view);
    }

    // The "active" class is for tests and hosts' styles; the colours are set here.
    private static void Paint(Button button, bool active)
    {
        button.Background = active ? ActiveBackground : Brushes.Transparent;
        button.Foreground = active ? ActiveText : IdleText;
        button.Classes.Set("active", active);
    }
}
