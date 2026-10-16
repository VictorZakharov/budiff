# CLAUDE.md

The agent operating guide for this repo is [`AGENTS.md`](AGENTS.md), and the full v1 scope is [`SPEC.md`](SPEC.md). **Read both before making changes.**

Quick reminders (full detail in those files):

- budiff is "`ls` for diffs" - a folder-scoped, read-only side-by-side diff viewer (CLI + Avalonia GUI), extracted from the **Burin** IDE.
- **Local reference:** on the original development machine the Burin source (private at the time of writing) is checked out as a sibling folder at **`../coderide`**. If present, read the real code there for implementation details (`SPEC.md` section 5 maps the files); if not, the budiff source itself is the reference.
- **Strict layering:** `Budiff.Core` pure (no Avalonia, no git); `Budiff.Avalonia` = Avalonia + the `ISyntaxHighlighter` / `IDocumentPreviewer` interfaces only; git lives in the CLI / App.
- Build: `dotnet build`; Test: `dotnet test`. .NET 10. Match the pinned package versions in `SPEC.md`.
- **ASCII-only code comments** (`-`, `...`, `->`); fancy Unicode only in UI strings.
- Follow the build order in `SPEC.md` section 10; small commits; tests green after each.
