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
// (rows' line numbers are 1-based into OldText / NewText split on '\n').
public sealed record DocumentPreviewRequest(
    string OldText,
    string NewText,
    IReadOnlyList<DiffRow> Rows,
    DocumentPreviewMode Mode,
    bool CollapseUnchanged);

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
