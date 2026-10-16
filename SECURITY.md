# Security policy

budiff reads repositories, diffs, and documents that may come from anyone, so it treats them as untrusted input. It is read-only by design: it never writes to the repositories it shows (fetching a pull request's commits stores git objects only, never a ref).

Especially important:

- anything that makes budiff modify a repository or files on disk;
- running code or commands from a repository's content or configuration;
- markdown previews reaching the network or the file system beyond what the README documents (pictures load from the project; web pictures are not fetched);
- the MCP server (`budiff mcp`) exposing files outside the requested repository or scope;
- crashes or hangs triggered by crafted repositories, diffs, or documents.

## Reporting a vulnerability

Do not open a public issue. Report it privately through GitHub's **Report a vulnerability** button on the repository's **Security** tab.

Include the budiff version (`budiff --version`), your OS, reproduction steps, and the impact. Use a small synthetic repository - never real private source or credentials.

## Supported versions

Fixes go into the latest release only.
