# budiff - v1 specification

This is the canonical scope for budiff v1. It is meant to be self-contained: an implementing agent should not need any other document. It consolidates the design decisions made in the Burin tracker (issues #92 and #93 of the `coderide` repository, private at the time of writing).

## 0. Reference implementation (read the real source)

budiff is **extracted from the Burin IDE**, which lives in the **`coderide`** repository. On the original development machine it is checked out as a **sibling folder**:

- `../coderide` (optional - only present on that machine)

Section 5 maps the exact Burin source files to budiff projects. The mapping tells you *where things go*; when the checkout exists, **open the actual files under `../coderide` and read them for the *how*** (algorithms, threading, the highlighting pipeline, the funnel geometry, the git calls). Port and adapt that code - do not take a dependency on Burin/coderide. When it does not exist, the budiff source itself is the reference: v1 has already ported everything section 5 maps.

## 1. Vision

budiff is **`ls` for diffs**. You run it in a folder and instantly see what changed: zero config, read-only, run-from-anywhere, folder-scoped (never a single file). Like `ls`, the value is that it is always there and frictionless.

- **Default (terminal):** `budiff` with no args shows the **current folder's** Code Analysis bird's-eye - folders/projects, LOC, file counts, period churn, commits, and authors - and prints a syntax-highlighted side-by-side diff when you drill into a file.
- **Scope:** the current folder by default (changes under the CWD subtree, like `ls` lists the current dir, not the whole repo). `--gitroot` (name TBD: `--gitroot` / `--repo` / `-r`) widens to the whole repository. The CLI never silently widens scope.
- **`-i`:** opens the desktop GUI on the same target.
- Read-only. Never mutates the repo.

Tagline: *"ls, but for your diffs."*

## 2. Components (projects / packages)

- **`Budiff.Core`** - the diff engine. Pure .NET (BCL only), **no Avalonia, no git**. The diff algorithm + the row/result model + the binary/size policy.
- **`Budiff.Avalonia`** - the side-by-side diff **control** + its view-model. Depends on Avalonia and the `ISyntaxHighlighter` / `IDocumentPreviewer` interfaces, NOT on any implementation of them.
- **`Budiff.Avalonia.TextMate`** - a TextMate-backed `ISyntaxHighlighter` (the optional, heavier highlighting path).
- **`Budiff.Avalonia.Markdown`** - a Markdig-backed `IDocumentPreviewer`: the markdown **Preview** (the document formatted, as a block-level rich diff - changed blocks' old versions red, new versions green, unchanged runs foldable).
- **`Budiff.Cli`** - the terminal tool (`budiff`). Avalonia-free so it stays small and Native-AOT-friendly; references `Budiff.Core` + a small ANSI renderer.
- **`Budiff.App`** - the standalone desktop GUI (the README screenshot, the primary dogfooding target).
- A **sample** app and **test** projects.

## 3. Stack

- .NET 10; `Nullable` enabled.
- Avalonia 12.0.4; Avalonia.AvaloniaEdit 12.0.0 + AvaloniaEdit.TextMate 12.0.0; TextMateSharp.Grammars 2.0.4.
- Markdig 1.4.0 (the markdown preview).
- CommunityToolkit.Mvvm 8.4.2.
- LibGit2Sharp 0.31.0 (the git reader; v1 uses LibGit2Sharp across the board).
- Material.Icons.Avalonia 3.0.2.
- Tests: xunit v3 (`xunit.v3`), with Avalonia.Headless.XUnit for UI snapshot tests.
- License: MIT.

## 4. What v1 includes

- The **Code Analysis bird's-eye** (the `ls` surface): folder/project tree, period selector, sortable columns for Lines / Code / Blank / Comment / Files / Added / Removed / Commits / Authors, file search, extension filters, and author filters. Folder-scoped (CWD subtree) by default; `--gitroot` for the whole repo.
- **Drill-in side-by-side diff**: syntax-highlighted, intra-line highlighted, with the Rider-style centre "funnel" connectors (draggable to resize the split), collapse-unchanged, gap expansion, and line wrap (on by default). Markdown files can switch to a formatted **Preview** of the change.
- A **commit / branch picker** so you can diff points other than working-tree-vs-HEAD.
- Both surfaces in **terminal** (CLI) and **desktop GUI** (`-i` / `Budiff.App`).
- **MCP server** (`budiff mcp`) over stdio for AI agents, exposing `list_changes`, `get_file_diff`, and `repo_overview` from the same structured git + Core engine.
- Binary / very-large files: a short placeholder instead of a garbage text diff.

Out of v1 (later, see section 9): deeper agent-specific repo summaries beyond the MCP tools above.

## 5. Porting source from coderide (Burin)

Reference checkout: `../coderide` (see section 0). Port these files into the budiff projects below; drop the Burin-specific glue as noted.

### -> `Budiff.Core` (pure)
- `src/Burin.Vcs/LineDiff.cs` - the diff algorithm (LCS for small inputs, Myers O(ND) for large). Namespace is `Burin.App.Vcs`; rename to `Budiff`.
- the model records (currently under Burin.Vcs):
  - `DiffRow (int? OldNo, string? OldText, int? NewNo, string? NewText, DiffKind Kind)`
  - `DiffKind { Unchanged, Added, Removed, Modified }`
  - `DiffResult (string? Placeholder, IReadOnlyList<DiffRow>? Rows)`
  - `BlobInfo (string Text, long Bytes, bool IsBinary)`
  - `GitFileState` enum
  - NOTE: `DiffRun (string Text, IBrush Brush)` carries an Avalonia brush, so it belongs in `Budiff.Avalonia`, not Core. Core's syntax model should be brush-free (e.g. a token + a colour key/index); the Avalonia layer maps that to `IBrush`.
- `src/Burin.Vcs/DiffLoader.cs` - port only the **pure** policy: `Build`, `LooksBinary`, `BuildAgainstWorking`, and the size/binary thresholds. Leave the git-specific `LoadCommitDiff` / `LoadWorkingDiff` behind (those belong with the git reader, 5.5).

### -> `Budiff.Avalonia` (the control)
- `src/Burin.App/Views/DiffView.axaml` (+ `.axaml.cs`), `DiffFunnelGutter.cs`, `DiffInlines.cs`, and the `HScrollOffsetConverter` from `Views/Converters.cs`.
- **Extract a `DiffViewModel`** from `src/Burin.App/Docking/DiffDocument.cs` (~494 lines). The reusable core to lift:
  - off-thread `BeginLoad(Func<DiffResult>)` with a **generation counter** (a diff re-pointed mid-load drops the stale result),
  - `SetDiff(rows)`,
  - **viewport-first incremental highlighting** (publish ~64 rows at a time so the visible top colours in a few ms; a full re-pass when collapsed gaps meant the first pass had to guess grammar state across them),
  - **collapse-unchanged** with +/-3 lines of context, and **gap expansion**,
  - the bound state: `Rows`, `IsHighlighting`, `IsPlaceholder`, `Placeholder`, `HorizontalScroll`, `MaxScroll`.
  - Leave the `EditorDocument` base class behind - that is Burin docking glue.
- **Parameterize the colours** (today hard-coded) into Avalonia `StyledProperty`s + a default `ResourceDictionary`, so a consumer can theme it and it looks good out of the box:
  - added-line background `#3331C36B`, removed-line background `#33E05252`,
  - funnel added `#5531C36B`, removed `#55E05252`, modified `#55E2C08D`,
  - the five theme brushes the view reads today become budiff defaults: editor background `#1E1F22`, toolbar `#3C3F41`, border `#1E1F22`, text `#DFE1E5`, muted text `#9DA0A8`,
  - default syntax token colour `#D4D4D4` (DarkPlus).
- Define **`ISyntaxHighlighter`**: given a line's text (and a per-line grammar-state handle), return coloured runs. The control depends only on this interface; with no highlighter it renders plain text.

### -> `Budiff.Avalonia.TextMate`
- `src/Burin.App/Docking/DiffSyntax.cs` + `src/Burin.App/TextMatePalette.cs` -> an `ISyntaxHighlighter` implementation. Keep the DarkPlus theme, the grammar + brush caches, the off-thread tokenization serialized on a single lock ("gate"), and the ~6000-line highlight cap.

### -> the Code Analysis bird's-eye
- `src/Burin.Core/CodeAnalysis/AnalysisTreeBuilder.cs`, `AnalysisNode.cs`, `CodeScan.cs`, `LineCounter.cs`, and `CommentSyntax.cs` - the folder/project tree, LOC scanner, extension stats, filters, and per-node aggregates.
- `src/Burin.Core/CodeAnalysis/GitHistory.cs` - commits and authors per path plus the windowed net-diff metric. The model is pure; the git loader lives with the CLI/App git reader per section 5.5.

### 5.5 -> the git reader (lives in the CLI / App, never in Core/Avalonia)
- `src/Burin.Vcs/GitService.cs` (LibGit2Sharp): `GetStatus`, `GetHeadFileBlob`, `GetCommitFileBlobs`, history, branches. It reads the repo and feeds a `DiffResult` into `DiffViewModel.BeginLoad`. **Core and Avalonia never reference git** - the CLI and the App own the git reader and inject text / results into the engine.

### Do NOT port
`src/Burin.App/ViewModels/DiffPresenter.cs`, `DiffDocument`'s `EditorDocument` wrapper, and anything `Dock.Avalonia` - the `Budiff.App` shell (section 7) replaces that glue.

## 6. `Budiff.Cli`

- `budiff` (no args) -> terminal mode, **current folder**: print the Code Analysis bird's-eye; drilling into a file prints the side-by-side ANSI diff. Folder-scoped to the CWD subtree.
- `budiff --gitroot` -> widen analysis scope to the whole repository (flag name TBD).
- `budiff -i` -> launch `Budiff.App` on the target.
- Keep **Avalonia out** of the CLI (reference `Budiff.Core` + a small ANSI renderer) so it stays tiny and Native-AOT-friendly.
- **Architectural rule:** render the terminal output from a **structured change-model**, never by concatenating ANSI strings ad hoc. Keeping the data model separate from the renderer is what made `--json` and the MCP server (section 9) cheap to add.

## 7. `Budiff.App` (standalone GUI)

A thin desktop window around the diff control - **no Dock dependency**:
- open a repo (or the CWD); **repo-root detection**: when launched from a subfolder, *offer* to widen to the repo root (this suggestion is GUI-only; the CLI does not do it),
- a **Code Analysis bird's-eye** panel with grouping, period, sort, search, extension filters, and author filters,
- selection -> the `DiffView` side-by-side pane,
- a commit / branch picker to diff other points,
- a **pull request period**: the period list's "Pull request..." entry opens a picker (the user's PRs by default, 10 at a time with more on scroll; filter by author - anyone in the repo -, state, and free text / `#number` / GitHub qualifiers). The bird's-eye then shows only the files the PR changes (counted at its head, churn = the merge-base -> head net diff, commits/authors from the PR's commits) and the drill-in diffs the PR's merge-base against its head. PR data comes from the GitHub CLI (`gh`); a PR whose commits are not local is fetched from `refs/pull/N/head` without writing any ref.

Reuse Burin's diff-window look and feel (see `DiffWindow` / `DiffView` under `../coderide`). This is the surface the README screenshot shows.

## 8. CI / repo hygiene

- GitHub Actions: build + `dotnet test` (exclude any `Integration` category), pack on a version tag, publish packages to NuGet and attach CLI / GUI build artifacts to the release.
- `MIT` LICENSE; this `SPEC.md`; a README with a GUI screenshot once available; `.editorconfig`.
- **ASCII-only code comments** (no em-dashes, curly quotes, ellipses, or arrow glyphs; write `-`, `...`, `->`). Fancy Unicode is fine in on-screen UI strings.

## 9. AI agents as first-class users

budiff is consumable by AI coding agents, not only humans (agents constantly need "what changed here?" and today parse verbose raw `git diff`).
- **`--json` output** - the listing + diffs as structured JSON (per-file status, `+/-` churn, authors, hunks with intra-line kinds). The single most useful affordance; enabled by the section-6 structured-change-model rule.
- **Two-tier = token-efficient by design** - the ls-then-drill model is how a context-limited agent should consume changes: cheap overview first, then only the file diffs it needs.
- **MCP server** (`budiff mcp`) exposes `list_changes(scope)` / `get_file_diff(path, base, head)` / `repo_overview(period)` over stdio. It reuses the same Core + git engine; no CLI-text parsing.
- **Future verbosity knobs** (collapse-unchanged, large-file placeholders, `--summary`) can further cap output to an agent's context budget.

## 10. Build order

1. `Budiff.Core` - port LineDiff + the pure models + the binary/size policy; unit tests.
2. `Budiff.Avalonia` - DiffView + DiffViewModel + funnel + `ISyntaxHighlighter` + the default theme; a headless UI-snapshot test.
3. `Budiff.Avalonia.TextMate`.
4. `Budiff.App` - the GUI shell + the bird's-eye listing (GitHistory / AnalysisTreeBuilder + GitService).
5. `Budiff.Cli`.
6. CI + README + screenshot.

Later (after budiff is public): swap Burin's in-tree diff for the budiff NuGet packages.
