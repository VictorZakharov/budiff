using System;
using global::Avalonia.Controls;

namespace Budiff.Avalonia;

// The diff's rows: an ItemsControl whose recycled rows keep their controls.
// Stock containers (ContentPresenter) rebuild the row template whenever a
// recycled row comes back (~7ms a row; reaching the top of a file rebuilt
// every row on screen). A DiffRowContainer builds its row once and, when
// recycled, only takes the next row as its DataContext.
public sealed class DiffRowsHost : ItemsControl
{
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new DiffRowContainer();

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = typeof(DiffRowContainer);
        return true;
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        if (container is DiffRowContainer row)
        {
            row.DataContext = item;
            row.Child ??= ItemTemplate?.Build(item);
            return;
        }

        base.PrepareContainerForItemOverride(container, item, index);
    }

    // Nothing to clear: the row's controls wait for their next row.
    protected override void ClearContainerForItemOverride(Control container)
    {
        if (container is not DiffRowContainer)
        {
            base.ClearContainerForItemOverride(container);
        }
    }
}

public sealed class DiffRowContainer : Decorator
{
}
