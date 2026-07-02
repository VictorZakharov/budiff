using System.Diagnostics;

internal static class AppLauncher
{
    // A real launch failure (permissions, broken exe) is a different problem
    // than "no GUI found"; remember it so the final message can say which.
    private static string? _lastLaunchError;

    public static int Launch(CliOptions options)
    {
        var start = Environment.CurrentDirectory;
        _lastLaunchError = null;
        var configured = Environment.GetEnvironmentVariable("BUDIFF_APP_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && TryStart(configured, start, options.GitRoot))
        {
            return 0;
        }

        // The GUI ships inside the tool package under gui/win-x64/ as a
        // RID-specific ReadyToRun publish (see Budiff.Cli.csproj), so only
        // probe it on Windows - its native deps are win-x64 only. The bare
        // gui/ and exe-next-to-CLI probes cover hand-assembled layouts.
        var baseDir = AppContext.BaseDirectory;
        foreach (var name in new[] { "Budiff.App.exe", "Budiff.App" })
        {
            var candidates = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                candidates.Add(Path.Combine(baseDir, "gui", "win-x64", name));
            }

            candidates.Add(Path.Combine(baseDir, "gui", name));
            candidates.Add(Path.Combine(baseDir, name));
            foreach (var path in candidates)
            {
                if (File.Exists(path) && TryStart(path, start, options.GitRoot))
                {
                    return 0;
                }
            }
        }

        // A portable (RID-less) gui/ layout has no apphost for this OS - run
        // the assembly through the dotnet host instead. The shipped package is
        // win-x64 only, so this serves hand-assembled portable layouts.
        var bundledDll = Path.Combine(baseDir, "gui", "Budiff.App.dll");
        if (File.Exists(bundledDll) && TryStartDll(bundledDll, start, options.GitRoot))
        {
            return 0;
        }

        // Dev fallback: inside the budiff repo, launch the exe a normal build
        // already produced. Never build here - `dotnet run` was slow and opened
        // an extra console window.
        var appDir = FindDevAppDirectory(Directory.GetCurrentDirectory());
        if (appDir is not null)
        {
            var built = FindNewestBuiltApp(appDir);
            if (built is not null && TryStart(built, start, options.GitRoot))
            {
                return 0;
            }

            Console.Error.WriteLine(_lastLaunchError is { } devError
                ? $"Could not start Budiff.App ({devError})."
                : $"Budiff.App at {appDir} is not built. Run: dotnet build");
            return 1;
        }

        Console.Error.WriteLine(_lastLaunchError is { } error
            ? $"Could not start Budiff.App ({error})."
            : "Could not find Budiff.App. Set BUDIFF_APP_PATH to the GUI executable.");
        return 1;
    }

    private static bool TryStart(string executable, string startPath, bool gitRoot)
    {
        try
        {
            // UseShellExecute detaches the GUI from this console entirely - the
            // terminal gets its prompt back and never waits on the GUI's lifetime.
            var args = $"\"{startPath}\"" + (gitRoot ? " --gitroot" : string.Empty);
            Process.Start(new ProcessStartInfo(executable, args) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            _lastLaunchError = $"{executable}: {ex.Message}";
            return false;
        }
    }

    private static bool TryStartDll(string assemblyPath, string startPath, bool gitRoot)
    {
        try
        {
            var args = $"\"{assemblyPath}\" \"{startPath}\"" + (gitRoot ? " --gitroot" : string.Empty);
            Process.Start(new ProcessStartInfo("dotnet", args) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            _lastLaunchError = $"dotnet {assemblyPath}: {ex.Message}";
            return false;
        }
    }

    private static string? FindDevAppDirectory(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Budiff.App");
            if (File.Exists(Path.Combine(candidate, "Budiff.App.csproj")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    internal static string? FindNewestBuiltApp(string appDirectory)
    {
        var bin = Path.Combine(appDirectory, "bin");
        if (!Directory.Exists(bin))
        {
            return null;
        }

        return Directory.EnumerateFiles(bin, "Budiff.App*", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) is "Budiff.App.exe" or "Budiff.App")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
