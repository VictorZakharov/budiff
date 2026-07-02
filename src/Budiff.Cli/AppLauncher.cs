using System.Diagnostics;

internal static class AppLauncher
{
    // The GUI takes about a second to first paint; give up on the window
    // well after that so a slow cold start still gets the "ready" moment.
    private const int WindowWaitTimeoutMs = 15000;

    // A real launch failure (permissions, broken exe) is a different problem
    // than "no GUI found"; remember it so the final message can say which.
    private static string? _lastLaunchError;

    public static int Launch(CliOptions options, TextWriter output)
    {
        var start = Environment.CurrentDirectory;
        _lastLaunchError = null;
        var configured = Environment.GetEnvironmentVariable("BUDIFF_APP_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && TryStart(configured, start, options.GitRoot) is { } fromEnv)
        {
            return WaitForFirstWindow(fromEnv, output);
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
                if (File.Exists(path) && TryStart(path, start, options.GitRoot) is { } fromPackage)
                {
                    return WaitForFirstWindow(fromPackage, output);
                }
            }
        }

        // A portable (RID-less) gui/ layout has no apphost for this OS - run
        // the assembly through the dotnet host instead. The shipped package is
        // win-x64 only, so this serves hand-assembled portable layouts.
        var bundledDll = Path.Combine(baseDir, "gui", "Budiff.App.dll");
        if (File.Exists(bundledDll) && TryStartDll(bundledDll, start, options.GitRoot) is { } fromDll)
        {
            return WaitForFirstWindow(fromDll, output);
        }

        // Dev fallback: inside the budiff repo, launch the exe a normal build
        // already produced. Never build here - `dotnet run` was slow and opened
        // an extra console window.
        var appDir = FindDevAppDirectory(Directory.GetCurrentDirectory());
        if (appDir is not null)
        {
            var built = FindNewestBuiltApp(appDir);
            if (built is not null && TryStart(built, start, options.GitRoot) is { } fromDev)
            {
                return WaitForFirstWindow(fromDev, output);
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

    // Hold the prompt with a spinner until the GUI actually has a window, so
    // the launch never reads as "nothing happened". Returns non-zero if the
    // app died before showing one.
    private static int WaitForFirstWindow(Process process, TextWriter output)
    {
        output.Write("Opening budiff GUI... ");
        if (!OperatingSystem.IsWindows())
        {
            // MainWindowHandle is Windows-only; elsewhere just report the start.
            output.WriteLine();
            return 0;
        }

        var spinner = new[] { '|', '/', '-', '\\' };
        var animate = !Console.IsOutputRedirected;
        var stopwatch = Stopwatch.StartNew();
        var frame = 0;
        while (stopwatch.ElapsedMilliseconds < WindowWaitTimeoutMs)
        {
            process.Refresh();
            if (process.HasExited)
            {
                output.WriteLine();
                Console.Error.WriteLine($"Budiff.App exited before showing a window (exit code {process.ExitCode}).");
                return 1;
            }

            if (process.MainWindowHandle != IntPtr.Zero)
            {
                output.WriteLine("ready.");
                return 0;
            }

            if (animate)
            {
                output.Write(spinner[frame++ % spinner.Length]);
                output.Write('\b');
            }

            Thread.Sleep(80);
        }

        // No window within the timeout; the app may still be coming up, so
        // hand the prompt back without failing the launch.
        output.WriteLine();
        return 0;
    }

    private static Process? TryStart(string executable, string startPath, bool gitRoot)
    {
        try
        {
            // UseShellExecute detaches the GUI from this console entirely - the
            // terminal gets its prompt back and never waits on the GUI's lifetime.
            var args = $"\"{startPath}\"" + (gitRoot ? " --gitroot" : string.Empty);
            return Process.Start(new ProcessStartInfo(executable, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _lastLaunchError = $"{executable}: {ex.Message}";
            return null;
        }
    }

    private static Process? TryStartDll(string assemblyPath, string startPath, bool gitRoot)
    {
        try
        {
            var args = $"\"{assemblyPath}\" \"{startPath}\"" + (gitRoot ? " --gitroot" : string.Empty);
            return Process.Start(new ProcessStartInfo("dotnet", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _lastLaunchError = $"dotnet {assemblyPath}: {ex.Message}";
            return null;
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
