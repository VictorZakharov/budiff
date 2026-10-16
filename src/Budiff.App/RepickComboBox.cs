using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Budiff.App;

// A ComboBox that also reports picking the item that is already selected (a
// click, or Enter, on it in the open drop-down). SelectedItem does not change
// then, so a binding never hears about it; RepickCommand does, with the item
// as its parameter.
public sealed class RepickComboBox : ComboBox
{
    public static readonly StyledProperty<ICommand?> RepickCommandProperty =
        AvaloniaProperty.Register<RepickComboBox, ICommand?>(nameof(RepickCommand));

    public ICommand? RepickCommand
    {
        get => GetValue(RepickCommandProperty);
        set => SetValue(RepickCommandProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ComboBox);

    public override bool UpdateSelectionFromEvent(Control container, RoutedEventArgs eventArgs)
    {
        // Pointer and key picks only - focus moving onto the selected item
        // (the drop-down opening) comes through here too.
        var picked = eventArgs is PointerEventArgs or KeyEventArgs;
        var before = SelectedItem;
        if (!base.UpdateSelectionFromEvent(container, eventArgs))
        {
            return false;
        }

        if (picked && before is not null && Equals(ItemFromContainer(container), before) && RepickCommand is { } command)
        {
            // Once the drop-down has closed and the click is over.
            Dispatcher.UIThread.Post(() =>
            {
                if (command.CanExecute(before))
                {
                    command.Execute(before);
                }
            });
        }

        return true;
    }
}
