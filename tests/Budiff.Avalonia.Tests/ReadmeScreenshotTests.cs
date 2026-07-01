using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Budiff;
using Budiff.Avalonia.TextMate;
using Xunit;
using CornerRadius = global::Avalonia.CornerRadius;
using Thickness = global::Avalonia.Thickness;

namespace Budiff.Avalonia.Tests;

public sealed class ReadmeScreenshotTests
{
    [AvaloniaFact]
    public void ReadmeScreenshot_RendersBirdsEyeAndDiff()
    {
        var diff = new DiffViewModel
        {
            Title = "src/Budiff.Cli/Program.cs",
            Subtitle = "HEAD vs working tree",
            Extension = ".cs",
            SyntaxHighlighter = new TextMateSyntaxHighlighter(),
        };

        diff.SetDiff(LineDiff.Compute(
            """
            using Budiff;
            using Budiff.Git;

            return BudiffCli.Run(args);

            internal static class BudiffCli
            {
                public static int Run(string[] args)
                {
                    var options = CliOptions.Parse(args);
                    AnsiRenderer.RenderList(changeSet, Console.Out);
                    return 0;
                }
            }
            """,
            """
            using Budiff;
            using Budiff.Git;

            return BudiffCli.Run(args);

            internal static class BudiffCli
            {
                public static int Run(string[] args)
                {
                    var options = CliOptions.Parse(args);
                    if (options.Json)
                    {
                        JsonRenderer.RenderList(changeSet, Console.Out);
                    }
                    else
                    {
                        AnsiRenderer.RenderList(changeSet, Console.Out);
                    }

                    return 0;
                }
            }
            """));

        for (var i = 0; i < 8; i++)
        {
            Thread.Sleep(40);
            UiSnapshot.Pump();
        }

        var window = new Window
        {
            Width = 1440,
            Height = 820,
            Content = Shell(diff),
        };

        var snapshot = UiSnapshot.Capture(window, "gui-screenshot");

        Assert.Equal(1440, snapshot.Width);
        Assert.Equal(820, snapshot.Height);
    }

    private static Control Shell(DiffViewModel diff)
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = Brush("#1E1F22"),
        };

        root.Children.Add(Header());

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("900,*"),
        };
        Grid.SetRow(body, 1);

        body.Children.Add(BirdsEye());
        var view = new DiffView { DataContext = diff };
        Grid.SetColumn(view, 1);
        body.Children.Add(view);

        root.Children.Add(body);
        return root;
    }

    private static Control Header()
    {
        var header = new Border
        {
            Padding = new Thickness(12, 8),
            Background = Brush("#3C3F41"),
            BorderBrush = Brush("#1E1F22"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 12,
        };

        grid.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "budiff",
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brush("#DFE1E5"),
                },
                new TextBlock
                {
                    Text = "repo root - feat/json-output",
                    FontSize = 11.5,
                    Foreground = Brush("#9DA0A8"),
                },
            },
        });

        var status = new TextBlock
        {
            Text = "HEAD vs working tree",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("#9DA0A8"),
        };
        Grid.SetColumn(status, 1);
        grid.Children.Add(status);

        var refresh = new TextBlock
        {
            Text = "Refresh",
            Foreground = Brush("#DFE1E5"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(refresh, 2);
        grid.Children.Add(refresh);

        header.Child = grid;
        return header;
    }

    private static Control BirdsEye()
    {
        var panel = new DockPanel
        {
            Background = Brush("#252629"),
        };

        var title = new Border
        {
            Padding = new Thickness(10, 8),
            BorderBrush = Brush("#1E1F22"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Bird's-eye",
                                FontWeight = FontWeight.SemiBold,
                                Foreground = Brush("#DFE1E5"),
                            },
                            new TextBlock
                            {
                                Text = "24 files - 8,412 lines (6,950 code / 782 blank / 680 comment) - +418 / -74 in window - 9 commits, 3 authors",
                                FontSize = 11.5,
                                Foreground = Brush("#9DA0A8"),
                            },
                        },
                    },
                    CountText("Last month"),
                },
            },
        };
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);

        var controls = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            ColumnSpacing = 8,
            Margin = new Thickness(10, 8),
            Children =
            {
                Chip("Folders"),
                Chip("Last month"),
                SearchBox(),
            },
        };
        Grid.SetColumn(controls.Children[1], 1);
        Grid.SetColumn(controls.Children[2], 2);
        DockPanel.SetDock(controls, Dock.Top);
        panel.Children.Add(controls);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,70,70,58,70,50,70,70,60,60"),
            Margin = new Thickness(10, 4, 10, 2),
            Children =
            {
                HeaderCell("Name", 0),
                HeaderCell("Lines", 1),
                HeaderCell("Code", 2),
                HeaderCell("Blank", 3),
                HeaderCell("Comment", 4),
                HeaderCell("Files", 5),
                HeaderCell("Added", 6),
                HeaderCell("Removed", 7),
                HeaderCell("Commits", 8),
                HeaderCell("Authors", 9),
            },
        };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        var rows = new StackPanel
        {
            Margin = new Thickness(8, 0),
            Spacing = 2,
            Children =
            {
                AnalysisRow("src", "5,842", "4,860", "512", "470", "14", "+312", "-52", "7", "3", 1.0, selected: false),
                AnalysisRow("  Budiff.Cli", "1,920", "1,610", "168", "142", "4", "+140", "-18", "4", "2", 0.55, selected: true),
                AnalysisRow("    Program.cs", "760", "650", "54", "56", "", "+42", "-8", "3", "2", 0.36, selected: true),
                AnalysisRow("  Budiff.Core", "1,504", "1,226", "130", "148", "5", "+96", "-12", "3", "2", 0.43, selected: false),
                AnalysisRow("tests", "1,644", "1,320", "170", "154", "7", "+82", "-16", "5", "2", 0.32, selected: false),
                AnalysisRow("README.md", "292", "230", "62", "0", "", "+24", "-6", "2", "1", 0.18, selected: false),
            },
        };

        panel.Children.Add(rows);
        return panel;
    }

    private static Control CountText(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = Brush("#9DA0A8"),
        };
        Grid.SetColumn(block, 1);
        return block;
    }

    private static Control Chip(string text)
        => new Border
        {
            Background = Brush("#303338"),
            BorderBrush = Brush("#4A4D52"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5),
            Child = new TextBlock { Text = text, Foreground = Brush("#DFE1E5") },
        };

    private static Control SearchBox()
        => new Border
        {
            Background = Brush("#1E1F22"),
            BorderBrush = Brush("#4A4D52"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5),
            Child = new TextBlock { Text = "Filter files...", Foreground = Brush("#9DA0A8") },
        };

    private static Control HeaderCell(string text, int column)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#9DA0A8"),
            HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
        };
        Grid.SetColumn(block, column);
        return block;
    }

    private static Control AnalysisRow(
        string name,
        string lines,
        string code,
        string blank,
        string comment,
        string files,
        string added,
        string removed,
        string commits,
        string authors,
        double bar,
        bool selected)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,70,70,58,70,50,70,70,60,60,82"),
            MinHeight = 30,
        };

        grid.Children.Add(Cell(name, 0, Brush("#DFE1E5"), left: true));
        grid.Children.Add(Cell(lines, 1, Brush("#DFE1E5")));
        grid.Children.Add(Cell(code, 2, Brush("#DFE1E5")));
        grid.Children.Add(Cell(blank, 3, Brush("#6E747E")));
        grid.Children.Add(Cell(comment, 4, Brush("#5E8C5E")));
        grid.Children.Add(Cell(files, 5, Brush("#9DA0A8")));
        grid.Children.Add(Cell(added, 6, Brush("#7EE787")));
        grid.Children.Add(Cell(removed, 7, Brush("#FF7B72")));
        grid.Children.Add(Cell(commits, 8, Brush("#9DA0A8")));
        grid.Children.Add(Cell(authors, 9, Brush("#9DA0A8")));
        grid.Children.Add(Bar(bar));

        return new Border
            {
            Background = selected ? Brush("#343A40") : Brushes.Transparent,
            Padding = new Thickness(4, 0, 8, 0),
            Child = grid,
        };
    }

    private static Control Cell(string text, int column, IBrush brush, bool left = false)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontFamily = left ? FontFamily.Default : FontFamily.Parse("Cascadia Code,Consolas,monospace"),
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0),
        };
        Grid.SetColumn(block, column);
        return block;
    }

    private static Control Bar(double value)
    {
        var outer = new Border
        {
            Background = Brush("#22FFFFFF"),
            CornerRadius = new CornerRadius(3),
            Height = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 6, 0),
        };
        Grid.SetColumn(outer, 10);
        outer.Child = new Border
        {
            Width = Math.Max(4, 58 * value),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brush("#61AFEF"),
            CornerRadius = new CornerRadius(3),
        };
        return outer;
    }

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}
