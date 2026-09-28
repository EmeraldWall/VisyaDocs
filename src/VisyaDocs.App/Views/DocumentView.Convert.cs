using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VisyaDocs.App.Services;
using VisyaDocs.Core;
using VisyaDocs.Core.Export;
using VisyaDocs.Platform;
using Windows.ApplicationModel.DataTransfer;

namespace VisyaDocs.App.Views;

// OCR and conversions.
public sealed partial class DocumentView
{
    private string BaseName => Path.GetFileNameWithoutExtension(_name);

    private IReadOnlyList<int> AllPages => Enumerable.Range(0, _doc.PageCount).ToArray();

    /// <summary>Opens the scanned document banner when the first pages look like scans.</summary>
    private async Task CheckScannedAsync()
    {
        try
        {
            int sample = Math.Min(_doc.PageCount, 5);
            bool scanned = await Task.Run(() => Enumerable.Range(0, sample).Any(_doc.IsLikelyScanned));
            ScanBar.IsOpen = scanned && !_disposed;
        }
        catch (PdfException)
        {
        }
    }

    private async void MakeSearchable_Click(object sender, RoutedEventArgs e)
    {
        if (!Permitted(_permissions.CanModify, "changes")) return;
        string recognized = string.Empty;
        bool ok = await RunOperationAsync("Recognizing text", async (progress, ct) =>
            recognized = await OcrService.MakeSearchableAsync(_doc, AllPages, AppSettings.Current.OcrLanguage, progress, ct));
        if (!ok) return;
        ScanBar.IsOpen = false;
        if (recognized.Length == 0)
        {
            ShowMessage("Nothing to recognize", "Every page already contains text.", InfoBarSeverity.Informational, autoHide: true);
            return;
        }
        ShowText("Recognized text", recognized);
        ShowMessage("Document is now searchable", "Scanned pages have an invisible text layer. Save to keep it.", InfoBarSeverity.Success);
    }

    private async void ExtractText_Click(object sender, RoutedEventArgs e)
    {
        if (!Permitted(_permissions.CanCopy, "copying text")) return;
        IReadOnlyList<string> texts = [];
        bool ok = await RunOperationAsync("Extracting text", async (progress, ct) => texts = await GetPageTextsAsync(AllPages, progress, ct));
        if (!ok) return;
        var sb = new StringBuilder();
        for (int i = 0; i < texts.Count; i++)
        {
            if (texts.Count > 1) sb.AppendLine($"--- Page {i + 1} ---");
            sb.AppendLine(texts[i].Trim()).AppendLine();
        }
        ShowText("Text", sb.ToString().TrimEnd());
    }

    /// <summary>Page texts, using OCR for scanned pages when an OCR language is installed.</summary>
    private async Task<IReadOnlyList<string>> GetPageTextsAsync(IReadOnlyList<int> pages, IProgress<double> progress, CancellationToken ct)
    {
        var texts = new List<string>(pages.Count);
        bool ocr = OcrService.IsAvailable;
        int skipped = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            int page = pages[i];
            if (await Task.Run(() => _doc.IsLikelyScanned(page), ct))
            {
                if (ocr) texts.Add((await OcrService.RecognizePageAsync(_doc, page, AppSettings.Current.OcrLanguage, ct)).Text);
                else
                {
                    texts.Add(string.Empty);
                    skipped++;
                }
            }
            else
            {
                texts.Add(await Task.Run(() => _doc.GetPageText(page), ct));
            }
            progress.Report((i + 1.0) / pages.Count);
        }
        if (skipped > 0)
        {
            ShowMessage("Some pages were not recognized",
                $"{skipped} scanned page(s) need an OCR language. Add one in Windows Settings > Time & language.", InfoBarSeverity.Warning);
        }
        return texts;
    }

    private void ShowText(string title, string text)
    {
        ExtractedText.Text = text;
        ShowSidePane(title, comments: false);
    }

    private void CopyExtracted_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(ExtractedText.Text);
        Clipboard.SetContent(package);
        ShowMessage("Copied", "Text copied to the clipboard.", InfoBarSeverity.Success, autoHide: true);
    }

    private async void SaveExtractedText_Click(object sender, RoutedEventArgs e)
    {
        var path = await Pickers.SaveFileAsync(BaseName, "Text file", ".txt");
        if (path is null) return;
        await SaveFileAsync(path, () => Converter.WriteText(path, [ExtractedText.Text]));
    }

    private async void SaveExtractedWord_Click(object sender, RoutedEventArgs e)
    {
        var path = await Pickers.SaveFileAsync(BaseName, "Word document", ".docx");
        if (path is null) return;
        string text = ExtractedText.Text;
        await SaveFileAsync(path, () => Converter.WriteDocx(path, [text]));
    }

    private async Task SaveFileAsync(string path, Action write)
    {
        try
        {
            await Task.Run(write);
            ShowMessage("Saved", path, InfoBarSeverity.Success, autoHide: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage("Could not save", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void ExportWord_Click(object sender, RoutedEventArgs e)
    {
        if (!Permitted(_permissions.CanCopy, "copying text")) return;
        await ExportDocumentTextAsync("Export to Word", "Word document", ".docx", Converter.WriteDocx);
    }

    private async void ExportText_Click(object sender, RoutedEventArgs e)
    {
        if (!Permitted(_permissions.CanCopy, "copying text")) return;
        await ExportDocumentTextAsync("Export to text", "Text file", ".txt", Converter.WriteText);
    }

    private async Task ExportDocumentTextAsync(string title, string typeName, string extension, Action<string, IReadOnlyList<string>> write)
    {
        var options = await Dialogs.AskExportOptionsAsync(XamlRoot, title, _doc.PageCount, images: false);
        if (options is null) return;
        var path = await Pickers.SaveFileAsync(BaseName, typeName, extension);
        if (path is null) return;
        var pages = Converter.ParsePageRange(options.PageRange, _doc.PageCount);
        bool ok = await RunOperationAsync(title, async (progress, ct) =>
        {
            var texts = await GetPageTextsAsync(pages, progress, ct);
            await Task.Run(() => write(path, texts), ct);
        });
        if (ok) ShowMessage("Exported", path, InfoBarSeverity.Success, autoHide: true);
    }

    private async void ExportImages_Click(object sender, RoutedEventArgs e)
    {
        if (!Permitted(_permissions.CanCopy, "copying its pages")) return;
        var format = (sender as FrameworkElement)?.Tag as string == "Jpeg" ? ExportFormat.Jpeg : ExportFormat.Png;
        var options = await Dialogs.AskExportOptionsAsync(XamlRoot, $"Export pages as {(format == ExportFormat.Jpeg ? "JPEG" : "PNG")}",
            _doc.PageCount, images: true);
        if (options is null) return;
        var folder = await Pickers.PickFolderAsync();
        if (folder is null) return;
        var pages = Converter.ParsePageRange(options.PageRange, _doc.PageCount);
        IReadOnlyList<string> files = [];
        bool ok = await RunOperationAsync("Exporting images", (progress, ct) => Task.Run(() =>
            files = Converter.ExportImages(_doc, folder, BaseName, pages, format, options.Dpi, ImageTools.EncodeJpeg, progress, ct), ct));
        if (ok) ShowMessage("Exported", $"{files.Count} image(s) saved to {folder}", InfoBarSeverity.Success);
    }

    private async void AppendPdf_Click(object sender, RoutedEventArgs e)
    {
        if (!Permitted(_permissions.CanModify, "changes")) return;
        var files = await Pickers.OpenFilesAsync(".pdf");
        if (files.Count == 0) return;
        await RunOperationAsync("Appending pages", async (progress, ct) =>
        {
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var other = await Task.Run(() => PdfDocument.Open(files[i]), ct);
                await Task.Run(() => _doc.AppendDocument(other), ct);
                progress.Report((i + 1.0) / files.Count);
            }
        });
    }

    private async void AppendImages_Click(object sender, RoutedEventArgs e)
    {
        if (!Permitted(_permissions.CanModify, "changes")) return;
        var files = await Pickers.OpenFilesAsync(ImageTools.SupportedImageExtensions);
        if (files.Count == 0) return;
        await RunOperationAsync("Adding images", (progress, ct) => ImageTools.AppendImagesAsync(_doc, files, progress, ct));
    }
}
