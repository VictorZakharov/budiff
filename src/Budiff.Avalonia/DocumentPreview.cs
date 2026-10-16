using System;
using System.Collections.Generic;
using Budiff;
using global::Avalonia.Controls;
using global::Avalonia.Media;

namespace Budiff.Avalonia;

// What a formatted preview shows: the change between the two sides, or just
// one side when there is nothing to compare (identical, wholly added, or
// wholly deleted).
public enum DocumentPreviewMode
{
    Diff,
    NewOnly,
    OldOnly,
}

// Everything a previewer needs: both sides' full text plus the line diff
// (rows' line numbers are 1-based into OldText / NewText split on '\n'), and
// optionally the project around the document.
public sealed record DocumentPreviewRequest(
    string OldText,
    string NewText,
    IReadOnlyList<DiffRow> Rows,
    DocumentPreviewMode Mode,
    bool CollapseUnchanged,
    DocumentPreviewContext? Context = null);

// Which side of the diff a previewed block (and what it references) is from.
public enum DocumentSide
{
    Old,
    New,
}

// The project around a previewed document, so a preview can show the images
// it references and follow its links. Paths are relative to the project
// root and '/'-separated; DocumentPath is the previewed file itself.
//   ReadFile - a project file's bytes as it is on one side of the diff, or
//              null when it is absent there. Called OFF the UI thread.
//   OpenFile - shows another project file (a relative link was clicked).
public sealed record DocumentPreviewContext(
    string DocumentPath,
    Func<string, DocumentSide, byte[]?> ReadFile,
    Action<string> OpenFile);

// The diff view's colors, so a preview matches the surrounding theme.
public sealed record DocumentPreviewTheme(
    IBrush Text,
    IBrush MutedText,
    IBrush Border,
    IBrush AddedBackground,
    IBrush RemovedBackground,
    IBrush AddedMarker,
    IBrush RemovedMarker);

// Renders a document type (e.g. markdown) formatted, with its changes
// marked, in place of the line diff. The diff control depends only on this
// interface; implementations live in their own packages.
public interface IDocumentPreviewer
{
    bool CanPreview(string extension);

    // Called on the UI thread; returns a self-contained, scrollable view.
    Control Render(DocumentPreviewRequest request, DocumentPreviewTheme theme);
}
