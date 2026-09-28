using Microsoft.UI.Xaml;
using VisyaDocs.App.Services;
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

        // A plain folder install gets files on the command line; an installed package opened from a
        // .pdf gets a file activation instead.
        var files = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToList();
        if (files.Count == 0)
            files = FilesFrom(Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs());
        if (files.Count > 0) MainWindow.OpenFiles(files);
        if (SelfTest.OutputPath is { Length: > 0 } output) _ = SelfTest.RunAsync(output);
    }

    /// <summary>Another launch (for example a double-clicked PDF) was handed to this instance.</summary>
    public static void OnRedirectedActivation(Microsoft.Windows.AppLifecycle.AppActivationArguments args)
    {
        var files = FilesFrom(args);
        MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            MainWindow.BringToFront();
            if (files.Count > 0) MainWindow.OpenFiles(files);
        });
    }

    /// <summary>Files handed over by a file activation (packaged app) or a launch command line.</summary>
    private static List<string> FilesFrom(Microsoft.Windows.AppLifecycle.AppActivationArguments args)
    {
        var files = new List<string>();
        if (args.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File
            && args.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs)
        {
            files.AddRange(fileArgs.Files.Select(f => f.Path).Where(File.Exists));
        }
        else if (args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
        {
            files.AddRange(CommandLine.Split(launch.Arguments).Where(File.Exists));
        }
        return files;
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Keep the app (and any unsaved work) alive; report the problem instead of crashing.
        e.Handled = true;
        MainWindow?.ShowError("Something went wrong", e.Exception?.Message ?? e.Message);
    }
}
