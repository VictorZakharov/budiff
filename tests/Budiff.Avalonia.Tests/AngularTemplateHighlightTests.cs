using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Budiff.App;
using Budiff.Avalonia.TextMate;
using Budiff.Git;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// An Angular component's inline template: is HTML and its styles: CSS, not
// one long string: tags, [prop] / (event) bindings, {{ interpolation }} and
// the @if / @for control flow get their own colours. Only a template: or
// styles: inside a decorator gets this - any other backtick string (one
// that merely holds markup, or a template: key in a plain object) stays a
// string.
public sealed class AngularTemplateHighlightTests
{
    // Dark+ colours.
    private static readonly Color StringColour = Color.Parse("#CE9178");
    private static readonly Color KeywordColour = Color.Parse("#569CD6");
    private static readonly Color AttributeColour = Color.Parse("#9CDCFE");
    private static readonly Color ControlColour = Color.Parse("#C586C0");
    private static readonly Color FunctionColour = Color.Parse("#DCDCAA");
    private static readonly Color CssPropertyColour = Color.Parse("#4EC9B0");

    // Tags and entities share Dark+'s blue with TypeScript keywords.
    private static readonly Color TagColour = KeywordColour;

    private const string Source = """
        import { Component, input } from '@angular/core';
        import { Avatar } from './avatar';

        const greeting = `<b>Hello</b> ${name}`;

        @Component({
          selector: 'app-room',
          imports: [Avatar],
          host: { '[class.busy]': 'busy()' },
          template: `
            @if (room(); as r) {
              <section class="pane" #box="ref" (drop)="upload($event)" [attr.aria-label]="r.title">
                <app-avatar [src]="r.icon" [size]="22" [(open)]="isOpen" *ngIf="r.owner" />
                <span class="muted">{{ r.owner | titlecase }} &#64; {{ r.count }}</span>
              </section>
            } @else {
              <h1># {{ fallback }}</h1>
            }
            @for (m of members().shown; track m.id) {
              <app-avatar [src]="m.icon" />
            } @empty {
              <p>nobody</p>
            }
            @let total = members().length;
          `,
          styles: `
            :host { display: block; }
            .muted { color: #888; }
          `,
        })
        export class Room {
          readonly room = input<string>();
          label = `<i>room</i> ${this.room()}`;
          config = { template: `<b>plain</b>` };
        }
        """;

    [Fact]
    public void InlineTemplateAndStyles_ReadAsHtmlAndCss()
    {
        var lines = Highlight(Source);
        var text = Source.Split('\n');
        IReadOnlyList<DiffRun> Line(string marker) => lines[Array.FindIndex(text, l => l.Contains(marker, StringComparison.Ordinal))];

        // Plain backtick strings stay strings, markup or not.
        Assert.Equal(StringColour, Colour(Line("const greeting"), "<b>Hello</b>"));
        Assert.Equal(StringColour, Colour(Line("label ="), "<i>room</i>"));
        Assert.Equal(StringColour, Colour(Line("config ="), "<b>plain</b>"));

        var section = Line("<section");
        Assert.Equal(TagColour, Colour(section, "section"));
        Assert.Equal(AttributeColour, Colour(section, "class"));
        Assert.Equal(StringColour, Colour(section, "pane"));
        Assert.Equal(AttributeColour, Colour(section, "box"));
        Assert.Equal(AttributeColour, Colour(section, "drop"));
        Assert.Equal(FunctionColour, Colour(section, "upload"));
        Assert.Equal(AttributeColour, Colour(section, "attr.aria-label"));
        Assert.Equal(AttributeColour, Colour(section, "title"));

        var avatar = Line("[size]=\"22\"");
        Assert.Equal(TagColour, Colour(avatar, "app-avatar"));
        Assert.Equal(AttributeColour, Colour(avatar, "open"));
        Assert.Equal(AttributeColour, Colour(avatar, "ngIf"));

        var span = Line("<span");
        Assert.NotEqual(StringColour, Colour(span, "{{"));
        Assert.Equal(AttributeColour, Colour(span, "owner"));
        Assert.Equal(FunctionColour, Colour(span, "titlecase"));
        Assert.Equal(TagColour, Colour(span, "#64;"));

        Assert.Equal(ControlColour, Colour(Line("@if"), "if"));
        Assert.Equal(ControlColour, Colour(Line("@if"), "as"));
        Assert.Equal(ControlColour, Colour(Line("@else"), "else"));
        Assert.Equal(ControlColour, Colour(Line("@for"), "for"));
        Assert.Equal(ControlColour, Colour(Line("@for"), "track"));
        Assert.Equal(ControlColour, Colour(Line("@empty"), "empty"));
        Assert.Equal(ControlColour, Colour(Line("@let"), "let"));
        Assert.NotEqual(StringColour, Colour(Line("nobody"), "nobody"));

        Assert.Equal(CssPropertyColour, Colour(Line(":host"), "display"));
        Assert.Equal(CssPropertyColour, Colour(Line(".muted {"), "color"));

        // After the decorator the class body is TypeScript again.
        Assert.Equal(KeywordColour, Colour(Line("readonly room"), "readonly"));
    }

    [AvaloniaFact]
    public void AngularComponent_InTheMainWindow_ColoursTheInlineTemplate()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-angular-template");
        var changed = Source
            .Replace("[size]=\"22\"", "[size]=\"24\"", StringComparison.Ordinal)
            .Replace("<p>nobody</p>", "<p>no one yet</p>", StringComparison.Ordinal)
            .Replace("<i>room</i> ", "<i>room:</i> ", StringComparison.Ordinal);
        using (var repo = new Repository(root))
        {
            ReadmeFixture.Commit(repo, ReadmeFixture.Sig("Ada Lovelace", "ada@example.com", daysAgo: 2), "Add the room", new() { ["web/room.ts"] = Source + "\n" });
            ReadmeFixture.Commit(repo, ReadmeFixture.Sig("Ada Lovelace", "ada@example.com", daysAgo: 1), "Tweak the room", new() { ["web/room.ts"] = changed + "\n" });
        }

        var commit = GitRepositoryReader.ReadCommit(root, "HEAD")!;
        var window = new MainWindow(root, gitRoot: true, settings: null, commit, "web/room.ts") { Width = 1400, Height = 900 };
        window.Show();
        var diff = window.ViewModel.Diff;
        WaitUntil(() => window.ViewModel.IsDiffOpen && !diff.IsLoading && !diff.IsPlaceholder && diff.FileName == "room.ts", "room.ts");

        // Highlighting runs off the UI thread, and the template's rows sit
        // between collapsed gaps: wait for the colours to settle.
        IReadOnlyList<DiffRun> Row(string marker)
            => diff.Rows.First(r => !r.IsGap && (r.Source?.NewText ?? string.Empty).Contains(marker, StringComparison.Ordinal)).NewRuns ?? [];
        WaitUntil(() => Row("@for").Any(r => r.Text == "for" && Hue(r) == ControlColour) && Row("label =").Count > 1, "highlighting");

        var section = Row("<section");
        var tag = Colour(section, "section");
        var binding = Colour(section, "attr.aria-label");
        var @event = Colour(section, "drop");
        var interpolation = Colour(Row("<span"), "{{");
        var @if = Colour(Row("@if"), "if");
        var @for = Colour(Row("@for"), "for");

        Assert.All(new[] { tag, binding, @event, interpolation, @if, @for }, c => Assert.NotEqual(StringColour, c));
        Assert.Equal(4, new[] { tag, binding, interpolation, @if }.Distinct().Count());
        Assert.Equal(@if, @for);
        Assert.Equal(binding, @event);

        // The old side of a changed template line is a template too.
        var oldAvatar = diff.Rows.First(r => (r.Source?.OldText ?? string.Empty).Contains("[size]=\"22\"", StringComparison.Ordinal)).OldRuns ?? [];
        Assert.Equal(TagColour, Colour(oldAvatar, "app-avatar"));

        // A backtick string outside the decorator stays a string.
        Assert.Equal(StringColour, Colour(Row("label ="), "<i>room:</i>"));

        // ...and that is what the rendered rows show.
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        var rendered = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && t.Inlines is { Count: > 0 })
            .SelectMany(t => t.Inlines!.OfType<Run>())
            .ToList();
        Assert.Contains(rendered, r => r.Text == "for" && Hue(r.Foreground) == ControlColour);
        Assert.Contains(rendered, r => r.Text == "section" && Hue(r.Foreground) == TagColour);

        UiSnapshot.Capture(window, "angular-inline-template");
        window.Close();
    }

    private static List<IReadOnlyList<DiffRun>> Highlight(string text)
    {
        var lines = text.Split('\n');
        var session = new TextMateSyntaxHighlighter().CreateSession(".ts", lines.Length)!;
        var result = new List<IReadOnlyList<DiffRun>>();
        object? state = null;
        foreach (var line in lines)
        {
            var highlighted = session.HighlightLine(line, state);
            state = highlighted.State;
            result.Add(highlighted.Runs);
        }

        return result;
    }

    private static Color Colour(IReadOnlyList<DiffRun> line, string text)
    {
        var run = line.FirstOrDefault(r => r.Text.Trim() == text)
            ?? throw new Xunit.Sdk.XunitException($"No run '{text}' in: {string.Join(" | ", line.Select(r => r.Text))}");
        return Hue(run);
    }

    private static Color Hue(DiffRun run) => Hue(run.Brush);

    private static Color Hue(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color : Colors.Transparent;

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            Thread.Sleep(25);
            UiSnapshot.Pump();
        }

        UiSnapshot.Pump();
    }
}
