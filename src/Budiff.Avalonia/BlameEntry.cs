using global::Avalonia.Media;

namespace Budiff.Avalonia;

// One line's blame, pre-formatted by the host (this library stays git-free).
// Label is the compact gutter text ("author age"); the rest feeds the hover
// tooltip with the commit's details. Shade tints the gutter cell by how
// recent the line is (null = none).
public sealed record BlameEntry(string Label, string Sha, string Author, string Email, string Date, string Message, IBrush? Shade = null);

// A file's blame for both sides of its diff, indexed by 0-based line; a null
// side has none.
public sealed record BlameSides(IReadOnlyList<BlameEntry?>? Old, IReadOnlyList<BlameEntry?>? New);
