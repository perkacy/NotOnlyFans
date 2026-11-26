using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NotOnlyFans.Services;

/// <summary>
/// The window app's own files in %LOCALAPPDATA%\NotOnlyFans (pins, names, settings).
/// Written in one step (a crash or power cut mid-write never leaves half a file); a damaged file is kept
/// as *.damaged instead of being overwritten, so the user's names and pins are never lost silently.
/// </summary>
internal static class AppFiles
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotOnlyFans");

    /// <summary>The saved value; null if there is none or it can't be read (the caller starts from defaults).</summary>
    public static T? Load<T>(string fileName, JsonTypeInfo<T> type) where T : class
    {
        string path = Path.Combine(Folder, fileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) : null;
        }
        catch (JsonException ex)
        {
            App.LogError(ex);
            try
            {
                File.Copy(path, path + ".damaged", overwrite: true);
            }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
            {
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            App.LogError(ex);
            return null;
        }
    }

    /// <summary>Saves; on failure the change still applies for this session (and the error goes to crash.log).</summary>
    public static void Save<T>(string fileName, T value, JsonTypeInfo<T> type)
    {
        string path = Path.Combine(Folder, fileName);
        try
        {
            Directory.CreateDirectory(Folder);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, type));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            App.LogError(ex);
        }
    }
}
