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
        Styles.Add(new FluentTheme());
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Budiff.Avalonia.Tests"))
        {
            Source = new Uri("avares://Budiff.Avalonia/Themes/BudiffTheme.axaml"),
        });
    }

    public override void OnFrameworkInitializationCompleted() => base.OnFrameworkInitializationCompleted();
}
