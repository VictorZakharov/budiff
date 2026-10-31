using System;
using System.Linq;
using System.Text;
using Budiff.CodeAnalysis;
using Xunit;

namespace Budiff.Core.Tests;

public sealed class CodeDebtTests
{
    [Fact]
    public void Shape_reads_levels_from_the_files_own_indent_step()
    {
        var twoSpaces = "a\n  b\n    c\n      d\n\n";
        Assert.Equal(new CodeShape(3, 0, 0), CodeShape.Measure(twoSpaces));

        // Tabs count four columns; with a four-column step that is one level each.
        var tabs = "a\r\n\tb\r\n\t\tc\r\n\t\t\t\t\t\tdeep\r\n";
        Assert.Equal(new CodeShape(6, 1, 0), CodeShape.Measure(tabs));

        // Block-comment continuations and blank lines are not nesting.
        var comment = "/**\n * doc\n */\nclass A\n{\n    int x;\n}\n";
        Assert.Equal(1, CodeShape.Measure(comment).MaxDepth);

        Assert.Equal(1, CodeShape.Measure("short\n" + new string('x', CodeShape.LongFrom + 1) + "\n").LongLines);
        Assert.Equal(default, CodeShape.Measure(string.Empty));
    }

    [Fact]
    public void Scanner_measures_the_shape_of_counted_files()
    {
        var stat = CodeScanner.CountBytes("src/a.cs", Encoding.UTF8.GetBytes("class A\n{\n    void M()\n    {\n        if (x)\n        {\n        }\n    }\n}\n"));
        Assert.Equal(2, stat.Shape.MaxDepth);
    }

    [Fact]
    public void Score_is_low_for_small_flat_files_and_high_for_big_deep_busy_ones()
    {
        var clean = File("Clean.cs", code: 80, new CodeShape(2, 0, 0), commits: 1);
        var deep = File("Deep.cs", code: 2500, new CodeShape(10, 900, 300), commits: 25);
        var midSize = File("Mid.cs", code: 900, new CodeShape(4, 10, 5), commits: 3);

        var cleanScore = CodeDebt.Score(clean, hasHistory: true);
        var deepScore = CodeDebt.Score(deep, hasHistory: true);
        var midScore = CodeDebt.Score(midSize, hasHistory: true);
        Assert.InRange(cleanScore, 0, 0.1);
        Assert.InRange(deepScore, 0.9, 1);
        Assert.True(cleanScore < midScore && midScore < deepScore);
        Assert.Equal("clean", CodeDebt.Describe(cleanScore));
        Assert.Equal("dilapidated", CodeDebt.Describe(deepScore));

        // Without history the structural metrics carry the whole score.
        Assert.InRange(CodeDebt.Score(deep, hasHistory: false), 0.9, 1.0 + 1e-9);

        var binary = File("logo.png", code: 0, default, commits: 0);
        binary.IsBinary = true;
        Assert.Equal(-1, CodeDebt.Score(binary, hasHistory: true));
    }

    [Fact]
    public void Debt_city_heights_follow_code_size_and_heat_is_the_score()
    {
        var root = new AnalysisNode(string.Empty, string.Empty, AnalysisNodeKind.Root);
        var folder = new AnalysisNode("src", "src", AnalysisNodeKind.Folder);
        root.Children.Add(folder);
        var small = File("src/Small.cs", code: 50, new CodeShape(1, 0, 0), commits: 0);
        var big = File("src/Big.cs", code: 3000, new CodeShape(9, 800, 100), commits: 30);
        folder.Children.Add(small);
        folder.Children.Add(big);
        folder.Lines = small.Lines + big.Lines;

        var city = CityLayout.Build(root, CityMode.Debt, 1_800_000_000, hasHistory: true);
        var smallBlock = city.Buildings.Single(b => b.Node == small);
        var bigBlock = city.Buildings.Single(b => b.Node == big);
        Assert.Equal(CityMode.Debt, city.Mode);
        Assert.True(bigBlock.Height > smallBlock.Height);
        Assert.Equal(CodeDebt.Score(big, hasHistory: true), bigBlock.Heat, 9);
        Assert.True(bigBlock.Heat > smallBlock.Heat);
    }

    [Fact]
    public void Every_city_leaves_out_data_prose_and_binary_files()
    {
        var root = new AnalysisNode(string.Empty, string.Empty, AnalysisNodeKind.Root);
        var src = new AnalysisNode("src", "src", AnalysisNodeKind.Folder);
        var data = new AnalysisNode("data", "data", AnalysisNodeKind.Folder);
        root.Children.Add(src);
        root.Children.Add(data);
        var code = File("src/App.cs", code: 200, new CodeShape(3, 0, 0), commits: 2);
        var readme = File("src/README.md", code: 40, default, commits: 1);
        var logo = File("src/logo.png", code: 0, default, commits: 1);
        logo.IsBinary = true;
        var bundle = File("src/vendor.min.js", code: 900, default, commits: 1);
        var json = File("data/portfolio.json", code: 100_000, new CodeShape(7, 50_000, 0), commits: 13);
        var yaml = File("data/config.yml", code: 30, default, commits: 1);
        src.Children.AddRange(new[] { code, readme, logo, bundle });
        data.Children.AddRange(new[] { json, yaml });

        foreach (var file in new[] { readme, logo, bundle, json, yaml })
        {
            Assert.Equal(-1, CodeDebt.Score(file, hasHistory: true));
        }

        // Every city stands only on code: a 100k-line JSON file no longer
        // towers over the Hotspots city.
        foreach (var mode in new[] { CityMode.Debt, CityMode.Hotspots, CityMode.Age })
        {
            var city = CityLayout.Build(root, mode, 1_800_000_000, hasHistory: true);
            Assert.Equal(new[] { code }, city.Buildings.Select(b => b.Node));
            Assert.DoesNotContain(city.Districts, d => d.Node == data);
        }
    }

    private static AnalysisNode File(string path, int code, CodeShape shape, int commits)
    {
        var node = new AnalysisNode(path.Split('/')[^1], path, AnalysisNodeKind.File)
        {
            Lines = new LineCounts(code, code / 10, code / 10),
            FileCount = 1,
            Shape = shape,
        };
        for (var i = 0; i < commits; i++)
        {
            node.CommitIds.Add(i);
        }

        return node;
    }
}
