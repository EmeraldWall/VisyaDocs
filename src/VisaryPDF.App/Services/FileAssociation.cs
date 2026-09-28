using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VisaryPDF.App.Services;

/// <summary>
/// Lets Windows offer VisaryPDF for PDF files (plain folder installs; a package declares this in its manifest). Registers the app for the current user only (no admin
/// rights needed); Windows then lets the user pick it as the default in Settings > Default apps.
/// </summary>
public static partial class FileAssociation
{
    private const string ProgId = "VisaryPDF.Pdf";
    private const string AppName = AppInfo.Name;
    private const string ExeName = AppInfo.Name + ".exe";

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeName);

    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            return key?.GetValue(null) is string command && command.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Register()
    {
        RemoveOldName();
        string command = $"\"{ExePath}\" \"%1\"";
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "PdfFile.ico");
        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue(null, "PDF Document");
            using (var defaultIcon = progId.CreateSubKey("DefaultIcon")) defaultIcon.SetValue(null, icon);
            using var open = progId.CreateSubKey(@"shell\open\command");
            open.SetValue(null, command);
        }
        using (var openWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        using (var app = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{ExeName}"))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var open = app.CreateSubKey(@"shell\open\command")) open.SetValue(null, command);
            using var types = app.CreateSubKey("SupportedTypes");
            types.SetValue(".pdf", string.Empty);
        }
        using (var capabilities = Registry.CurrentUser.CreateSubKey($@"Software\{AppName}\Capabilities"))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", "Read, fill, sign and convert PDF files");
            capabilities.SetValue("ApplicationIcon", $"\"{ExePath}\",0");
            using var associations = capabilities.CreateSubKey("FileAssociations");
            associations.SetValue(".pdf", ProgId);
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue(AppName, $@"Software\{AppName}\Capabilities");
        NotifyShell();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\Applications\{ExeName}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\{AppName}", throwOnMissingSubKey: false);
        using (var openWith = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", writable: true))
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            registered?.DeleteValue(AppName, throwOnMissingValue: false);
        NotifyShell();
    }

    /// <summary>Opens Windows Default apps on the app's page (Windows 11), or the general page.</summary>
    public static async Task OpenDefaultAppsSettingsAsync()
    {
        if (!await Windows.System.Launcher.LaunchUriAsync(new Uri($"ms-settings:defaultapps?registeredAppUser={AppName}")))
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
    }

    /// <summary>Removes the registration made under the app's former name (VisyaDocs).</summary>
    private static void RemoveOldName()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\VisyaDocs.Pdf", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\VisyaDocs.exe", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\VisyaDocs", throwOnMissingSubKey: false);
        using (var openWith = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", writable: true))
            openWith?.DeleteValue("VisyaDocs.Pdf", throwOnMissingValue: false);
        using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            registered?.DeleteValue("VisyaDocs", throwOnMissingValue: false);
    }

    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, 0, 0);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
