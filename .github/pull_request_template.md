## What and why

Describe the change and the problem it solves. Link the issue, if any.

## Checklist

- [ ] Tests cover the change (GUI behavior on the real views, headless).
- [ ] Layering holds: `Budiff.Core` has no Avalonia or git; `Budiff.Avalonia` has no git.
- [ ] Code comments are ASCII-only.
- [ ] New dependencies or shipped assets have MIT-compatible licenses, recorded in `THIRD-PARTY-NOTICES.md` when they ship in the tool package.
- [ ] No credentials, private source, or machine-specific personal data (local paths, user names).

## Verification

What you ran (`dotnet test`, manual checks) and, for GUI changes, a screenshot.
