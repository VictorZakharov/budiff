using System;
using System.Collections.Generic;
using System.IO;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Budiff.Avalonia.TextMate;

// The bundled grammars plus two of budiff's own, injected into TypeScript:
// an Angular component's inline template: string reads as HTML (with the
// control-flow blocks, {{ interpolation }} and [prop] / (event) bindings),
// its styles: as CSS. Only a template: / styles: inside a decorator gets
// this; every other string stays a string. The grammars are embedded JSON
// read by TextMateSharp's own reader (no reflection), so NativeAOT is fine.
internal sealed class AngularRegistryOptions(IRegistryOptions inner) : IRegistryOptions
{
    internal const string InlineScope = "inline.ng.budiff";
    internal const string BindingsScope = "bindings.ng.budiff";

    private static readonly string[] TypeScriptInjections = [InlineScope, BindingsScope];

    public IRawTheme GetTheme(string scopeName) => inner.GetTheme(scopeName);

    public IRawTheme GetDefaultTheme() => inner.GetDefaultTheme();

    public IRawGrammar GetGrammar(string scopeName) => scopeName switch
    {
        InlineScope => Read("angular-inline"),
        BindingsScope => Read("angular-bindings"),
        _ => inner.GetGrammar(scopeName),
    };

    public ICollection<string> GetInjections(string scopeName)
        => scopeName == "source.ts" ? TypeScriptInjections : inner.GetInjections(scopeName);

    private static IRawGrammar Read(string name)
    {
        using var stream = typeof(AngularRegistryOptions).Assembly.GetManifestResourceStream($"Budiff.Grammars.{name}.tmLanguage.json")
            ?? throw new InvalidOperationException($"Missing grammar resource {name}.");
        using var reader = new StreamReader(stream);
        return GrammarReader.ReadGrammarSync(reader);
    }
}
