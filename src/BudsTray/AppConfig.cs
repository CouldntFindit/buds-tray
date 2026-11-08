using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BudsTray;

public enum AppTheme
{
    System,
    Light,
    Dark
}

public sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new AppThemeConverter() }
    };

    public string? DeviceAddress { get; set; }
    public string? DeviceName { get; set; }
    public bool ShowPercentage { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>
    /// Ear type reported by the last connection. It lets the sound-mode menu
    /// show the right rows immediately on startup, before a status frame has
    /// arrived; a fresh status frame overwrites it.
    /// </summary>
    public int? LastEarType { get; set; }

    /// <summary>
    /// When Samsung's official Galaxy Buds app owns the exclusive SPP channel,
    /// close its background processes (QuickControls.exe and BudsManager.exe)
    /// so BudsTray can connect. Set to <c>false</c> to only wait for the channel
    /// instead of stopping the other app.
    /// </summary>
    public bool TakeOverFromGalaxyBudsApp { get; set; } = true;

    private static string DirectoryPath => PortablePaths.DataDirectory;

    public static string FilePath => Path.Combine(DirectoryPath, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppConfig();
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load config", ex);
            return new AppConfig();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save config", ex);
        }
    }
}

/// <summary>
/// Reads the theme as either a name ("Dark") or a legacy number (2), so configs
/// written by older builds keep their device pairing instead of failing to load.
/// </summary>
public sealed class AppThemeConverter : JsonConverter<AppTheme>
{
    public override AppTheme Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
            return Enum.IsDefined(typeof(AppTheme), number) ? (AppTheme)number : AppTheme.System;

        if (reader.TokenType == JsonTokenType.String)
            return Enum.TryParse<AppTheme>(reader.GetString(), ignoreCase: true, out var theme)
                ? theme
                : AppTheme.System;

        return AppTheme.System;
    }

    public override void Write(Utf8JsonWriter writer, AppTheme value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
