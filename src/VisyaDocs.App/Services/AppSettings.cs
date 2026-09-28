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

/// <summary>User preferences, stored as JSON in %LocalAppData%\VisaryPDF\settings.json.</summary>
public sealed class AppSettings
{
    private const int MaxRecent = 10;

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name);

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

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
    public RailSide RailSide { get; set; } = RailSide.Right;

    /// <summary>Vertical position of the tool rail as a fraction of the free space (0 top, 0.5 centered).</summary>
    public double RailTop { get; set; }

    /// <summary>Last page read per file (most recent first), restored when the file is opened again.</summary>
    public List<FilePosition> Positions { get; set; } = [];

    /// <summary>Version of the tool bar defaults last applied (see <see cref="Upgrade"/>).</summary>
    public int ToolBarDefaults { get; set; }

    /// <summary>Whether the one-time welcome tip has been shown.</summary>
    public bool TipShown { get; set; }

    public int? GetPosition(string path) =>
        Positions.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))?.Page;

    public void SetPosition(string path, int page)
    {
        Positions.RemoveAll(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        Positions.Insert(0, new FilePosition { Path = path, Page = page });
        if (Positions.Count > 50) Positions.RemoveRange(50, Positions.Count - 50);
        Save();
    }

    public static string SignaturesFolder { get; } = Path.Combine(Folder, "signatures");

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
        MoveFromOldName();
        AppSettings? settings = null;
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.AppSettings);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        settings ??= new AppSettings();
        settings.Upgrade();
        return settings;
    }

    /// <summary>The app was called VisyaDocs before; its settings and saved signatures carry over once.</summary>
    private static void MoveFromOldName()
    {
        try
        {
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisyaDocs");
            if (Directory.Exists(Folder) || !Directory.Exists(old)) return;
            foreach (var file in Directory.GetFiles(old, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(Folder, Path.GetRelativePath(old, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Moves existing installs to new defaults once: the tool bar now starts open at the top of the
    /// right side. After that the user's own placement is kept.
    /// </summary>
    private void Upgrade()
    {
        if (ToolBarDefaults >= 1) return;
        RailSide = RailSide.Right;
        RailTop = 0;
        RailCollapsed = false;
        ToolBarDefaults = 1;
        Save();
    }
}

public sealed class FilePosition
{
    public string Path { get; set; } = string.Empty;
    public int Page { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
