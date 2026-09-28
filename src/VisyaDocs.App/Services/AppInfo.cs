namespace VisyaDocs.App.Services;

/// <summary>The product name shown to people, and how the app was installed.</summary>
public static class AppInfo
{
    public const string Name = "VisaryPDF";

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
