using System;
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

    public MainWindow()
        : this(Environment.CurrentDirectory, gitRoot: false)
    {
    }

    public MainWindow(string startPath, bool gitRoot)
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(startPath, gitRoot);
        DataContext = _viewModel;
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
