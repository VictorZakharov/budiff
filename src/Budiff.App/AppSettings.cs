using System.IO;
using System.Text.Json;

namespace Budiff.App;

// What the GUI remembers between runs: for now the bird's-eye columns the user
// turned off. One small JSON file under the user's app-data folder
// (%APPDATA%\budiff\settings.json on Windows); BUDIFF_SETTINGS_DIR points it
// elsewhere (the tests use a scratch folder). Read and written by hand with
// System.Text.Json's reader / writer - no reflection, so NativeAOT stays happy.
// A missing or unreadable file means the defaults; a failed save is dropped -
// remembering a preference is never worth an error.
internal sealed class AppSettings
{
    public const string DirectoryVariable = "BUDIFF_SETTINGS_DIR";

    private AppSettings(string path) => FilePath = path;

    public string FilePath { get; }

    public HashSet<string> HiddenColumns { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath
    {
        get
        {
            var folder = Environment.GetEnvironmentVariable(DirectoryVariable);
            if (string.IsNullOrEmpty(folder))
            {
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "budiff");
            }

            return Path.Combine(folder, "settings.json");
        }
    }

    public static AppSettings Load(string path)
    {
        var settings = new AppSettings(path);
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            if (json.RootElement.TryGetProperty("hiddenColumns", out var hidden) && hidden.ValueKind == JsonValueKind.Array)
            {
                foreach (var column in hidden.EnumerateArray())
                {
                    if (column.GetString() is { Length: > 0 } name)
                    {
                        settings.HiddenColumns.Add(name);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // No file yet, or a damaged one: start from the defaults.
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("hiddenColumns");
                foreach (var column in HiddenColumns.Order(StringComparer.OrdinalIgnoreCase))
                {
                    writer.WriteStringValue(column);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            // Write aside, then swap in: a crash mid-write never leaves half a file.
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, buffer.ToArray());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
