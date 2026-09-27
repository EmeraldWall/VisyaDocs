using Microsoft.UI.Xaml;
using VisyaDocs.Core;

namespace VisyaDocs.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public static MainWindow MainWindow { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Text outside Latin-1 (Cyrillic, Greek, Arabic, ...) is written with a Windows font.
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        PdfDocument.FallbackFontPath = new[] { "arial.ttf", "segoeui.ttf" }
            .Select(f => Path.Combine(fonts, f)).FirstOrDefault(File.Exists);

        MainWindow = new MainWindow();
        MainWindow.Activate();

        var files = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToArray();
        if (files.Length > 0) MainWindow.OpenFiles(files);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Keep the app (and any unsaved work) alive; report the problem instead of crashing.
        e.Handled = true;
        MainWindow?.ShowError("Something went wrong", e.Exception?.Message ?? e.Message);
    }
}
