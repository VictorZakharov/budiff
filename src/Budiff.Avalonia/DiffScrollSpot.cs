namespace Budiff.Avalonia;

// A vertical scroll position in the diff view: of the formatted preview, or
// of the line diff. FileStart instead asks for where a file opened fresh
// begins - the top, or past a TypeScript file's imports (DiffViewModel.StartRow).
public sealed record DiffScrollSpot(bool Preview, double Offset, bool FileStart = false)
{
    public static readonly DiffScrollSpot Start = new(Preview: false, Offset: 0, FileStart: true);
}
