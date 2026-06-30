# budiff

**`ls`, but for your diffs.** Run it in a folder and instantly see what changed.

> **Status: scaffolding / pre-release.** v1 is being built from [`SPEC.md`](SPEC.md). Nothing is released yet.

budiff is a folder-scoped, read-only, run-from-anywhere diff viewer - the diff equivalent of `ls`. In a terminal it lists the current folder's changes (files, `+/-` churn, author/commit counts) and renders a syntax-highlighted, intra-line **side-by-side** diff when you drill into a file. `-i` opens the same view as a desktop GUI. `--gitroot` widens scope from the current folder to the whole repository.

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

## License

[MIT](LICENSE).
