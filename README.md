# budiff

[![CI](https://github.com/VictorZakharov/budiff/actions/workflows/ci.yml/badge.svg)](https://github.com/VictorZakharov/budiff/actions/workflows/ci.yml)

**`ls`, but for your diffs.** Run it in a folder and instantly see what changed.

![budiff CLI bird's-eye](docs/cli-birdseye.svg)

budiff is a folder-scoped, read-only diff viewer. The CLI opens with a Code Analysis bird's-eye: folders or projects, line/code/blank/comment counts, file counts, period churn, commits, and authors. Drill into a file to print a side-by-side diff. `--gitroot` widens scope to the whole repository. `-i` opens the desktop GUI on the same target:

![budiff GUI bird's-eye](docs/gui-screenshot.png)

Drilling into a file docks a syntax-highlighted side-by-side diff next to a compact bird's-eye - resizable, collapsible, and able to float into its own window:

![budiff GUI diff drill-in](docs/gui-diff.png)

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

The GUI inside the tool package ships as a Native AOT publish (Windows), so `budiff -i` opens in well under a second. The self-contained CLI release builds are single-file JIT publishes; per-OS Native AOT CLI binaries are a possible future upgrade (AOT cannot cross-compile, so they need per-OS release legs).

## Usage

```sh
budiff                 # show the bird's-eye for the current folder
budiff --gitroot       # show the bird's-eye for the whole repository
budiff --days 90       # widen the analysis window (default 30; 0 = all time)
budiff src/App.cs      # print a syntax-highlighted side-by-side diff for one file
budiff --base main --head feature/foo --gitroot
budiff --json          # emit the listing as structured JSON
budiff src/App.cs --json
git diff | budiff      # render a unified diff from stdin as side-by-side output
budiff --stdin         # explicit stdin pager mode
budiff mcp             # run the stdio MCP server for AI agents
budiff -i              # open the GUI
```

Outside a git repository, `budiff` still works: it falls back to a plain folder analysis (lines / code / blank / comment - no churn, commits, or authors), and in the GUI double-clicking a file shows its current content. Terminal output is colored, and file diffs are syntax-highlighted with the same DarkPlus grammars the GUI uses; set `BUDIFF_FORCE_COLOR=1` to keep color when piping (e.g. `| less -R`).

The stdin mode is a pure unified-diff transform; it does not need repository access. The GUI ships inside the tool package, so `budiff -i` works from any folder right after `dotnet tool install` (set `BUDIFF_APP_PATH` only to point at a different build). The GUI provides the same bird's-eye with filters, grouping, and period controls (including a custom window in days). The period list also offers **Pull request...**: pick a PR (yours by default; search by author, state, `#number`, or text) and the bird's-eye shows just that PR's changes, with the drill-in diffing its merge-base against its head - this uses the [GitHub CLI](https://cli.github.com) (`gh auth login`). The diff pane stays hidden until you drill into a file; it then docks on the right (the bird's-eye compacts to its core columns, and drops Authors, Commits, Code and Lines in turn as you narrow it) with a base-branch picker and a working-tree toggle in its toolbar, the split is resizable, the funnel gutter between the old and new code drags to resize the side-by-side halves (double-click re-centers), long lines wrap (toggle **Wrap**), markdown files can switch to a formatted **Preview** that marks changed blocks (old in red, new in green), the tree can collapse away entirely, and the diff can pop out into its own window - handy on a second monitor.

## For AI agents

Agents work the same way humans do here: list what changed, then drill into just the files that matter - far cheaper than paging whole diffs into context.

- `budiff --json` (and `budiff <file> --json`) emit the change listing / a single file diff as structured JSON. Add `--context 3` to fold long unchanged runs into `Gap` rows (first hidden line numbers + a `hiddenLines` count) instead of paying for the whole file.
- `budiff mcp` runs a stdio MCP server exposing `list_changes`, `get_file_diff`, and `repo_overview`. `get_file_diff` collapses to 3 context lines by default; pass `contextLines` to widen it, or `null` for the full file.

Register with Claude Code in one line:

```sh
claude mcp add budiff -- budiff mcp
```

Or in any MCP-capable client's config:

```json
{
  "mcpServers": {
    "budiff": {
      "command": "budiff",
      "args": ["mcp"]
    }
  }
}
```

## Performance

The bird's-eye scans files under the selected folder and uses git's path-scoped history for the selected period. The changed-file drill-in still bounds recent author/commit enrichment to 1,000 commits by default. Set `BUDIFF_HISTORY_MAX_COMMITS=0` to skip those drill-in counts, or set it to a larger number when you want a deeper window.

## Components

| Project | What it is |
| --- | --- |
| `Budiff.Core` | The diff engine - pure .NET, no UI, no git. |
| `Budiff.Avalonia` | The side-by-side diff control (Avalonia 12). |
| `Budiff.Avalonia.TextMate` | Optional TextMate syntax highlighting. |
| `Budiff.Avalonia.Markdown` | Optional formatted markdown preview (a block-level rich diff). |
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
