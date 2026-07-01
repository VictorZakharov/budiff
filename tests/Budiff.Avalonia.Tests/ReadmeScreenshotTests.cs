using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Budiff;
using Budiff.Avalonia.TextMate;
using Xunit;
using Thickness = global::Avalonia.Thickness;

namespace Budiff.Avalonia.Tests;

public sealed class ReadmeScreenshotTests
{
    [AvaloniaFact]
    public void ReadmeScreenshot_RendersChangedFilesAndDiff()
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
            Width = 1180,
            Height = 760,
            Content = Shell(diff),
        };

        var snapshot = UiSnapshot.Capture(window, "gui-screenshot");

        Assert.Equal(1180, snapshot.Width);
        Assert.Equal(760, snapshot.Height);
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
            ColumnDefinitions = new ColumnDefinitions("340,*"),
        };
        Grid.SetRow(body, 1);

        body.Children.Add(ChangeList());
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

    private static Control ChangeList()
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
                    new TextBlock
                    {
                        Text = "Changed files",
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brush("#DFE1E5"),
                    },
                    CountText("5 files"),
                },
            },
        };
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);

        var files = new StackPanel
        {
            Margin = new Thickness(4, 6),
            Spacing = 2,
            Children =
            {
                FileRow("M", "src/Budiff.Cli/Program.cs", "1 author, 2 commits", "+30", "-3", selected: true),
                FileRow("A", "src/Budiff.Cli/JsonOutput.cs", "structured JSON renderer", "+148", "-0", selected: false),
                FileRow("M", "README.md", "usage docs", "+2", "-0", selected: false),
                FileRow("A", "tests/Budiff.Cli.Tests/JsonRendererTests.cs", "JSON coverage", "+73", "-0", selected: false),
                FileRow("A", "docs/gui-screenshot.png", "README screenshot", "+1", "-0", selected: false),
            },
        };

        panel.Children.Add(files);
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

    private static Control FileRow(string state, string path, string detail, string added, string removed, bool selected)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("32,*,Auto"),
            ColumnSpacing = 8,
            MinHeight = 48,
        };

        grid.Children.Add(new TextBlock
        {
            Text = state,
            Foreground = state == "M" ? Brush("#E2C08D") : Brush("#7EE787"),
            FontFamily = FontFamily.Parse("Cascadia Code,Consolas,monospace"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var names = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = path,
                    Foreground = Brush("#DFE1E5"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock
                {
                    Text = detail,
                    FontSize = 11.5,
                    Foreground = Brush("#9DA0A8"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
        Grid.SetColumn(names, 1);
        grid.Children.Add(names);

        var churn = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = added, Foreground = Brush("#7EE787") },
                new TextBlock { Text = removed, Foreground = Brush("#FF7B72") },
            },
        };
        Grid.SetColumn(churn, 2);
        grid.Children.Add(churn);

        return new Border
        {
            Background = selected ? Brush("#343A40") : Brushes.Transparent,
            Padding = new Thickness(4, 0, 8, 0),
            Child = grid,
        };
    }

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}
