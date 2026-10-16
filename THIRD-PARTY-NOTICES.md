# Third-party notices

budiff is licensed under the MIT License (see `LICENSE`). It is built on, and its
published binaries include, the third-party components below; each remains under
its own license. This file and the `licenses/` folder ship inside the `budiff`
tool package and the release archives.

- **The `budiff` tool package** carries the CLI (framework-dependent .NET) and,
  under `gui/win-x64/`, the desktop GUI as a Windows NativeAOT build - which
  compiles the .NET runtime and the GUI's dependencies into one executable next
  to their native libraries.
- **The release archives** (`budiff-<platform>`) are self-contained, single-file
  CLI builds: the CLI's dependencies plus the .NET runtime.
- **The library packages** (`Budiff.Core`, `Budiff.Avalonia`,
  `Budiff.Avalonia.TextMate`, `Budiff.Avalonia.Markdown`) contain only budiff's
  own code; their dependencies are ordinary NuGet references.

Versions below are the ones pinned at the time of writing.

## In the CLI and the GUI

| Component | Version | License | Notice |
| --- | --- | --- | --- |
| [.NET runtime](https://github.com/dotnet/runtime) | 10.0 | MIT | Copyright (c) .NET Foundation and Contributors. Its own third-party notices: [`licenses/dotnet-runtime.txt`](licenses/dotnet-runtime.txt) |
| [TextMateSharp](https://github.com/danipen/TextMateSharp), TextMateSharp.Grammars | 2.0.4 | MIT | Copyright (c) 2021 Daniel Peñalba. Bundled grammars and themes: see below |
| [Onigwrap](https://github.com/aikawayataro/Onigwrap) | 1.0.11 | MIT | Copyright (c) 2024 Aikawa Yataro |
| [Oniguruma](https://github.com/kkos/oniguruma) (in Onigwrap) | - | BSD-2-Clause | Copyright (c) 2002-2021 K.Kosako. Full text: [`licenses/oniguruma.txt`](licenses/oniguruma.txt) |

## In the GUI only

| Component | Version | License | Notice |
| --- | --- | --- | --- |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) (Avalonia, .Desktop, .Win32, .Skia, .HarfBuzz, .Themes.Fluent, .FreeDesktop, .Remote.Protocol, .Fonts.Inter) | 12.0.4 | MIT | Copyright (c) AvaloniaUI OÜ |
| [Inter](https://github.com/rsms/inter) typeface (in Avalonia.Fonts.Inter) | - | SIL Open Font License 1.1 | Copyright (c) 2016 The Inter Project Authors. Full text: [`licenses/inter-ofl.txt`](licenses/inter-ofl.txt) |
| [ANGLE](https://chromium.googlesource.com/angle/angle) (Avalonia.Angle.Windows.Natives) | 2.1.27548.20260419 | BSD-3-Clause | Copyright 2018 The ANGLE Project Authors. Full text: [`licenses/angle.txt`](licenses/angle.txt) |
| [SkiaSharp](https://github.com/mono/SkiaSharp), HarfBuzzSharp | 3.119.4, 8.3.1.3 | MIT | Copyright (c) 2015-2016 Xamarin, Inc.; Copyright (c) 2017-2018 Microsoft Corporation |
| Skia, HarfBuzz and the libraries they bundle (SkiaSharp / HarfBuzzSharp native assets) | 3.119.4, 8.3.1.3 | BSD-3-Clause, MIT and others | Full notices: [`licenses/skiasharp-harfbuzzsharp-native.txt`](licenses/skiasharp-harfbuzzsharp-native.txt) |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | Copyright (c) .NET Foundation and Contributors |
| [Markdig](https://github.com/xoofx/markdig) | 1.4.0 | BSD-2-Clause | Copyright (c) 2016-2026, Alexandre Mutel. Full text: [`licenses/markdig.txt`](licenses/markdig.txt) |
| [Material.Icons](https://github.com/SKProCH/Material.Icons), [Material.Icons.Avalonia](https://github.com/AvaloniaUtils/Material.Icons.Avalonia) | 3.0.2 | MIT | Copyright (c) 2021 SKProCH |
| [Material Design Icons](https://github.com/Templarian/MaterialDesign) (icon data in Material.Icons) | - | Pictogrammers Free License (icons: Apache-2.0) | Pictogrammers. Full text: [`licenses/material-design-icons.txt`](licenses/material-design-icons.txt), [`licenses/apache-2.0.txt`](licenses/apache-2.0.txt) |
| [MicroCom.Runtime](https://github.com/kekekeks/MicroCom) | 0.11.4 | MIT | Copyright (c) 2021 Nikita Tsukanov |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | 0.92.0 | MIT | Copyright 2006 Alp Toker; Copyright 2010 Other Contributors; Copyright 2016 Tom Deseyn |

## TextMate grammars and themes

TextMateSharp.Grammars embeds TextMate grammars and themes (budiff uses its Dark+
theme). Their upstream projects and licenses, from the package's component
manifests:

| Grammar | License | Source |
| --- | --- | --- |
| AnsonYeung/vscode-pascal-language-basics | MIT | <https://github.com/AnsonYeung/vscode-pascal-language-basics> |
| atom/language-clojure | MIT | <https://github.com/atom/language-clojure> |
| atom/language-coffee-script | MIT | <https://github.com/atom/language-coffee-script> |
| atom/language-css | GitHub License | <https://github.com/atom/language-css> |
| atom/language-java | MIT | <https://github.com/atom/language-java> |
| atom/language-sass | MIT | <https://github.com/atom/language-sass> |
| atom/language-shellscript | MIT | <https://github.com/atom/language-shellscript> |
| atom/language-xml | MIT | <https://github.com/atom/language-xml> |
| better-go-syntax | MIT | <https://github.com/jeff-hykin/better-go-syntax> |
| daaain/Handlebars | MIT | <https://github.com/daaain/Handlebars> |
| dart-lang/dart-syntax-highlight | BSD-3-Clause | <https://github.com/dart-lang/dart-syntax-highlight> |
| davidrios/pug-tmbundle | MIT | <https://github.com/davidrios/pug-tmbundle> |
| demyte/language-cshtml | MIT | <https://github.com/demyte/language-cshtml> |
| dotnet/csharp-tmLanguage | MIT | <https://github.com/dotnet/csharp-tmLanguage> |
| fadeevab/make.tmbundle | TextMate Bundle License | <https://github.com/fadeevab/make.tmbundle> |
| freebroccolo/atom-language-swift | MIT | <https://github.com/freebroccolo/atom-language-swift> |
| Ikuyadeu/vscode-R | MIT | <https://github.com/Ikuyadeu/vscode-R> |
| ionide/ionide-fsgrammar | MIT | <https://github.com/ionide/ionide-fsgrammar> |
| James-Yu/LaTeX-Workshop | MIT | <https://github.com/James-Yu/LaTeX-Workshop> |
| jeff-hykin/better-c-syntax | MIT | <https://github.com/jeff-hykin/better-c-syntax> |
| jeff-hykin/better-cpp-syntax | MIT | <https://github.com/jeff-hykin/better-cpp-syntax> |
| jeff-hykin/cpp-textmate-grammar | MIT | <https://github.com/jeff-hykin/cpp-textmate-grammar> |
| jlelong/vscode-latex-basics | MIT | <https://github.com/jlelong/vscode-latex-basics> |
| JuliaEditorSupport/atom-language-julia | MIT | <https://github.com/JuliaEditorSupport/atom-language-julia> |
| language-docker | Apache-2.0 | <https://github.com/moby/moby> |
| language-less | MIT | <https://github.com/atom/language-less> |
| language-php | MIT | <https://github.com/atom/language-php> |
| MagicStack/MagicPython | MIT | <https://github.com/MagicStack/MagicPython> |
| mattneub/AsciiDoc-TextMate-2.tmbundle | MIT | <https://github.com/mattneub/AsciiDoc-TextMate-2.tmbundle> |
| microsoft/TypeScript-TmLanguage | MIT | <https://github.com/microsoft/TypeScript-TmLanguage> |
| microsoft/vscode-JSON.tmLanguage | MIT | <https://github.com/microsoft/vscode-JSON.tmLanguage> |
| microsoft/vscode-markdown-tm-grammar | MIT | <https://github.com/microsoft/vscode-markdown-tm-grammar> |
| microsoft/vscode-mssql | MIT | <https://github.com/microsoft/vscode-mssql> |
| mmims/language-batchfile | MIT | <https://github.com/mmims/language-batchfile> |
| Myriad-Dreamin/tinymist | Apache-2.0 | <https://github.com/Myriad-Dreamin/tinymist> |
| NVIDIA/cuda-cpp-grammar | MIT | <https://github.com/NVIDIA/cuda-cpp-grammar> |
| PowerShell/EditorSyntax | MIT | <https://github.com/PowerShell/EditorSyntax> |
| rust-syntax | MIT | <https://github.com/dustypomerleau/rust-syntax> |
| shaders-tmLanguage | MIT | <https://github.com/tgjones/shaders-tmLanguage> |
| sumneko/lua.tmbundle | TextMate Bundle License | <https://github.com/sumneko/lua.tmbundle> |
| textmate/asp.vb.net.tmbundle | TextMate Bundle License | <https://github.com/textmate/asp.vb.net.tmbundle> |
| textmate/c.tmbundle | TextMate Bundle License | <https://github.com/textmate/c.tmbundle> |
| textmate/diff.tmbundle | TextMate Bundle License | <https://github.com/textmate/diff.tmbundle> |
| textmate/git.tmbundle | MIT | <https://github.com/textmate/git.tmbundle> |
| textmate/groovy.tmbundle | TextMate Bundle License | <https://github.com/textmate/groovy.tmbundle> |
| textmate/html.tmbundle | TextMate Bundle License | <https://github.com/textmate/html.tmbundle> |
| textmate/ini.tmbundle | TextMate Bundle License | <https://github.com/textmate/ini.tmbundle> |
| textmate/javascript.tmbundle | TextMate Bundle License | <https://github.com/textmate/javascript.tmbundle> |
| textmate/markdown.tmbundle | TextMate Bundle License | <https://github.com/textmate/markdown.tmbundle> |
| textmate/perl.tmbundle | TextMate Bundle License | <https://github.com/textmate/perl.tmbundle> |
| textmate/ruby.tmbundle | TextMate Bundle License | <https://github.com/textmate/ruby.tmbundle> |
| textmate/yaml.tmbundle | TextMate Bundle License | <https://github.com/textmate/yaml.tmbundle> |
| TypeScript-TmLanguage | MIT | <https://github.com/microsoft/TypeScript-TmLanguage> |
| Universal-Assembly-Syntax-Highlighting | MIT | <https://github.com/haxo-games/Universal-Assembly-Syntax-Highlighting> |
| vscode-logfile-highlighter | MIT | <https://github.com/emilast/vscode-logfile-highlighter> |
| vscode-swift | MIT | <https://github.com/owensd/vscode-swift> |

The "TextMate Bundle License" reads:

> Permission to copy, use, modify, sell and distribute this software is granted.
> This software is provided "as is" without express or implied warranty, and
> with no claim as to its suitability for any purpose.

Apache-2.0: [`licenses/apache-2.0.txt`](licenses/apache-2.0.txt).

## MIT License

The components marked MIT above are provided under the MIT License, with the
copyright notices listed for each:

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
