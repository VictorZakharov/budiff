# budiff

**`ls`, but for your diffs.** Run it in a folder and instantly see what changed.

![budiff GUI screenshot](docs/gui-screenshot.png)

budiff is a folder-scoped, read-only diff viewer. The CLI lists changed files under the current folder by default, including `+/-` churn and author/commit counts, then prints a side-by-side diff when you drill into a file. `--gitroot` widens scope to the whole repository. `-i` opens the desktop GUI on the same target.

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
