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

    // The populated side, for the single-pane layout (all-added shows the new
    // side, all-deleted the old side).
    public string? SingleNo => NewNo ?? OldNo;

    public IReadOnlyList<DiffRun>? SingleRuns => NewNo is not null ? NewRuns : OldRuns;

    public IBrush SingleBackground => NewNo is not null ? NewBackground : OldBackground;

    partial void OnNewRunsChanged(IReadOnlyList<DiffRun>? value) => OnPropertyChanged(nameof(SingleRuns));

    partial void OnOldRunsChanged(IReadOnlyList<DiffRun>? value) => OnPropertyChanged(nameof(SingleRuns));

    public DiffKind Kind { get; init; }

    // An unchanged line shown as context while Collapse unchanged is on: it
    // stays one line (trimmed) even when wrapping, so a long unchanged line
    // - a markdown paragraph - does not turn the context into a wall of text.
    public bool IsCompact { get; init; }

    public IBrush OldBackground { get; init; } = Brushes.Transparent;

    public IBrush NewBackground { get; init; } = Brushes.Transparent;
}
