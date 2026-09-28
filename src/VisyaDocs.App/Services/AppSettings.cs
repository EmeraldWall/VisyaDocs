using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisyaDocs.App.Services;

public enum ViewLayout
{
    Continuous,
    TwoPages,
    TwoPagesCover,
    SinglePage,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
    Black,
}

public enum RailSide
{
    Left,
    Right,
}

/// <summary>User preferences, stored as JSON in %LocalAppData%\VisyaDocs\settings.json.</summary>
public sealed class AppSettings
{
    private const int MaxRecent = 10;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisyaDocs", "settings.json");

    public static AppSettings Current { get; } = Load();

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Slightly dims page content in the dark theme for comfortable night reading.</summary>
    public bool DimPagesInDark { get; set; } = true;

    /// <summary>OCR language tag, or null for the user's Windows display languages.</summary>
    public string? OcrLanguage { get; set; }

    /// <summary>Name written as the author of new comments.</summary>
    public string Author { get; set; } = Environment.UserName;

    public List<string> RecentFiles { get; set; } = [];

    /// <summary>Page layout used when reading.</summary>
    public ViewLayout Layout { get; set; } = ViewLayout.Continuous;

    /// <summary>Whether the floating tool rail is collapsed to its handle.</summary>
    public bool RailCollapsed { get; set; }

    /// <summary>Side of the page area the tool rail is docked to.</summary>
    public RailSide RailSide { get; set; } = RailSide.Left;

    /// <summary>Vertical position of the tool rail as a fraction of the free space (0 top, 0.5 centered).</summary>
    public double RailTop { get; set; } = 0.5;

    public static string SignaturesFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisyaDocs", "signatures");

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecent) RecentFiles.RemoveRange(MaxRecent, RecentFiles.Count - MaxRecent);
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings));
        }
        catch (IOException)
        {
            // Settings are a convenience; failing to persist them must never break the app.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.AppSettings) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
