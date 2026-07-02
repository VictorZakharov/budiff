namespace Budiff.Avalonia;

// One line's blame, pre-formatted by the host (this library stays git-free).
// Label is the compact gutter text ("author age"); the rest feeds the hover
// tooltip with the commit's details.
public sealed record BlameEntry(string Label, string Sha, string Author, string Email, string Date, string Message);
