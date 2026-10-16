# Releasing budiff

A release publishes one NuGet package, the `budiff` tool (the CLI, with the desktop GUI for Windows inside), and a GitHub release with self-contained CLI archives for Windows (with the GUI), Linux, and macOS.

The libraries (`Budiff.Core`, `Budiff.Avalonia`, `Budiff.Avalonia.TextMate`, `Budiff.Avalonia.Markdown`) are not published: their projects are not packable, and the release stops if the pack makes any package besides `budiff`. (0.1.0 also published them and one tool package per platform; those are unlisted.)

Everything runs in [`.github/workflows/release.yml`](../.github/workflows/release.yml). The package is published under the **WellScoped** NuGet.org organization through [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): GitHub exchanges a short-lived token for the push, so no NuGet API key is stored anywhere.

## One-time setup

1. **GitHub environment:** Settings -> Environments -> `nuget.org`:
   - deployment branches and tags: tags matching `v*` only;
   - required reviewer: the lead maintainer (self-review allowed);
   - variable `NUGET_USER` = the NuGet.org user name that owns the trusted-publishing policy below (`VictorZakharov`) - not an email address and not the organization name.
2. **NuGet.org:** signed in as that user, **Trusted Publishing** -> add a GitHub Actions policy:
   - policy owner: `WellScoped` (so new package IDs are created under the organization);
   - repository owner `VictorZakharov`, repository `budiff`;
   - workflow file `release.yml`;
   - environment `nuget.org`.

   While the repository is private, a policy stays active for 7 days only until its first successful use - create it close to the first release, or after the repository is public.

## Each release

1. Make sure `main` is green and the README and screenshots describe what ships.
2. Tag the release on `main` and push the tag:

   ```sh
   git tag -a v0.1.0 -m "budiff 0.1.0"
   git push origin v0.1.0
   ```

   A version with a suffix (`v0.2.0-preview.1`) is published as a prerelease.
3. The **Build release packages** job packs the tool, checks its contents (the GUI included), installs it and runs `budiff --version`, and builds the CLI archives. Pull requests that touch packaging run this job too.
4. The **Publish to NuGet.org and GitHub** job waits for approval in the `nuget.org` environment. Approve it from the workflow run; it pushes the package (with symbols) and creates the GitHub release with generated notes and the archives.
5. Once NuGet.org has indexed the package (a few minutes), check from a clean location:

   ```sh
   dotnet tool install -g budiff --version 0.1.0
   budiff --version
   ```

   and that the NuGet.org pages show the README, the MIT license, and the repository link.

NuGet versions are immutable. If a release is wrong, unlist it on NuGet.org, fix the problem, and release the next version. Do not move or delete a pushed tag.
