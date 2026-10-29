# budiff - v1 specification

This is the canonical scope for budiff v1. It is meant to be self-contained: an implementing agent should not need any other document. The budiff source itself is the reference implementation; section 5 maps where each responsibility lives.

## 1. Vision

budiff is **`ls` for diffs**. You run it in a folder and instantly see what changed: zero config, read-only, run-from-anywhere, folder-scoped (never a single file). Like `ls`, the value is that it is always there and frictionless.

- **Default (terminal):** `budiff` with no args shows the **current folder's** Code Analysis bird's-eye - folders/projects, LOC, file counts, period churn, commits, and authors - and prints a syntax-highlighted side-by-side diff when you drill into a file.
- **Scope:** the current folder by default (changes under the CWD subtree, like `ls` lists the current dir, not the whole repo). `--gitroot` (name TBD: `--gitroot` / `--repo` / `-r`) widens to the whole repository. The CLI never silently widens scope.
- **`-i`:** opens the desktop GUI on the same target.
- Read-only. Never mutates the repo.

Tagline: *"ls, but for your diffs."*

## 2. Components (projects)

- **`Budiff.Core`** - the diff engine. Pure .NET (BCL only), **no Avalonia, no git**. The diff algorithm + the row/result model + the binary/size policy.
- **`Budiff.Avalonia`** - the side-by-side diff **control** + its view-model. Depends on Avalonia and the `ISyntaxHighlighter` / `IDocumentPreviewer` interfaces, NOT on any implementation of them.
- **`Budiff.Avalonia.TextMate`** - a TextMate-backed `ISyntaxHighlighter` (the optional, heavier highlighting path).
- **`Budiff.Avalonia.Markdown`** - a Markdig-backed `IDocumentPreviewer`: the markdown **Preview** (the document formatted, as a block-level rich diff - changed blocks' old versions red, new versions green, unchanged runs foldable). Pictures load from the project (each side from its own revision; web pictures are not fetched) and open in a lightbox (wheel zoom around the pointer, drag to pan when zoomed, double-click for actual size, Left/Right or side buttons to step through the document's pictures, Esc to close); links open project files in budiff (with Back), scroll to `#headings`, or open web addresses in the browser (marked with the external-link icon). Text documents preview too - `*.txt` and extensionless files such as LICENSE or NOTICE (not code like Makefile) - as plain text by paragraph, or, best effort, as markdown when their content reads like it.
- **`Budiff.Cli`** - the terminal tool (`budiff`). Avalonia-free so it stays small and Native-AOT-friendly; references `Budiff.Core` + a small ANSI renderer.
- **`Budiff.App`** - the standalone desktop GUI (the README screenshot, the primary dogfooding target).
- A **sample** app and **test** projects.

## 3. Stack

- .NET 10; `Nullable` enabled.
- Avalonia 12.1.4; Avalonia.AvaloniaEdit 12.0.0 + AvaloniaEdit.TextMate 12.0.0; TextMateSharp.Grammars 2.0.4.
- Markdig 1.5.0 (the markdown preview).
- CommunityToolkit.Mvvm 8.4.2.
- The `git` command (2.11 or later, on PATH) is the git reader: status, diffs, file contents through one long-running `git cat-file --batch`, history, blame. No git library ships.
- Icons: the Material Design icons the app uses, inlined as path data (`AppIcon` in `Budiff.App`; the markdown preview keeps its own few). No icon package ships.
- Tests: xunit v3 (`xunit.v3`), with Avalonia.Headless.XUnit for UI snapshot tests; LibGit2Sharp 0.32.0 builds the test fixture repositories (tests only).
- License: MIT.

## 4. What v1 includes

- The **Code Analysis bird's-eye** (the `ls` surface): folder/project tree, period selector, sortable columns for Lines / Code / Blank / Comment / Files / Added / Removed / Commits / Authors / Recency (how long ago the newest commit in the period touched it: <1min, 5min, 3hr, 2d, 4mo, 1y), file search, extension filters, and author filters. Folder-scoped (CWD subtree) by default; `--gitroot` for the whole repo.
- **Drill-in side-by-side diff**: syntax-highlighted, intra-line highlighted, with the Rider-style centre "funnel" connectors (draggable to resize the split), collapse-unchanged, gap expansion, and line wrap (on by default). Markdown files can switch to a formatted **Preview** of the change. A **View / Diff** switch at the top of the pane (Ctrl+D flips it) shows the file as it is now instead - only the new side, with no change colours, bars, removed lines, gaps or funnel, still highlighted, numbered, wrapped and blamed (blame is per line, so it stays); a markdown Preview shows the new document. Diff is the default when a window opens; the choice then carries from file to file (docked and floating pane alike). The switch is not shown where there is nothing to hide or nothing left to show: a plain file view (no git), an unchanged file, a deleted file, a folder summary, a placeholder.
- A **commit / branch picker** so you can diff points other than working-tree-vs-HEAD.
- Both surfaces in **terminal** (CLI) and **desktop GUI** (`-i` / `Budiff.App`).
- **MCP server** (`budiff mcp`) over stdio for AI agents, exposing `list_changes`, `get_file_diff`, and `repo_overview` from the same structured git + Core engine.
- Binary / very-large files: a short placeholder instead of a garbage text diff.

Out of v1 (later, see section 9): deeper agent-specific repo summaries beyond the MCP tools above.

## 5. Where things live

### `Budiff.Core` (pure)
- `LineDiff.cs` - the diff algorithm (LCS for small inputs, Myers O(ND) for large); `InlineDiff.cs` - intra-line changed spans.
- the model records (`Models.cs`):
  - `DiffRow (int? OldNo, string? OldText, int? NewNo, string? NewText, DiffKind Kind)`
  - `DiffKind { Unchanged, Added, Removed, Modified }`
  - `DiffResult (string? Placeholder, IReadOnlyList<DiffRow>? Rows)`
  - `BlobInfo (string Text, long Bytes, bool IsBinary)`
  - `GitFileState` enum
  - NOTE: `DiffRun (string Text, IBrush Brush)` carries an Avalonia brush, so it belongs in `Budiff.Avalonia`, not Core. Core's syntax model stays brush-free; the Avalonia layer maps it to `IBrush`.
- `DiffLoader.cs` - the **pure** policy only: `Build`, `LooksBinary`, `BuildAgainstWorking`, and the size/binary thresholds. Loading blobs from git belongs with the git reader (5.5).

### `Budiff.Avalonia` (the control)
- `DiffView.axaml` (+ `.axaml.cs`), `DiffFunnelGutter.cs`, `DiffInlines.cs`, `CodeClip.cs` (a code cell: clipped, shifted by the shared horizontal scroll), and `DiffRowsHost.cs` (the rows list; a recycled row keeps its controls and only rebinds).
- **`DiffViewModel`**, the reusable core:
  - off-thread `BeginLoad(Func<DiffResult>)` with a **generation counter** (a diff re-pointed mid-load drops the stale result),
  - `SetDiff(rows)`,
  - **viewport-first incremental highlighting** (publish ~64 rows at a time so the visible top colours in a few ms; a full re-pass when collapsed gaps meant the first pass had to guess grammar state across them),
  - **collapse-unchanged** with +/-3 lines of context, and **gap expansion**,
  - the bound state: `Rows`, `IsHighlighting`, `IsPlaceholder`, `Placeholder`, `HorizontalScroll`, `MaxScroll`.
- **Themeable colours** - Avalonia `StyledProperty`s + a default `ResourceDictionary` (`Themes/BudiffTheme.axaml`), so a consumer can theme it and it looks good out of the box:
  - added-line background `#3331C36B`, removed-line background `#33E05252`,
  - funnel added `#5531C36B`, removed `#55E05252`, modified `#55E2C08D`,
  - editor background `#1E1F22`, toolbar `#3C3F41`, border `#1E1F22`, text `#DFE1E5`, muted text `#9DA0A8`,
  - default syntax token colour `#D4D4D4` (DarkPlus).
- **`ISyntaxHighlighter`**: per file, a highlighting session that threads the grammar state line by line and returns coloured runs. The control depends only on this interface; with no highlighter it renders plain text.

### `Budiff.Avalonia.TextMate`
- `TextMateSyntaxHighlighter.cs` - the `ISyntaxHighlighter` implementation: the DarkPlus theme, the grammar + brush caches, the off-thread tokenization serialized on a single lock ("gate"), and the ~6000-line highlight cap.

### The Code Analysis bird's-eye
- `Budiff.Core/CodeAnalysis/` - `AnalysisTreeBuilder.cs`, `AnalysisNode.cs`, `CodeScanner.cs`, `LineCounter.cs`, and `CommentSyntax.cs`: the folder/project tree, LOC scanner, extension stats, filters, and per-node aggregates.
- `Budiff.Shared/Git/GitHistory.cs` - commits and authors per path plus the windowed net-diff metric, loaded by the git reader (5.5).

### 5.5 The git reader (lives in the CLI / App, never in Core/Avalonia)
- `src/Budiff.Shared/Git/*.cs` (the `git` command), compiled into both `Budiff.Cli` and `Budiff.App`: status, HEAD / commit blobs, history, branches, blame, pull requests. It reads the repo and feeds a `DiffResult` into `DiffViewModel.BeginLoad`. **Core and Avalonia never reference git** - the CLI and the App own the git reader and inject text / results into the engine.

## 6. `Budiff.Cli`

- `budiff` (no args) -> terminal mode, **current folder**: print the Code Analysis bird's-eye; drilling into a file prints the side-by-side ANSI diff. Folder-scoped to the CWD subtree.
- `budiff --gitroot` -> widen analysis scope to the whole repository (flag name TBD).
- `budiff -i` -> launch `Budiff.App` on the target.
- Keep **Avalonia out** of the CLI (reference `Budiff.Core` + a small ANSI renderer) so it stays tiny and Native-AOT-friendly.
- **Architectural rule:** render the terminal output from a **structured change-model**, never by concatenating ANSI strings ad hoc. Keeping the data model separate from the renderer is what made `--json` and the MCP server (section 9) cheap to add.

## 7. `Budiff.App` (standalone GUI)

A thin desktop window around the diff control - **no Dock dependency**:
- open a repo (or the CWD); **repo-root detection**: when launched from a subfolder, *offer* to widen to the repo root (this suggestion is GUI-only; the CLI does not do it),
- a **Code Analysis bird's-eye** panel with grouping, period, sort, search, extension filters, and author filters; file rows carry a Material icon for their type (tinted in the language's colour; `*.module.ts` and `*.spec.ts` / `*.test.ts` get their own), a trimmed name shows in full as a tooltip, and a column chooser (right-click the header, or its corner button) turns any column but Name off, all on/off at once, or back to the default - remembered across restarts in `settings.json` under the user's app-data folder (`BUDIFF_SETTINGS_DIR` overrides); right-clicking a row - or a building or district in the code city - offers **Show in Explorer** for that file or folder (Reveal in Finder on macOS; a file gone from disk shows its nearest folder), and a tree folder's menu also has **Expand all** / **Collapse all** for that folder and everything under it (other folders stay as they are); the tree is sorted by Name by default - folders first, then files, A-Z (click Name again for Z-A within each; a number column's header sorts by it); **Refresh** reloads the bird's-eye in place: the tree stays on screen while it scans, then only rows that changed change (new numbers, new files in, gone files out) - the sort, the open and closed folders, the filters and the selected row stay, the selected row at the same height in the tree - and so does what the diff pane shows (the same file, where it was scrolled to, or the same folder's summary); moving to another file swaps the old file's rows and blame for the new ones together (a blame slower than ~300 ms fills its column in afterwards),
- selection -> the `DiffView` side-by-side pane (double-click or Enter on a file opens it); a file opens where it was last left (until restart), or - opened for the first time - at the top (a TypeScript file just past its header comments and imports, but never past a change). While the pane is open it follows the bird's-eye selection - arrow keys, Home/End, PageUp/PageDown, type-to-search or a single click - with no Enter: a file shows its diff, a folder (or project) a **folder summary** in its place (files changed, new, modified and deleted in the period, lines added and removed, and its changed files, biggest change first, from the bird's-eye's own numbers; a new file is one whose every line is added, a deleted one a path the period's net diff only removes from - not counted in All time). A held arrow key shows the first row and then only the row it stops on; focus stays in the tree. With the pane closed the keys only move the selection,
- a commit / branch picker to diff other points,
- a **commit picker** on the summary line: its "N commits" is a link that drops down the commits it counts (the applied period's, less those of files the filters hide), newest first - subject, short sha, author, how long ago (as Recency says it) and the lines each added and removed in the folder - after an "All" entry for the whole period. Picking a commit scopes the whole view - bird's-eye and diff - to it, as the commit window's full window does (the period list's "Commit" entry, the commit chip); the list then still shows the period, the commit marked, and picking it again or All returns to the period (or pull request) it came from. The list comes from the history already loaded (`git log` carries each commit's subject); no git runs to open it,
- a **commit window**: clicking a blame cell opens the commit that last touched the line in a small window - id (copyable), author, date and message on the left, the files it changed (a folder tree with +/- counts) on the right; the PR it is part of (any state, from GitHub via `gh`) shows as a pill with its number in the state colour, which opens the PR; Esc closes it. Its expand button, or a double-clicked file or folder, opens a full window on the same folder (so the same files) scoped to that commit - temporary: it always shows the Added / Removed columns and never saves its column choices; the double-clicked file opens, a folder is expanded and selected; co-author / sign-off trailers show as names (the period list gains a "Commit" entry; the diffs compare the commit with its first parent, a root commit with nothing), with that file open,
- a **pull request period**: the period list's "Pull request..." entry opens a picker (the user's PRs by default, 10 at a time with more on scroll; filter by author - a drop-down of Mine / Anyone / the repo's people, fetched on its first drop-down and kept until restart, or a typed login -, state, and free text / `#number` / GitHub qualifiers). Opened on a folder (not `--gitroot`), the picker lists only PRs that touch it: merged ones from the folder's first-parent history on the default branch (GitHub is asked only which PR each of those commits was), open ones and author/text searches through GitHub search, kept when their merge-base -> head range changes the folder (checked locally when both commits are present, else from the PR's file list); an explicit `#number` is shown regardless. The bird's-eye then shows only the files the PR changes (counted at its head, churn = the merge-base -> head net diff, commits/authors from the PR's commits) and the drill-in diffs the PR's merge-base against its head. PR data comes from the GitHub CLI (`gh`); a PR whose commits are not local is fetched from `refs/pull/N/head` without writing any ref.

This is the surface the README screenshots show.

## 8. CI / repo hygiene

- GitHub Actions: build + `dotnet test` on Windows and Linux (exclude any `Integration` category); a version tag packs, publishes the `budiff` tool package to NuGet.org (trusted publishing, after approval) and attaches the CLI archives to the GitHub release - see `docs/RELEASING.md`.
- `MIT` LICENSE; `THIRD-PARTY-NOTICES.md` (+ `licenses/`) for what the tool package redistributes; this `SPEC.md`; a README with GUI screenshots; `.editorconfig`.
- **ASCII-only code comments** (no em-dashes, curly quotes, ellipses, or arrow glyphs; write `-`, `...`, `->`). Fancy Unicode is fine in on-screen UI strings.

## 9. AI agents as first-class users

budiff is consumable by AI coding agents, not only humans (agents constantly need "what changed here?" and today parse verbose raw `git diff`).
- **`--json` output** - the listing + diffs as structured JSON (per-file status, `+/-` churn, authors, hunks with intra-line kinds). The single most useful affordance; enabled by the section-6 structured-change-model rule.
- **Two-tier = token-efficient by design** - the ls-then-drill model is how a context-limited agent should consume changes: cheap overview first, then only the file diffs it needs.
- **MCP server** (`budiff mcp`) exposes `list_changes(scope)` / `get_file_diff(path, base, head)` / `repo_overview(period)` over stdio. It reuses the same Core + git engine; no CLI-text parsing.
- **Future verbosity knobs** (collapse-unchanged, large-file placeholders, `--summary`) can further cap output to an agent's context budget.

## 10. Build order

1. `Budiff.Core` - LineDiff + the pure models + the binary/size policy; unit tests.
2. `Budiff.Avalonia` - DiffView + DiffViewModel + funnel + `ISyntaxHighlighter` + the default theme; a headless UI-snapshot test.
3. `Budiff.Avalonia.TextMate`.
4. `Budiff.App` - the GUI shell + the bird's-eye listing (GitHistory / AnalysisTreeBuilder + the git reader).
5. `Budiff.Cli`.
6. CI + README + screenshot.
