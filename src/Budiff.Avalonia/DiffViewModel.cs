using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Budiff;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using global::Avalonia.Media;
using global::Avalonia.Media.Immutable;
using global::Avalonia.Threading;

namespace Budiff.Avalonia;

public sealed partial class DiffViewModel : ObservableObject
{
    private const int ContextLines = 3;
    private const int HighlightIndicatorThreshold = 400;
    private const int LoadingIndicatorDelayMs = 120;
    private const int ViewportChunkRows = 64;

    // Monospace advance at the diff font size; the horizontal range is the
    // longest line's width minus what the narrower pane already shows.
    private const double CharWidth = 7.6;

    public const double MinSplitRatio = 0.15;
    public const double MaxSplitRatio = 0.85;

    private readonly HashSet<int> _expandedGaps = new();
    private IReadOnlyList<DiffRow> _all = Array.Empty<DiffRow>();
    private DiffRun[][] _oldRuns = Array.Empty<DiffRun[]>();
    private DiffRun[][] _newRuns = Array.Empty<DiffRun[]>();
    private IBrush _addedBackground = Brush("#3331C36B");
    private IBrush _removedBackground = Brush("#33E05252");
    private IBrush _defaultSyntaxBrush = Brush("#D4D4D4");
    private bool _collapseUnchanged = true;
    private bool _hasChanges;
    private DocumentPreviewMode _previewMode = DocumentPreviewMode.NewOnly;
    private IDocumentPreviewer? _previewer;
    private double _splitRatio = 0.5;
    private int _maxLineLength;

    // Until a view reports its real pane width, assume ~80 visible columns.
    private double _visibleTextWidth = 80 * CharWidth;
    private IReadOnlyList<BlameEntry?>? _blameOld;
    private IReadOnlyList<BlameEntry?>? _blameNew;
    private int _tokenizeGen;
    private int _loadGen;
    private DispatcherTimer? _loadingTimer;

    public DiffViewModel()
    {
        IsPlaceholder = true;
        Placeholder = "No diff selected.";
    }

    public BulkObservableCollection<DiffRowVm> Rows { get; } = new();

    public ISyntaxHighlighter? SyntaxHighlighter { get; set; }

    // Optional formatted view for document types (e.g. markdown); with none,
    // the Preview toggle never shows.
    public IDocumentPreviewer? Previewer
    {
        get => _previewer;
        set
        {
            if (SetProperty(ref _previewer, value))
            {
                NotifyPreviewState();
            }
        }
    }

    // The Preview toggle: sticky across files, it applies whenever the open
    // file is a type the previewer formats.
    [ObservableProperty]
    private bool _showPreview;

    partial void OnShowPreviewChanged(bool value) => NotifyPreviewState();

    public bool CanPreview => Previewer is { } previewer
        && (FileName.Length > 0 ? previewer.CanPreviewFile(FileName) : previewer.CanPreview(Extension));

    public bool IsPreviewActive => ShowPreview && CanPreview && !IsPlaceholder;

    // Blame and wrap belong to the line diff; the preview has neither.
    public bool ShowBlameOption => ShowDiffOptions && !IsPreviewActive;

    public bool ShowWrapOption => !IsPreviewActive;

    // Where the open document lives (its images, its links); set by the host
    // with each file it opens, before that file's diff arrives.
    public DocumentPreviewContext? PreviewContext { get; set; }

    // Where the open file is scrolled to now (its preview or its line diff),
    // kept current by the view - a host remembers it to come back to.
    public DiffScrollSpot? ScrollSpot { get; set; }

    // Set by a host before opening a file: once that file shows, the view
    // scrolls to this spot (the file's own last spot, or DiffScrollSpot.Start).
    public DiffScrollSpot? RestoreScroll { get; set; }

    // The row (index into Rows) a file opened fresh starts at: past a
    // TypeScript file's imports to its first useful line, but never past a
    // change in a two-sided diff; 0 for everything else.
    public int StartRow()
    {
        if (!ImportPreamble.AppliesTo(FileName.Length > 0 ? FileName : Extension) || _all.Count == 0)
        {
            return 0;
        }

        var lines = new string?[_all.Count];
        for (var i = 0; i < _all.Count; i++)
        {
            lines[i] = _all[i].NewNo is not null ? _all[i].NewText : _all[i].OldText;
        }

        // A wholly added (or deleted) file is all change: skip its imports too.
        var target = ImportPreamble.FirstUsefulLine(lines);
        for (var i = 0; i < target && !IsSinglePane; i++)
        {
            if (_all[i].Kind != DiffKind.Unchanged)
            {
                target = i;
                break;
            }
        }

        if (target == 0)
        {
            return 0;
        }

        var index = new Dictionary<DiffRow, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < _all.Count; i++)
        {
            index[_all[i]] = i;
        }

        for (var k = 0; k < Rows.Count; k++)
        {
            if (Rows[k].Source is { } source && index[source] >= target)
            {
                // Folded into the gap just above: start at the gap.
                return k > 0 && Rows[k - 1] is { IsGap: true } gap && gap.GapRunStart <= target ? k - 1 : k;
            }
        }

        return 0;
    }

    // After following a preview link, Back (header button, Alt+Left, the
    // mouse's back button) returns; the host keeps the trail.
    [ObservableProperty]
    private bool _canGoBack;

    public Action? GoBackHandler { get; set; }

    // A click on a blame cell: the host opens that commit (null = blame cells
    // are not clickable).
    public Action<BlameEntry>? OpenCommitHandler { get; set; }

    [RelayCommand]
    private void GoBack() => GoBackHandler?.Invoke();

    // Bumped whenever what the preview shows changes (new content, collapse
    // toggled), so a view re-renders it.
    [ObservableProperty]
    private int _previewVersion;

    // Between BeginLoad and its result. The rows still belong to the file
    // shown before, so a view keeps showing that instead of rendering them
    // under the next file's name (a flash of the wrong document).
    public bool IsLoading { get; private set; }

    public DocumentPreviewRequest CreatePreviewRequest()
    {
        var (oldLines, newLines) = ReconstructSides();
        return new DocumentPreviewRequest(
            string.Join("\n", oldLines),
            string.Join("\n", newLines),
            _all,
            _previewMode,
            _collapseUnchanged,
            PreviewContext,
            FileName.Length > 0 ? FileName : null);
    }

    private void NotifyPreviewState()
    {
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(IsPreviewActive));
        OnPropertyChanged(nameof(ShowBlameOption));
        OnPropertyChanged(nameof(ShowWrapOption));
    }

    partial void OnExtensionChanged(string value) => NotifyPreviewState();

    partial void OnFileNameChanged(string value) => NotifyPreviewState();

    partial void OnIsPlaceholderChanged(bool value) => NotifyPreviewState();

    partial void OnIsFileViewChanged(bool value) => NotifyPreviewState();

    [ObservableProperty]
    private string _title = "Diff";

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _extension = string.Empty;

    // The open file's name (LICENSE, notes.md); with none, the preview goes
    // by Extension alone.
    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private double _horizontalScroll;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHorizontalScroll))]
    private double _maxScroll;

    [ObservableProperty]
    private bool _isHighlighting;

    // Blame gutter: ShowBlame is BOTH the column's visibility and the header
    // checkbox, so the checkmark always tracks what is on screen. Crossing the
    // "pane under half the window" boundary auto-sets it (off when cramped, on
    // when roomy again); the user can flip the checkbox afterwards to override
    // in either direction until the next crossing.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlameVisible))]
    [NotifyPropertyChangedFor(nameof(BlameGutterWidth))]
    private bool _showBlame = true;

    [ObservableProperty]
    private bool _blameCramped;

    partial void OnBlameCrampedChanged(bool value) => ShowBlame = !value;

    // A plain file view (nothing to compare against - e.g. a folder outside
    // git): its rows are all unchanged, so they show as one full-width pane
    // like any identical diff; this flag additionally drops blame and the
    // diff-only toggles.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlameVisible))]
    [NotifyPropertyChangedFor(nameof(BlameGutterWidth))]
    [NotifyPropertyChangedFor(nameof(ShowDiffOptions))]
    private bool _isFileView;

    // The Blame / Collapse unchanged toggles mean nothing in a file view.
    public bool ShowDiffOptions => !IsFileView;

    public bool IsBlameVisible => ShowBlame && !IsFileView;

    // The funnel gutter offsets its center by this (the blame column sits left
    // of both code halves); the row template's blame cells bind to it too.
    public double BlameGutterWidth => IsBlameVisible ? BlameLabelWidth : 0;

    // Wide enough for the file's longest blame label ("author age") in full.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlameGutterWidth))]
    private double _blameLabelWidth = MinBlameWidth;

    internal const double MinBlameWidth = 120;
    internal const double MaxBlameWidth = 280;

    // Measures a blame label as its cell draws it - set by the view, which
    // knows the font; without one (plain view-model tests) an estimate stands
    // in. Setting it re-fits the column to the blame already shown.
    public Func<string, double>? MeasureBlameLabel
    {
        get => _measureBlameLabel;
        set
        {
            _measureBlameLabel = value;
            FitBlameColumn();
        }
    }

    private Func<string, double>? _measureBlameLabel;

    private void FitBlameColumn()
    {
        var measure = _measureBlameLabel ?? (label => label.Length * 6.5);
        var widest = (_blameOld ?? Array.Empty<BlameEntry?>()).Concat(_blameNew ?? Array.Empty<BlameEntry?>())
            .OfType<BlameEntry>().Select(e => e.Label).Distinct().Select(measure).DefaultIfEmpty(0).Max();
        // + the cell's padding (8 left, 4 right) and a little air.
        BlameLabelWidth = Math.Clamp(Math.Ceiling(widest) + 16, MinBlameWidth, MaxBlameWidth);
    }

    [ObservableProperty]
    private bool _isPlaceholder;

    // A wholly added (new file) or wholly deleted diff has one empty side -
    // render just the populated side at full width instead of a dead half.
    [ObservableProperty]
    private bool _isSinglePane;

    [ObservableProperty]
    private string _placeholder = string.Empty;

    public bool HasHorizontalScroll => MaxScroll > 0;

    // Long lines wrap inside their pane (rows grow taller) instead of
    // scrolling sideways. On by default, for every file type.
    [ObservableProperty]
    private bool _wrapLines = true;

    partial void OnWrapLinesChanged(bool value) => UpdateMaxScroll();

    // The OLD side's share of the two-pane code width (what remains after the
    // blame column and the funnel gutter). Dragging the gutter sets it, a
    // double-click resets it; it carries over from file to file.
    public double SplitRatio
    {
        get => _splitRatio;
        set => SetProperty(ref _splitRatio, Math.Clamp(double.IsFinite(value) ? value : 0.5, MinSplitRatio, MaxSplitRatio));
    }

    // Text width (px) of the narrower code pane, reported by the view as the
    // split or the pane size changes - a squeezed side must still be able to
    // scroll to the end of the longest line.
    public double VisibleTextWidth
    {
        get => _visibleTextWidth;
        set
        {
            if (Math.Abs(_visibleTextWidth - value) >= 0.5)
            {
                _visibleTextWidth = value;
                UpdateMaxScroll();
            }
        }
    }

    private void UpdateMaxScroll()
    {
        MaxScroll = WrapLines || _maxLineLength == 0 ? 0 : Math.Max(0, (_maxLineLength * CharWidth) - _visibleTextWidth);
        if (HorizontalScroll > MaxScroll)
        {
            HorizontalScroll = MaxScroll;
        }
    }

    public bool CollapseUnchanged
    {
        get => _collapseUnchanged;
        set
        {
            if (SetProperty(ref _collapseUnchanged, value))
            {
                Rebuild();
                PreviewVersion++;
            }
        }
    }

    public void SetTheme(IBrush addedBackground, IBrush removedBackground, IBrush defaultSyntaxBrush)
    {
        _addedBackground = addedBackground;
        _removedBackground = removedBackground;
        _defaultSyntaxBrush = defaultSyntaxBrush;
        if (_all.Count > 0)
        {
            Rebuild();
        }
    }

    public void BeginLoad(Func<DiffResult> loader)
    {
        var gen = ++_loadGen;
        IsLoading = true;
        _tokenizeGen++;
        IsHighlighting = false;

        _loadingTimer?.Stop();
        _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LoadingIndicatorDelayMs) };
        _loadingTimer.Tick += (_, _) =>
        {
            _loadingTimer?.Stop();
            if (gen == _loadGen)
            {
                ShowLoadingIndicator();
            }
        };
        _loadingTimer.Start();

        // The result comes back through the dispatcher taken here, on the UI
        // thread (see HighlightAsync).
        var ui = Dispatcher.UIThread;
        Task.Run(() =>
        {
            DiffResult result;
            try
            {
                result = loader();
            }
            catch
            {
                result = DiffResult.Message("Could not load this diff.");
            }

            ui.Post(() =>
            {
                if (gen != _loadGen)
                {
                    return;
                }

                _loadingTimer?.Stop();
                IsLoading = false;
                if (result.Rows is { } rows)
                {
                    SetDiff(rows);
                }
                else
                {
                    SetPlaceholder(result.Placeholder ?? "Nothing to show.");
                }
            });
        });
    }

    // Blame arrives asynchronously (1-based line n at index n-1); null clears
    // it, e.g. when a new diff starts loading. One side's blame: the OLD
    // side's, or (forNewSide) the NEW side's.
    public void SetBlame(IReadOnlyList<BlameEntry?>? lines, bool forNewSide = false)
        => SetBlame(forNewSide ? null : lines, forNewSide ? lines : null);

    // Both sides: a row shows who last changed the line as it is NOW (the new
    // side), and only a removed line - gone from the new side - its old blame.
    public void SetBlame(IReadOnlyList<BlameEntry?>? oldSide, IReadOnlyList<BlameEntry?>? newSide)
    {
        _blameOld = oldSide;
        _blameNew = newSide;
        FitBlameColumn();
        foreach (var vm in Rows)
        {
            vm.Blame = BlameFor(vm.Source);
        }
    }

    private BlameEntry? BlameFor(DiffRow? row)
        => At(_blameNew, row?.NewNo) ?? (row?.NewNo is null || _blameNew is null ? At(_blameOld, row?.OldNo) : null);

    private static BlameEntry? At(IReadOnlyList<BlameEntry?>? blame, int? lineNo)
        => blame is not null && lineNo is { } n && n >= 1 && n <= blame.Count ? blame[n - 1] : null;

    // The diff is taking a while. The file's blame is loaded alongside and
    // may already be here: keep it for the rows, or they arrive without it.
    internal void ShowLoadingIndicator()
    {
        var (oldBlame, newBlame) = (_blameOld, _blameNew);
        SetPlaceholder("Loading diff...");
        (_blameOld, _blameNew) = (oldBlame, newBlame);
    }

    // Drops a load still under way: its result never lands. For a host that
    // shows something else in its place (SetPlaceholder alone would be
    // overwritten when the load finishes).
    public void CancelLoad()
    {
        _loadGen++;
        _loadingTimer?.Stop();
        IsLoading = false;
    }

    public void SetPlaceholder(string text)
    {
        // A direct placeholder must also silence a pending BeginLoad
        // "Loading diff..." tick, or the timer overwrites this text.
        _loadingTimer?.Stop();
        _tokenizeGen++;
        IsHighlighting = false;
        _all = Array.Empty<DiffRow>();
        _blameOld = null;
        _blameNew = null;
        _oldRuns = Array.Empty<DiffRun[]>();
        _newRuns = Array.Empty<DiffRun[]>();
        _expandedGaps.Clear();
        _maxLineLength = 0;
        MaxScroll = 0;
        HorizontalScroll = 0;
        IsSinglePane = false;
        Rows.Clear();
        Placeholder = text;
        IsPlaceholder = true;
    }

    public void SetDiff(IReadOnlyList<DiffRow> rows)
    {
        _loadingTimer?.Stop();
        IsLoading = false;

        // A reload that produced the exact same rows (e.g. the working-tree
        // toggle with a clean tree) keeps everything - rows, highlighting,
        // scroll - instead of rebuilding into a visible flicker.
        if (!IsPlaceholder && SameRows(_all, rows))
        {
            // Still news for a view: the file may be another one with the
            // same text (a preview renders per file), and a pending restore
            // is consumed now rather than jump the view later.
            PreviewVersion++;
            return;
        }

        IsPlaceholder = false;
        Placeholder = string.Empty;
        _all = rows;
        _expandedGaps.Clear();
        HorizontalScroll = 0;

        // A missing side diffs as no lines (LineDiff.Compute), so a wholly
        // added or deleted file is all Added or all Removed rows.
        var anyChange = false;
        var allAdded = true;
        var allRemoved = true;
        foreach (var row in rows)
        {
            anyChange |= row.Kind != DiffKind.Unchanged;
            allAdded &= row.Kind == DiffKind.Added;
            allRemoved &= row.Kind == DiffKind.Removed;
        }

        // Identical sides (no change at all) show the file itself: one pane,
        // nothing folded away - collapsing would hide the whole file in a gap.
        _hasChanges = anyChange;
        IsSinglePane = !anyChange || allAdded || allRemoved;
        _previewMode = !anyChange || allAdded ? DocumentPreviewMode.NewOnly
            : allRemoved ? DocumentPreviewMode.OldOnly
            : DocumentPreviewMode.Diff;

        var maxLen = 0;
        foreach (var row in rows)
        {
            maxLen = Math.Max(maxLen, Math.Max(row.OldText?.Length ?? 0, row.NewText?.Length ?? 0));
        }

        _maxLineLength = maxLen;
        UpdateMaxScroll();

        var (oldLines, newLines) = ReconstructSides();
        _oldRuns = Plain(oldLines);
        _newRuns = Plain(newLines);
        Rebuild();
        PreviewVersion++;

        var displayed = new List<DiffRow>(Rows.Count);
        foreach (var vm in Rows)
        {
            if (vm.Source is { } source)
            {
                displayed.Add(source);
            }
        }

        var hasHidden = displayed.Count < _all.Count;
        var gen = ++_tokenizeGen;
        var extension = Extension;
        IsHighlighting = oldLines.Length + newLines.Length > HighlightIndicatorThreshold
            && SyntaxHighlighter is not null;

        if (SyntaxHighlighter is not null)
        {
            var ui = Dispatcher.UIThread;
            Task.Run(() => HighlightAsync(oldLines, newLines, displayed, hasHidden, extension, gen, ui));
        }
    }

    public void ExpandGap(int runStart)
    {
        _expandedGaps.Add(runStart);
        Rebuild();
    }

    // Runs off the UI thread and posts back through ui, the dispatcher taken
    // on the UI thread: reaching for Dispatcher.UIThread from here could
    // adopt this thread as the UI one while there is none (between tests).
    private void HighlightAsync(string[] oldLines, string[] newLines, List<DiffRow> displayed, bool hasHidden, string extension, int gen, Dispatcher ui)
    {
        try
        {
            var oldSession = SyntaxHighlighter?.CreateSession(extension, oldLines.Length);
            var newSession = SyntaxHighlighter?.CreateSession(extension, newLines.Length);

            if (oldSession is not null || newSession is not null)
            {
                object? oldState = null;
                object? newState = null;
                var oldBatch = new List<(int Line, DiffRun[] Runs)>();
                var newBatch = new List<(int Line, DiffRun[] Runs)>();
                var pending = 0;

                foreach (var row in displayed)
                {
                    if (oldSession is not null && row.OldNo is { } oldNo)
                    {
                        var result = oldSession.HighlightLine(row.OldText ?? string.Empty, oldState);
                        oldState = result.State;
                        oldBatch.Add((oldNo, result.Runs.ToArray()));
                    }

                    if (newSession is not null && row.NewNo is { } newNo)
                    {
                        var result = newSession.HighlightLine(row.NewText ?? string.Empty, newState);
                        newState = result.State;
                        newBatch.Add((newNo, result.Runs.ToArray()));
                    }

                    if (++pending >= ViewportChunkRows)
                    {
                        PublishRuns(oldBatch, newBatch, gen, ui);
                        oldBatch = new List<(int Line, DiffRun[] Runs)>();
                        newBatch = new List<(int Line, DiffRun[] Runs)>();
                        pending = 0;
                    }
                }

                PublishRuns(oldBatch, newBatch, gen, ui);
            }

            if (hasHidden)
            {
                var oldTok = TokenizeAll(oldLines, extension);
                var newTok = TokenizeAll(newLines, extension);
                ui.Post(() =>
                {
                    if (gen != _tokenizeGen)
                    {
                        return;
                    }

                    _oldRuns = oldTok;
                    _newRuns = newTok;
                    ApplyRuns();
                    IsHighlighting = false;
                });
            }
            else
            {
                ui.Post(() =>
                {
                    if (gen == _tokenizeGen)
                    {
                        IsHighlighting = false;
                    }
                });
            }
        }
        catch
        {
            ui.Post(() =>
            {
                if (gen == _tokenizeGen)
                {
                    IsHighlighting = false;
                }
            });
        }
    }

    private DiffRun[][] TokenizeAll(IReadOnlyList<string> lines, string extension)
    {
        var session = SyntaxHighlighter?.CreateSession(extension, lines.Count);
        if (session is null)
        {
            return Plain(lines);
        }

        var result = new DiffRun[lines.Count][];
        object? state = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = session.HighlightLine(lines[i] ?? string.Empty, state);
            state = line.State;
            result[i] = line.Runs.ToArray();
        }

        return result;
    }

    private void PublishRuns(List<(int Line, DiffRun[] Runs)> oldBatch, List<(int Line, DiffRun[] Runs)> newBatch, int gen, Dispatcher ui)
    {
        if (oldBatch.Count == 0 && newBatch.Count == 0)
        {
            return;
        }

        ui.Post(() =>
        {
            if (gen != _tokenizeGen)
            {
                return;
            }

            foreach (var (line, runs) in oldBatch)
            {
                if (line >= 1 && line <= _oldRuns.Length)
                {
                    _oldRuns[line - 1] = runs;
                }
            }

            foreach (var (line, runs) in newBatch)
            {
                if (line >= 1 && line <= _newRuns.Length)
                {
                    _newRuns[line - 1] = runs;
                }
            }

            ApplyRuns();
        });
    }

    private static bool SameRows(IReadOnlyList<DiffRow> a, IReadOnlyList<DiffRow> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    private (string[] Old, string[] New) ReconstructSides()
    {
        int oldMax = 0, newMax = 0;
        foreach (var row in _all)
        {
            if (row.OldNo is { } oldNo && oldNo > oldMax)
            {
                oldMax = oldNo;
            }

            if (row.NewNo is { } newNo && newNo > newMax)
            {
                newMax = newNo;
            }
        }

        var oldLines = new string[oldMax];
        var newLines = new string[newMax];
        foreach (var row in _all)
        {
            if (row.OldNo is { } oldNo)
            {
                oldLines[oldNo - 1] = row.OldText ?? string.Empty;
            }

            if (row.NewNo is { } newNo)
            {
                newLines[newNo - 1] = row.NewText ?? string.Empty;
            }
        }

        return (oldLines, newLines);
    }

    private void ApplyRuns()
    {
        foreach (var vm in Rows)
        {
            if (vm.IsGap || vm.Source is not { } source)
            {
                continue;
            }

            vm.OldRuns = RunsFor(_oldRuns, source.OldNo);
            vm.NewRuns = RunsFor(_newRuns, source.NewNo);
        }
    }

    private void Rebuild()
    {
        // Built as a list and swapped in with ONE reset: per-row Add events made
        // the items host (and the gutter) process thousands of changes per toggle.
        // The collapse/gap shape itself lives in DiffRowCollapser.
        var collapsing = _collapseUnchanged && _hasChanges;
        var plan = DiffRowCollapser.Build(_all, collapsing, ContextLines, _expandedGaps);
        var rows = new List<DiffRowVm>(plan.Count);
        foreach (var item in plan)
        {
            rows.Add(item.IsGap
                ? new DiffRowVm
                {
                    IsGap = true,
                    GapRunStart = item.GapRunStart,
                    GapText = DiffRowCollapser.GapText(item.Hidden),
                }
                : Content(_all[item.Index], compact: collapsing && _all[item.Index].Kind == DiffKind.Unchanged));
        }

        Rows.ReplaceAll(rows);
    }

    private DiffRowVm Content(DiffRow row, bool compact)
    {
        var (oldBg, newBg) = row.Kind switch
        {
            DiffKind.Added => (Brushes.Transparent, _addedBackground),
            DiffKind.Removed => (_removedBackground, (IBrush)Brushes.Transparent),
            DiffKind.Modified => (_removedBackground, _addedBackground),
            _ => ((IBrush)Brushes.Transparent, Brushes.Transparent),
        };

        return new DiffRowVm
        {
            Source = row,
            OldNo = row.OldNo?.ToString(),
            OldText = row.OldText,
            NewNo = row.NewNo?.ToString(),
            NewText = row.NewText,
            OldRuns = RunsFor(_oldRuns, row.OldNo),
            NewRuns = RunsFor(_newRuns, row.NewNo),
            Blame = BlameFor(row),
            Kind = row.Kind,
            IsCompact = compact,
            OldBackground = oldBg,
            NewBackground = newBg,
        };
    }

    private DiffRun[][] Plain(IReadOnlyList<string> lines)
    {
        var result = new DiffRun[lines.Count][];
        for (var i = 0; i < lines.Count; i++)
        {
            result[i] = new[] { new DiffRun(lines[i] ?? string.Empty, _defaultSyntaxBrush) };
        }

        return result;
    }

    private static IReadOnlyList<DiffRun>? RunsFor(DiffRun[][] runs, int? lineNo)
        => lineNo is { } n && n >= 1 && n <= runs.Length ? runs[n - 1] : null;

    private static IBrush Brush(string hex) => HexBrush.Parse(hex);
}
