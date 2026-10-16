namespace Budiff.Avalonia;

// A vertical scroll position in the diff view: of the formatted preview, or
// of the line diff.
public sealed record DiffScrollSpot(bool Preview, double Offset);
