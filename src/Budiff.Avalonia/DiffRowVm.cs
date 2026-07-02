using System.Collections.Generic;
using Budiff;
using CommunityToolkit.Mvvm.ComponentModel;
using global::Avalonia.Media;

namespace Budiff.Avalonia;

public sealed partial class DiffRowVm : ObservableObject
{
    public bool IsGap { get; init; }

    public string? GapText { get; init; }

    public int GapRunStart { get; init; }

    public string? OldNo { get; init; }

    public string? OldText { get; init; }

    public string? NewNo { get; init; }

    public string? NewText { get; init; }

    public DiffRow? Source { get; init; }

    [ObservableProperty]
    private IReadOnlyList<DiffRun>? _oldRuns;

    [ObservableProperty]
    private IReadOnlyList<DiffRun>? _newRuns;

    // Blame for the OLD side's line; arrives asynchronously after the diff.
    [ObservableProperty]
    private BlameEntry? _blame;

    public DiffKind Kind { get; init; }

    public IBrush OldBackground { get; init; } = Brushes.Transparent;

    public IBrush NewBackground { get; init; } = Brushes.Transparent;
}
