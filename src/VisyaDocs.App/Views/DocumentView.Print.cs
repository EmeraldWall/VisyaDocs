using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VisyaDocs.Platform;

namespace VisyaDocs.App.Views;

// Printing through the standard Windows print dialog.
public sealed partial class DocumentView
{
    private async void Print_Click(object sender, RoutedEventArgs e) => await PrintAsync();

    private async void PrintAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PrintAsync();
    }

    private async Task PrintAsync()
    {
        if (_pages.Count == 0 || _operation is not null) return;
        if (!Permitted(_permissions.CanPrint, "printing")) return;
        CommitEditor();

        PrintJob? job;
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            job = PrintService.ShowPrintDialog(hwnd, _doc.PageCount, _currentPage);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowMessage("Could not open the print dialog", ex.Message, InfoBarSeverity.Error);
            return;
        }
        if (job is null) return;

        int sheets = job.Pages.Count * job.Copies;
        bool ok = await RunOperationAsync($"Printing {sheets} page(s)", (progress, ct) =>
            PrintService.PrintAsync(_doc, job, _name, progress, ct));
        if (ok) ShowMessage("Printing", $"Sent {sheets} page(s) to {job.PrinterName}.", InfoBarSeverity.Success, autoHide: true);
    }
}
