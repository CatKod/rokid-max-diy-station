// SPDX-License-Identifier: MIT
//
// JSON-backed settings persistence for the tray app.
//
// We deliberately don't use the .NET options-pattern here: the tray
// app is the only consumer of the file and we want atomic, well
// defined semantics — read once on startup, write on exit (and on
// every change if the user wants, but the UI batches).
using System.IO;
using System.Text.Json;

namespace RokidDiyStation.App;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string FilePath { get; }
    public AppSettings Current { get; private set; } = AppSettings.Default;

    public SettingsService(string? filePath = null)
    {
        FilePath = filePath ?? DefaultPath();
    }

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RokidDiyStation");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Current = AppSettings.Default;
                Save();
                return Current;
            }
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
            Current = loaded ?? AppSettings.Default;
            return Current;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable: fall back to defaults.  We do NOT
            // throw because the user has to be able to start the app
            // to fix it.
            Current = AppSettings.Default;
            return Current;
        }
    }

    /// <summary>Persist the current settings.  Writes to a temp file then
    /// renames atomically to avoid leaving a half-written settings.json
    /// on power loss.</summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath) ?? ".";
        Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOpts));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
        else File.Move(tmp, FilePath);
    }

    public void Update(AppSettings next)
    {
        Current = next;
        Save();
    }
}
