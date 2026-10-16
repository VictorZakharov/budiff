# CLAUDE.md

The agent operating guide for this repo is [`AGENTS.md`](AGENTS.md), and the full v1 scope is [`SPEC.md`](SPEC.md). **Read both before making changes.**

Quick reminders (full detail in those files):

- budiff is "`ls` for diffs" - a folder-scoped, read-only side-by-side diff viewer (CLI + Avalonia GUI). `SPEC.md` section 5 maps where each responsibility lives.
- **Strict layering:** `Budiff.Core` pure (no Avalonia, no git); `Budiff.Avalonia` = Avalonia + the `ISyntaxHighlighter` / `IDocumentPreviewer` interfaces only; git lives in the CLI / App.
- Build: `dotnet build`; Test: `dotnet test`. .NET 10. Match the pinned package versions in `SPEC.md`.
- **ASCII-only code comments** (`-`, `...`, `->`); fancy Unicode only in UI strings.
- Follow the build order in `SPEC.md` section 10; small commits; tests green after each.
