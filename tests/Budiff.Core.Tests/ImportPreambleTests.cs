using Budiff;
using Xunit;

namespace Budiff.Core.Tests;

public sealed class ImportPreambleTests
{
    private static int Start(string text) => ImportPreamble.FirstUsefulLine(text.Split('\n'));

    [Theory]
    [InlineData("app.ts", true)]
    [InlineData("app.component.spec.ts", true)]
    [InlineData("view.TSX", true)]
    [InlineData("lib.mts", true)]
    [InlineData("app.js", false)]
    [InlineData("Program.cs", false)]
    [InlineData(".ts", true)]
    public void AppliesTo_TypeScriptFiles(string name, bool expected)
        => Assert.Equal(expected, ImportPreamble.AppliesTo(name));

    [Fact]
    public void SkipsHeaderCommentsImportsAndBlankLines()
    {
        var text = """
            /**
             * Licensed under MIT.
             */
            // eslint-disable
            'use strict';
            import { Component, OnInit } from '@angular/core';
            import type { User } from './user';
            import * as fs from "fs";
            import './polyfills';

            /** The app. */
            export class App {}
            """;

        Assert.Equal(10, Start(text));
    }

    [Fact]
    public void FollowsImportsThatSpanLines_WithOrWithoutSemicolons()
    {
        var text = """
            import {
              a,
              b,
            } from './ab'
            import c = require('c');
            export * from './all';
            export {
              d,
            } from './d';
            const x = 1;
            """;

        Assert.Equal(9, Start(text));
    }

    [Fact]
    public void StopsAtALocalExportList()
    {
        var text = """
            import { a } from './a';
            export { a };
            """;

        Assert.Equal(1, Start(text));
    }

    [Theory]
    [InlineData("const x = 1;\nimport { a } from './a';\n")]
    [InlineData("// only a comment\nexport function f() {}\n")]
    [InlineData("import { a } from './a';\n\n")]
    [InlineData("")]
    public void StaysAtTheTop_WithoutImportsToSkip(string text)
        => Assert.Equal(0, Start(text));

    [Fact]
    public void AnImportsLookalikeIsNotAnImport()
        => Assert.Equal(0, Start("importantThing();\nimport { a } from './a';\n"));
}
