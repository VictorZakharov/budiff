using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Budiff.App;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the main window ends the process even if a secondary window
            // (e.g. the floating diff) is somehow still around.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var args = desktop.Args ?? Array.Empty<string>();
            var gitRoot = args.Any(a => a.Equals("--gitroot", StringComparison.OrdinalIgnoreCase)
                                        || a.Equals("--repo", StringComparison.OrdinalIgnoreCase)
                                        || a.Equals("-r", StringComparison.OrdinalIgnoreCase));
            var startPath = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal)) ?? Environment.CurrentDirectory;
            desktop.MainWindow = new MainWindow(startPath, gitRoot);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
