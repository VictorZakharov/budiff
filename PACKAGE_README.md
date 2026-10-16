# budiff

**`ls`, but for your diffs.** Run it in a folder and instantly see what changed.

budiff is a folder-scoped, read-only diff viewer. The CLI opens with a Code Analysis bird's-eye - folders or projects, line/code/blank/comment counts, file counts, period churn, commits, and authors - and prints a syntax-highlighted side-by-side diff when you drill into a file. `budiff -i` opens the desktop GUI (Windows) on the same folder.

![budiff's desktop app: the bird's-eye with a file's side-by-side diff](https://raw.githubusercontent.com/VictorZakharov/budiff/main/docs/screenshots/diff.png)

## Install the tool

```sh
dotnet tool install -g budiff
```

```sh
budiff                 # the bird's-eye for the current folder
budiff --gitroot       # ... for the whole repository
budiff src/App.cs      # a side-by-side diff for one file
git diff | budiff      # render a unified diff from stdin side by side
budiff --json          # structured output for scripts and AI agents
budiff mcp             # a stdio MCP server for AI agents
budiff -i              # the desktop GUI (Windows)
```

Needs the .NET 10 SDK and git 2.11 or later on PATH.

## Libraries

The diff engine and the Avalonia side-by-side control ship as libraries:

| Package | What it is |
| --- | --- |
| `Budiff.Core` | The diff engine - pure .NET, no UI, no git. |
| `Budiff.Avalonia` | The side-by-side diff control (Avalonia 12). |
| `Budiff.Avalonia.TextMate` | Optional TextMate syntax highlighting. |
| `Budiff.Avalonia.Markdown` | Optional formatted markdown preview (a block-level rich diff). |

## More

Documentation, screenshots, and issues: <https://github.com/VictorZakharov/budiff>.

MIT licensed. The `budiff` tool package includes third-party components under their own licenses - see `THIRD-PARTY-NOTICES.md` in the package.
