# budiff

**`ls`, but for your diffs.** Run it in a folder and instantly see what changed.

![budiff GUI screenshot](docs/gui-screenshot.png)

budiff is a folder-scoped, read-only diff viewer. The CLI lists changed files under the current folder by default, including `+/-` churn and author/commit counts, then prints a side-by-side diff when you drill into a file. `--gitroot` widens scope to the whole repository. `-i` opens the desktop GUI on the same target.

## Install

Once published to NuGet:

```sh
dotnet tool install -g budiff
```

Release tags also attach self-contained, single-file CLI builds for users without the .NET SDK:

| Platform | Asset |
| --- | --- |
| Windows x64 | `budiff-win-x64.zip` |
| Linux x64 | `budiff-linux-x64.tar.gz` |
| macOS arm64 | `budiff-osx-arm64.tar.gz` |

One-line install scripts:

```powershell
irm https://raw.githubusercontent.com/VictorZakharov/budiff/main/scripts/install.ps1 | iex
```

```sh
curl -fsSL https://raw.githubusercontent.com/VictorZakharov/budiff/main/scripts/install.sh | sh
```

Set `BUDIFF_VERSION=v0.1.0` / `BUDIFF_INSTALL_DIR=...` for the shell script, or pass `-Version v0.1.0 -InstallDir ...` to the PowerShell script. Winget and Homebrew manifests are not published yet; the release assets and install scripts are the stable inputs for those manifests.

Native AOT is not enabled for the CLI yet. The CLI depends on LibGit2Sharp and native libgit2, which is not trim/AOT-friendly enough for a reliable first release. The release path is self-contained single-file publishing; AOT can be revisited if the git reader moves behind trim annotations or a git-CLI backend.

## Usage

```sh
budiff                 # list changes under the current folder
budiff --gitroot       # list changes for the whole repository
budiff src/App.cs      # print a side-by-side diff for one changed file
budiff --base main --head feature/foo --gitroot
budiff --json          # emit the listing as structured JSON
budiff src/App.cs --json
budiff -i              # open the GUI
```

The GUI provides the same changed-file listing, a base/head picker for branch or commit comparisons, and a syntax-highlighted side-by-side diff pane.

## Performance

The changed-file listing computes `+/-` churn from the current diff, then enriches each changed path with recent author and commit counts. History enrichment is path-scoped and scans at most 1,000 recent commits by default so `budiff` stays responsive in large repositories. Set `BUDIFF_HISTORY_MAX_COMMITS=0` to skip author/commit counts, or set it to a larger number when you want a deeper history window.

## Components

| Project | What it is |
| --- | --- |
| `Budiff.Core` | The diff engine - pure .NET, no UI, no git. |
| `Budiff.Avalonia` | The side-by-side diff control (Avalonia 12). |
| `Budiff.Avalonia.TextMate` | Optional TextMate syntax highlighting. |
| `Budiff.Cli` | The terminal tool (`budiff`). |
| `Budiff.App` | The standalone desktop GUI. |

## Build

```sh
dotnet build
dotnet test
```

Requires the .NET 10 SDK.

The UI snapshot test writes PNGs under the test output folder by default. Set `BUDIFF_UISNAPSHOT_DIR` to collect them elsewhere.

## License

[MIT](LICENSE).
