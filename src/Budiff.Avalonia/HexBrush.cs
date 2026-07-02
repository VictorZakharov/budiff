using global::Avalonia.Media;
using global::Avalonia.Media.Immutable;

namespace Budiff.Avalonia;

public static class HexBrush
{
    // Immutable so the brush is safe to create and use off the UI thread.
    public static IBrush Parse(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}
