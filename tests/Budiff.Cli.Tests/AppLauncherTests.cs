using System;
using System.IO;
using Xunit;

namespace Budiff.Cli.Tests;

public sealed class AppLauncherTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("budiff-launcher-").FullName;

    [Fact]
    public void FindNewestBuiltApp_ReturnsNullWithoutBinFolder()
    {
        Assert.Null(AppLauncher.FindNewestBuiltApp(_root));
    }

    [Fact]
    public void FindNewestBuiltApp_PicksNewestExeAndIgnoresDlls()
    {
        var debug = Path.Combine(_root, "bin", "Debug", "net10.0");
        var release = Path.Combine(_root, "bin", "Release", "net10.0");
        Directory.CreateDirectory(debug);
        Directory.CreateDirectory(release);

        var debugExe = Path.Combine(debug, "Budiff.App.exe");
        var releaseExe = Path.Combine(release, "Budiff.App.exe");
        File.WriteAllText(debugExe, string.Empty);
        File.WriteAllText(releaseExe, string.Empty);
        File.WriteAllText(Path.Combine(release, "Budiff.App.dll"), string.Empty);
        File.SetLastWriteTimeUtc(releaseExe, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(debugExe, DateTime.UtcNow);

        Assert.Equal(debugExe, AppLauncher.FindNewestBuiltApp(_root));

        File.SetLastWriteTimeUtc(releaseExe, DateTime.UtcNow.AddHours(1));

        Assert.Equal(releaseExe, AppLauncher.FindNewestBuiltApp(_root));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort - temp cleanup only
        }
    }
}
