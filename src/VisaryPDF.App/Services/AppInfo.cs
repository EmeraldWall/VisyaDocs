namespace VisaryPDF.App.Services;

/// <summary>The product name shown to people, and how the app was installed.</summary>
public static class AppInfo
{
    public const string Name = "VisaryPDF";

    /// <summary>Version for people: "1" for 1.0.0, "1.2" for 1.2.0, "1.2.3" for 1.2.3.</summary>
    public static string Version { get; } = FormatVersion(typeof(AppInfo).Assembly.GetName().Version);

    private static string FormatVersion(System.Version? v) =>
        v is null ? "1" : v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : v.Minor > 0 ? $"{v.Major}.{v.Minor}" : $"{v.Major}";

    /// <summary>
    /// True when running from an MSIX package (Microsoft Store or a sideloaded package); false when run
    /// from a plain folder. Packaged apps get their .pdf association from the package manifest.
    /// </summary>
    public static bool IsPackaged { get; } = DetectPackaged();

    private static bool DetectPackaged()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
