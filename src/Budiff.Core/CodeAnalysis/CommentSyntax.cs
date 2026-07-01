using System;
using System.Collections.Generic;
using System.IO;

namespace Budiff.CodeAnalysis;

public sealed class CommentSyntax
{
    private static readonly CommentSyntax CStyle = new(new[] { "//" }, "/*", "*/");
    private static readonly CommentSyntax Hash = new(new[] { "#" }, null, null);
    private static readonly CommentSyntax PowerShell = new(new[] { "#" }, "<#", "#>");
    private static readonly CommentSyntax Xml = new(Array.Empty<string>(), "<!--", "-->");
    private static readonly CommentSyntax Sql = new(new[] { "--" }, "/*", "*/");
    private static readonly CommentSyntax Lua = new(new[] { "--" }, "--[[", "]]");
    private static readonly CommentSyntax FSharp = new(new[] { "//" }, "(*", "*)");
    private static readonly CommentSyntax Semicolon = new(new[] { ";" }, null, null);
    private static readonly CommentSyntax Razor = new(Array.Empty<string>(), "@*", "*@");
    private static readonly Dictionary<string, CommentSyntax> ByExtension = Build();
    private static readonly Dictionary<string, CommentSyntax> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dockerfile"] = Hash,
        ["makefile"] = Hash,
        [".gitignore"] = Hash,
        [".gitattributes"] = Hash,
        [".editorconfig"] = Hash,
        [".dockerignore"] = Hash,
    };

    private CommentSyntax(IReadOnlyList<string> lineComments, string? blockStart, string? blockEnd)
    {
        LineComments = lineComments;
        BlockStart = blockStart;
        BlockEnd = blockEnd;
    }

    public IReadOnlyList<string> LineComments { get; }

    public string? BlockStart { get; }

    public string? BlockEnd { get; }

    public bool RecognisesComments => LineComments.Count > 0 || BlockStart is not null;

    public static readonly CommentSyntax None = new(Array.Empty<string>(), null, null);

    public static CommentSyntax For(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length > 0 && ByExtension.TryGetValue(ext.ToLowerInvariant(), out var byExt))
        {
            return byExt;
        }

        var name = Path.GetFileName(path);
        return name.Length > 0 && ByFileName.TryGetValue(name, out var byName) ? byName : None;
    }

    private static Dictionary<string, CommentSyntax> Build()
    {
        var map = new Dictionary<string, CommentSyntax>(StringComparer.Ordinal);

        void Add(CommentSyntax syntax, params string[] exts)
        {
            foreach (var ext in exts)
            {
                map[ext] = syntax;
            }
        }

        Add(CStyle,
            ".cs", ".c", ".h", ".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx", ".ino",
            ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts",
            ".java", ".kt", ".kts", ".go", ".rs", ".swift", ".scala", ".groovy", ".gradle",
            ".css", ".scss", ".sass", ".less", ".styl",
            ".php", ".m", ".mm", ".dart", ".proto", ".json5", ".jsonc",
            ".glsl", ".hlsl", ".shader", ".vert", ".frag", ".cg");
        Add(Hash,
            ".py", ".pyw", ".rb", ".sh", ".bash", ".zsh", ".fish", ".pl", ".pm", ".r",
            ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".env", ".properties",
            ".tf", ".tfvars", ".nim", ".cr", ".ex", ".exs", ".jl", ".coffee");
        Add(PowerShell, ".ps1", ".psm1", ".psd1");
        Add(Xml,
            ".xml", ".html", ".htm", ".xhtml", ".axaml", ".xaml", ".svg", ".vue", ".xsd", ".xslt",
            ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".nuspec", ".config", ".resx",
            ".plist", ".storyboard", ".slnx", ".wxs");
        Add(Sql, ".sql");
        Add(Lua, ".lua");
        Add(FSharp, ".fs", ".fsi", ".fsx", ".ml", ".mli");
        Add(Semicolon, ".clj", ".cljs", ".cljc", ".edn", ".el", ".lisp", ".scm", ".rkt");
        Add(Razor, ".razor", ".cshtml", ".vbhtml");

        return map;
    }
}
