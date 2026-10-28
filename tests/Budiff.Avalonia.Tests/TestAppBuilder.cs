using System;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(Budiff.Avalonia.Tests.TestAppBuilder))]

namespace Budiff.Avalonia.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

public sealed class TestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = global::Avalonia.Styling.ThemeVariant.Dark;
        Styles.Add(new FluentTheme());

        // Avalonia 12.1 finds an avares:// assembly's precompiled XAML only
        // once that assembly is loaded. Nothing has loaded Budiff.Avalonia yet
        // when a test runs on its own (in the full suite another test usually
        // has), so the theme failed with "No precompiled XAML found".
        System.Reflection.Assembly.Load(typeof(global::Budiff.Avalonia.DiffView).Assembly.GetName());
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Budiff.Avalonia.Tests"))
        {
            Source = new Uri("avares://Budiff.Avalonia/Themes/BudiffTheme.axaml"),
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // BUDIFF_TEST_SCALE=1.25 runs every window at that display scale (the
        // suite runs at 1 and at 1.25 - a common desktop setting, where a row
        // is no whole number of device pixels).
        if (TestScale.Value != 1)
        {
            global::Avalonia.Controls.Window.WindowOpenedEvent.AddClassHandler<global::Avalonia.Controls.Window>(
                (window, _) => window.SetRenderScaling(TestScale.Value));
        }

        base.OnFrameworkInitializationCompleted();
    }
}

public static class TestScale
{
    public static double Value { get; } =
        double.TryParse(Environment.GetEnvironmentVariable("BUDIFF_TEST_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale) && scale > 0
            ? scale
            : 1;

    // A size in device pixels (what a captured frame measures).
    public static int Px(double dip) => (int)Math.Round(dip * Value);
}
