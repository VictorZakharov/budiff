using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Budiff.App;

// The bird's-eye icon for a file: a Material Design icon for its type, tinted
// in the language's usual colour so a folder of mixed files reads at a glance.
// Unknown types keep the plain file outline in the muted text colour (no tint).
internal static class FileIcons
{
    internal readonly record struct FileIcon(AppIconKind Kind, IBrush? Tint);

    private static readonly FileIcon Plain = new(AppIconKind.FileOutline, null);

    // Whole names first: these files have no telling extension.
    private static readonly Dictionary<string, FileIcon> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = Icon(AppIconKind.Docker, "#2496ED"),
        [".dockerignore"] = Icon(AppIconKind.Docker, "#2496ED"),
        [".gitignore"] = Icon(AppIconKind.Git, "#F05033"),
        [".gitattributes"] = Icon(AppIconKind.Git, "#F05033"),
        [".gitmodules"] = Icon(AppIconKind.Git, "#F05033"),
        [".editorconfig"] = Icon(AppIconKind.Cog, "#B0BEC5"),
        ["Makefile"] = Icon(AppIconKind.Console, "#89E051"),
        ["LICENSE"] = Icon(AppIconKind.FileDocumentOutline, "#D4B85A"),
        ["LICENCE"] = Icon(AppIconKind.FileDocumentOutline, "#D4B85A"),
        ["COPYING"] = Icon(AppIconKind.FileDocumentOutline, "#D4B85A"),
        ["NOTICE"] = Icon(AppIconKind.FileDocumentOutline, "#D4B85A"),
        ["package-lock.json"] = Icon(AppIconKind.Lock, "#FFD54F"),
        ["packages.lock.json"] = Icon(AppIconKind.Lock, "#FFD54F"),
        ["yarn.lock"] = Icon(AppIconKind.Lock, "#FFD54F"),
        ["Cargo.lock"] = Icon(AppIconKind.Lock, "#FFD54F"),
    };

    // Extensions without the dot.
    private static readonly Dictionary<string, FileIcon> ByExtension = Extensions(
        (Icon(AppIconKind.LanguageCsharp, "#A179DC"), ["cs", "csx"]),
        (Icon(AppIconKind.LanguageJavascript, "#F1DD3F"), ["js", "mjs", "cjs"]),
        (Icon(AppIconKind.LanguageTypescript, "#4A90D9"), ["ts", "mts", "cts"]),
        (Icon(AppIconKind.React, "#61DAFB"), ["jsx", "tsx"]),
        (Icon(AppIconKind.Vuejs, "#41B883"), ["vue"]),
        (Icon(AppIconKind.LanguagePython, "#5A9FD4"), ["py", "pyw", "pyi"]),
        (Icon(AppIconKind.LanguageRust, "#DEA584"), ["rs"]),
        (Icon(AppIconKind.LanguageGo, "#00ADD8"), ["go"]),
        (Icon(AppIconKind.LanguageJava, "#E76F00"), ["java"]),
        (Icon(AppIconKind.LanguageKotlin, "#A97BFF"), ["kt", "kts"]),
        (Icon(AppIconKind.LanguageSwift, "#F05138"), ["swift"]),
        (Icon(AppIconKind.LanguageRuby, "#E0443A"), ["rb"]),
        (Icon(AppIconKind.LanguagePhp, "#8892BF"), ["php"]),
        (Icon(AppIconKind.LanguageC, "#A8B9CC"), ["c", "h"]),
        (Icon(AppIconKind.LanguageCpp, "#659AD2"), ["cpp", "cc", "cxx", "hpp", "hh", "hxx"]),
        (Icon(AppIconKind.LanguageLua, "#7A86F0"), ["lua"]),
        (Icon(AppIconKind.LanguageHtml5, "#E44D26"), ["html", "htm"]),
        (Icon(AppIconKind.LanguageCss3, "#42A5F5"), ["css"]),
        (Icon(AppIconKind.Sass, "#CD6799"), ["scss", "sass", "less"]),
        (Icon(AppIconKind.LanguageMarkdown, "#519ABA"), ["md", "markdown", "mdx"]),
        (Icon(AppIconKind.CodeJson, "#F5C842"), ["json", "jsonc", "json5"]),
        (Icon(AppIconKind.Xml, "#FF9800"), ["xml", "xaml", "axaml", "csproj", "fsproj", "vbproj", "props", "targets", "resx", "nuspec", "config", "xsd", "plist"]),
        (Icon(AppIconKind.MicrosoftVisualStudio, "#865FC5"), ["sln", "slnx"]),
        (Icon(AppIconKind.Cog, "#B0BEC5"), ["yml", "yaml", "toml", "ini", "cfg", "conf", "env"]),
        (Icon(AppIconKind.FileImage, "#26A69A"), ["png", "jpg", "jpeg", "gif", "bmp", "ico", "svg", "webp", "tif", "tiff"]),
        (Icon(AppIconKind.FilePdfBox, "#EF5350"), ["pdf"]),
        (Icon(AppIconKind.ZipBox, "#AFB42B"), ["zip", "gz", "tgz", "tar", "7z", "rar", "bz2", "xz", "nupkg"]),
        (Icon(AppIconKind.Console, "#89E051"), ["sh", "bash", "zsh", "fish", "bat", "cmd"]),
        (Icon(AppIconKind.Powershell, "#5391FE"), ["ps1", "psm1", "psd1"]),
        (Icon(AppIconKind.Database, "#FFCA28"), ["sql"]),
        (Icon(AppIconKind.Lock, "#FFD54F"), ["lock"]),
        (Icon(AppIconKind.FileTable, "#4CAF50"), ["csv", "tsv", "xls", "xlsx"]),
        (Icon(AppIconKind.FileMusic, "#E57373"), ["mp3", "wav", "ogg", "flac", "m4a"]),
        (Icon(AppIconKind.FileVideo, "#FFA726"), ["mp4", "mov", "webm", "avi", "mkv"]),
        (Icon(AppIconKind.FormatFont, "#EC407A"), ["ttf", "otf", "woff", "woff2"]),
        (new FileIcon(AppIconKind.FileDocumentOutline, null), ["txt", "log"]));

    public static FileIcon For(string fileName)
    {
        if (ByName.TryGetValue(fileName, out var named))
        {
            return named;
        }

        // Dockerfile.dev, Dockerfile.test ...
        if (fileName.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase))
        {
            return ByName["Dockerfile"];
        }

        var extension = Path.GetExtension(fileName);
        return extension.Length > 1 && ByExtension.TryGetValue(extension[1..], out var typed) ? typed : Plain;
    }

    private static FileIcon Icon(AppIconKind kind, string color) => new(kind, new ImmutableSolidColorBrush(Color.Parse(color)));

    private static Dictionary<string, FileIcon> Extensions(params (FileIcon Icon, string[] Extensions)[] groups)
    {
        var map = new Dictionary<string, FileIcon>(StringComparer.OrdinalIgnoreCase);
        foreach (var (icon, extensions) in groups)
        {
            foreach (var extension in extensions)
            {
                map.Add(extension, icon);
            }
        }

        return map;
    }
}
