# Contributing to budiff

budiff is an MIT-licensed, read-only diff viewer: a terminal tool and an Avalonia desktop GUI over a small diff engine. Bug reports, ideas, and pull requests are welcome.

## Before you start

- For anything larger than a small fix, open an issue first so the approach can be agreed before you spend time on it.
- Read [`SPEC.md`](SPEC.md) (what budiff is and where each part lives) and [`AGENTS.md`](AGENTS.md) (how to work in this repo - it applies to people as much as to AI agents).
- Install the .NET 10 SDK.

## Build and test

```sh
dotnet build
dotnet test
```

The GUI tests run headless (Avalonia.Headless) - no window opens, so they are safe to run while you work. CI runs `dotnet test --filter "Category!=Integration"` on Windows and Linux; integration tests need extra local tools and skip themselves when those are missing.

To try a local build of the tool:

```sh
dotnet pack src/Budiff.Cli/Budiff.Cli.csproj -c Release -o artifacts/local -p:Version=0.1.0-local.1
dotnet tool update -g budiff --add-source ./artifacts/local --version 0.1.0-local.1
```

Packing publishes the GUI as a Windows NativeAOT build, which needs the Visual Studio C++ build tools (`link.exe`). If linking fails because `vswhere` is not recognized, add `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` to `PATH`.

## Conventions

- **Layering:** `Budiff.Core` is pure .NET (no Avalonia, no git); `Budiff.Avalonia` depends on Avalonia and the `ISyntaxHighlighter` / `IDocumentPreviewer` interfaces only; git lives in the CLI and the App (`src/Budiff.Shared/Git`).
- **ASCII-only code comments** - write `-`, `...`, `->`; fancy Unicode is fine in on-screen UI strings.
- Nullable reference types are on; keep the build free of new warnings.
- Behavior changes come with tests. GUI behavior is tested on the real views, headless.
- Keep commits small and focused; tests green after each.

## Pull requests

- Keep each PR to one change and explain what it does, why, and how it was verified.
- Include screenshots for visible GUI changes.
- New dependencies, fonts, icons, or other assets need a license compatible with MIT, recorded in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) when they ship in the tool package.
- Do not include credentials, private source, or machine-specific personal data (local paths, user names) in code, tests, fixtures, or screenshots.

By contributing, you agree that your contribution is provided under the repository's [MIT License](LICENSE).
