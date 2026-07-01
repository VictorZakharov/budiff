using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace Budiff.App;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private DiffWindow? _floatWindow;
    private GridLength _diffWidth = new(640);

    public MainWindow()
        : this(Environment.CurrentDirectory, gitRoot: false)
    {
    }

    public MainWindow(string startPath, bool gitRoot)
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(startPath, gitRoot);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyDiffLayout();
    }

    internal MainWindowViewModel ViewModel => _viewModel;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ShowDiffPane)
            or nameof(MainWindowViewModel.IsTreeCollapsed)
            or nameof(MainWindowViewModel.IsDiffOpen)
            or nameof(MainWindowViewModel.IsDiffFloating))
        {
            ApplyDiffLayout();
        }
    }

    // Sizes the body columns for the current diff state. ColumnDefinitions cannot be
    // x:Name'd (no field is generated), so the named BodyGrid is indexed instead.
    private void ApplyDiffLayout()
    {
        var cols = BodyGrid.ColumnDefinitions;

        // Remember the user's docked diff width before leaving the docked state.
        if (DiffHost.IsVisible && BirdsEyePanel.IsVisible && cols[2].Width.IsAbsolute && cols[2].Width.Value >= 100)
        {
            _diffWidth = cols[2].Width;
        }

        if (_viewModel.ShowDiffPane)
        {
            var collapsed = _viewModel.IsTreeCollapsed;
            BirdsEyePanel.IsVisible = !collapsed;
            BodySplitter.IsVisible = !collapsed;
            DiffHost.IsVisible = true;
            if (collapsed)
            {
                cols[0].MinWidth = 0;
                cols[0].Width = new GridLength(0);
                cols[1].Width = new GridLength(0);
                cols[2].MinWidth = 0;
                cols[2].Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                cols[0].MinWidth = 420;
                cols[0].Width = new GridLength(1, GridUnitType.Star);
                cols[1].Width = new GridLength(5);
                cols[2].MinWidth = 360;
                cols[2].Width = _diffWidth;
            }
        }
        else
        {
            BirdsEyePanel.IsVisible = true;
            BodySplitter.IsVisible = false;
            DiffHost.IsVisible = false;
            cols[0].MinWidth = 0;
            cols[0].Width = new GridLength(1, GridUnitType.Star);
            cols[1].Width = new GridLength(0);
            cols[2].MinWidth = 0;
            cols[2].Width = new GridLength(0);
        }

        SyncFloatWindow();
    }

    private void SyncFloatWindow()
    {
        if (_viewModel.IsDiffFloating && _viewModel.IsDiffOpen)
        {
            if (_floatWindow is null)
            {
                _floatWindow = new DiffWindow(_viewModel.Diff);
                _floatWindow.Closed += OnFloatWindowClosed;
                _floatWindow.Show(this);
            }
        }
        else if (_floatWindow is not null)
        {
            CloseFloatWindow();
        }
    }

    private void OnFloatWindowClosed(object? sender, EventArgs e)
    {
        // User closed the floating window: re-dock the diff.
        _floatWindow = null;
        _viewModel.IsDiffFloating = false;
    }

    private void CloseFloatWindow()
    {
        var window = _floatWindow;
        _floatWindow = null;
        if (window is not null)
        {
            window.Closed -= OnFloatWindowClosed;
            window.Close();
        }
    }

    private void OnFloatDiff(object? sender, RoutedEventArgs e)
    {
        _viewModel.IsDiffFloating = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseFloatWindow();
        base.OnClosed(e);
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open repository or folder",
            AllowMultiple = false,
        });

        if (folders.Count == 0)
        {
            return;
        }

        if (folders[0].TryGetLocalPath() is { } path)
        {
            await _viewModel.LoadAsync(path, gitRoot: false);
        }
    }

    private async void OnAnalysisRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FindAnalysisRow(e.Source) is { IsFile: true } row)
        {
            await _viewModel.Analysis.OpenAsync(row);
        }
    }

    private static CodeAnalysisRow? FindAnalysisRow(object? source)
    {
        var node = source as StyledElement;
        while (node is not null)
        {
            if (node.DataContext is CodeAnalysisRow row)
            {
                return row;
            }

            node = node.Parent;
        }

        return null;
    }
}
