# Working in this repo (for AI coding agents)

This file is the operating guide for any AI agent (Codex, Claude Code, etc.) implementing budiff. Read [`SPEC.md`](SPEC.md) first - it is the complete, self-contained v1 scope. This file is *how* to work; `SPEC.md` is *what* to build.

## What budiff is (one paragraph)

budiff is "`ls` for diffs": a folder-scoped, read-only, syntax-highlighted side-by-side diff viewer, as a terminal tool (`Budiff.Cli`) and an Avalonia desktop GUI (`Budiff.App`). See `SPEC.md` sections 1-2.

## Local reference: read the real source

budiff is **extracted from the Burin IDE** (a private repository at the time of writing). On the original development machine it is checked out as a **sibling folder**:

- **`../coderide`** (optional - only present on that machine)

When that checkout exists, it is a valuable resource: `SPEC.md` section 5 lists exactly which files map to which budiff project and what glue to drop, and the real algorithms, threading, the highlighting pipeline, the funnel geometry, and the git calls live in those files. When it does not exist, the budiff source itself is the reference - v1 has already ported everything the spec maps. Either way, do **not** add a dependency on Burin/coderide.

## Build & test

- Build: `dotnet build`
- Test: `dotnet test` (once integration-tagged tests exist, the fast run is `dotnet test --filter "Category!=Integration"`)
- Toolchain: the .NET 10 SDK. Package versions are pinned in `SPEC.md` section 3 - match them.

## Conventions

- **ASCII-only code comments** - no em-dashes, curly quotes, ellipses, or arrow glyphs; write `-`, `...`, `->`. Fancy Unicode is fine in on-screen UI strings.
- **Strict layering** (`SPEC.md` section 2): `Budiff.Core` is pure (no Avalonia, no git); `Budiff.Avalonia` depends on Avalonia + the `ISyntaxHighlighter` interface only; git lives in the CLI / App, never in Core or Avalonia.
- Keep the CLI's **data model separate from its ANSI rendering** (`SPEC.md` section 6) so `--json` / MCP are cheap to add later.
- Work in the **build order** of `SPEC.md` section 10. Prefer small, reviewable commits; run the tests after each.
- Nullable reference types on; treat warnings seriously.

## Definition of done for v1

The full stack builds and tests green: `Budiff.Core` + `Budiff.Avalonia` (+ `.TextMate`) + `Budiff.Cli` + `Budiff.App`, with a sample app, a headless UI-snapshot test of the diff control, and CI (`SPEC.md` section 8). The GUI renders a themed, syntax-highlighted side-by-side diff out of the box.

## Shipped since v1

The Code-Analysis power view (the bird's-eye), the git-pager (stdin) mode, `--json`, and the MCP server have all landed. `SPEC.md` reflects the current scope; treat the issue tracker as the source of truth for remaining work.
