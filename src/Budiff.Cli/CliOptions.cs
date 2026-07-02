internal sealed record CliOptions(
    bool GitRoot,
    bool OpenGui,
    bool Mcp,
    bool Stdin,
    bool ShowHelp,
    string BaseRef,
    string? HeadRef,
    string? FilePath,
    bool Json,
    int? Days,
    string? Error)
{
    public static CliOptions Parse(string[] args)
    {
        var gitRoot = false;
        var openGui = false;
        var mcp = false;
        var stdin = false;
        var showHelp = false;
        var baseRef = "HEAD";
        string? headRef = null;
        string? filePath = null;
        string? error = null;
        var json = false;
        int? daysOption = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                case "-r":
                case "--repo":
                case "--gitroot":
                    gitRoot = true;
                    break;
                case "-i":
                    openGui = true;
                    break;
                case "mcp":
                    if (filePath is null)
                    {
                        mcp = true;
                    }
                    else
                    {
                        error = "`mcp` must be the first positional argument.";
                    }

                    break;
                case "--stdin":
                    stdin = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--base":
                    if (++i >= args.Length)
                    {
                        error = "--base requires a ref.";
                    }
                    else
                    {
                        baseRef = args[i];
                    }

                    break;
                case "--head":
                    if (++i >= args.Length)
                    {
                        error = "--head requires a ref.";
                    }
                    else
                    {
                        headRef = args[i];
                    }

                    break;
                case "--days":
                    if (++i >= args.Length || !int.TryParse(args[i], out var days) || days < 0)
                    {
                        error = "--days requires a non-negative number (0 = all time).";
                    }
                    else
                    {
                        daysOption = days;
                    }

                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        error = $"Unknown option '{arg}'.";
                    }
                    else if (filePath is null)
                    {
                        filePath = arg;
                    }
                    else
                    {
                        error = "Only one file path can be provided.";
                    }

                    break;
            }
        }

        if (openGui && json)
        {
            error = "--json cannot be combined with -i.";
        }

        if (openGui && stdin)
        {
            error = "--stdin cannot be combined with -i.";
        }

        if (mcp && openGui)
        {
            error = "mcp cannot be combined with -i.";
        }

        if (mcp && json)
        {
            error = "mcp cannot be combined with --json.";
        }

        if (mcp && stdin)
        {
            error = "mcp cannot be combined with --stdin.";
        }

        if (mcp && filePath is not null)
        {
            error = "mcp cannot be combined with a file path.";
        }

        if (stdin && filePath is not null)
        {
            error = "--stdin cannot be combined with a file path.";
        }

        return new CliOptions(gitRoot, openGui, mcp, stdin, showHelp, baseRef, headRef, filePath, json, daysOption, error);
    }
}
