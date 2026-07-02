namespace Budiff;

public static class RepoPaths
{
    // Forward slashes, no leading slash: the repo-relative key shape every
    // subsystem matches paths by. One implementation so the variants that had
    // grown in GitHistory / CodeScanner / GitRepositoryReader cannot drift.
    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
