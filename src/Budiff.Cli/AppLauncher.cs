using System.Diagnostics;
using System.Text;

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

        var ready = false;
        string? failure = null;
        var spinner = Console.IsOutputRedirected ? null : new ConsoleSpinner(output);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < WindowWaitTimeoutMs)
            {
                process.Refresh();
                if (process.HasExited)
                {
                    failure = $"Budiff.App exited before showing a window (exit code {process.ExitCode}).";
                    break;
                }

                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    ready = true;
                    break;
                }

                spinner?.Advance();
                Thread.Sleep(80);
            }
        }
        finally
        {
            spinner?.Dispose();
        }

        if (failure is not null)
        {
            output.WriteLine();
            Console.Error.WriteLine(failure);
            return 1;
        }

        // On timeout the app may still be coming up, so hand the prompt back
        // without failing the launch.
        output.WriteLine(ready ? "ready." : string.Empty);
        return 0;
    }

    // In-place console spinner. Hides the cursor while spinning - rewriting
    // the frame cell with a visible cursor flickers it back and forth - and
    // restores it on Dispose and on Ctrl+C. Uses braille frames where the
    // terminal is known to render them; classic conhost fonts often lack the
    // glyphs, so it falls back to ASCII there.
    private sealed class ConsoleSpinner : IDisposable
    {
        private static readonly string Esc = ((char)27).ToString();
        private static readonly string[] BrailleFrames =
            { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        private static readonly string[] AsciiFrames = { "|", "/", "-", "\\" };

        private readonly string[] _frames;
        private readonly Encoding? _previousEncoding;
        private readonly ConsoleCancelEventHandler _onCancel;
        private int _frame;
        private bool _drawn;

        public ConsoleSpinner(TextWriter output)
        {
            var fancyTerminal = Environment.GetEnvironmentVariable("WT_SESSION") is { Length: > 0 }
                || Environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode";
            if (fancyTerminal)
            {
                // Braille frames need UTF-8 console output; the setter can
                // throw on exotic hosts, in which case stay ASCII.
                try
                {
                    _previousEncoding = Console.OutputEncoding;
                    Console.OutputEncoding = Encoding.UTF8;
                    _frames = BrailleFrames;
                }
                catch (Exception)
                {
                    _previousEncoding = null;
                    _frames = AsciiFrames;
                }
            }
            else
            {
                _frames = AsciiFrames;
            }

            output.Write(Esc + "[?25l");
            output.Flush();
            // A Ctrl+C mid-spin would otherwise leave the console with a
            // hidden cursor (and a switched codepage) after we are gone.
            _onCancel = (_, _) => Restore();
            Console.CancelKeyPress += _onCancel;
        }

        public void Advance()
        {
            // Write through Console.Out, not the writer captured before the
            // encoding switch - the old writer still encodes with the old
            // codepage and would mangle the braille glyphs.
            Console.Out.Write(Esc + "[36m" + _frames[_frame++ % _frames.Length] + Esc + "[0m\b");
            _drawn = true;
        }

        public void Dispose()
        {
            Console.CancelKeyPress -= _onCancel;
            if (_drawn)
            {
                Console.Out.Write(" \b");
            }

            Restore();
        }

        private void Restore()
        {
            Console.Out.Write(Esc + "[?25h");
            Console.Out.Flush();
            if (_previousEncoding is not null)
            {
                try
                {
                    Console.OutputEncoding = _previousEncoding;
                }
                catch (Exception)
                {
                    // Leaving UTF-8 behind is harmless compared to throwing here.
                }
            }
        }
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
