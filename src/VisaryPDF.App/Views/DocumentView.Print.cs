using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VisaryPDF.Platform;

namespace VisaryPDF.App.Views;

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
        if (!_permissions.CanPrint)
        {
            var confirm = Services.Dialogs.Create(XamlRoot, "Print anyway?", new TextBlock
            {
                Text = "The author of this PDF does not allow printing. Only print it if you have the right to.",
                TextWrapping = TextWrapping.Wrap,
            }, "Print");
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }
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
        bool ok = await RunOperationAsync($"Printing {sheets} page(s)", async (progress, ct) =>
        {
            if (_permissions.CanPrint)
            {
                await PrintService.PrintAsync(_doc, job, _name, progress, ct);
                return;
            }
            // The author disallowed printing and the user chose to print anyway: print an
            // unrestricted copy held in memory (nothing is written to disk).
            using var copy = await Task.Run(_doc.CreateUnprotectedCopy, ct);
            await PrintService.PrintAsync(copy, job, _name, progress, ct);
        });
        if (ok) ShowMessage("Printing", $"Sent {sheets} page(s) to {job.PrinterName}.", InfoBarSeverity.Success, autoHide: true);
    }
}
