using System.Diagnostics;

namespace VisaryPDF.App.Services;

/// <summary>
/// Memory check used by CI: when VISARYPDF_MEMTEST names an output file, the app opens the PDF from
/// the command line, scrolls through every page at 200% (like a high DPI screen), writes its memory
/// use to that file and exits. Not used in normal runs.
/// </summary>
internal static class SelfTest
{
    public static string? OutputPath => Environment.GetEnvironmentVariable("VISARYPDF_MEMTEST");

    public static async Task RunAsync(string output)
    {
        var lines = new List<string>();
        void Sample(string label)
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            lines.Add($"{label}: working set {process.WorkingSet64 >> 20} MB, private {process.PrivateMemorySize64 >> 20} MB, " +
                $"managed heap {GC.GetGCMemoryInfo().HeapSizeBytes >> 20} MB");
        }

        await Task.Delay(5000);
        var view = App.MainWindow.ActiveView;
        if (view is null)
        {
            lines.Add("No document was opened.");
        }
        else
        {
            Sample($"Open ({view.PageCount} pages)");
            await view.TourAsync(2.0, 150);
            Sample("After scrolling through every page at 200%");
            await Task.Delay(5000);
            Sample("5 seconds later");
        }
        File.WriteAllLines(output, lines);
        App.Current.Exit();
    }
}
