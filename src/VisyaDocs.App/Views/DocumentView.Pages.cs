using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VisyaDocs.App.Services;
using VisyaDocs.Core;

namespace VisyaDocs.App.Views;

// Page tools on the thumbnails: rotate, delete, extract.
public sealed partial class DocumentView
{
    private void ThumbList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ThumbnailItem thumb) return;
        e.Handled = true;
        int index = thumb.Index;
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = $"Page {index + 1}", IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(PageMenuItem("Rotate right", "", async () => await RotateAsync(index, +1)));
        menu.Items.Add(PageMenuItem("Rotate left", "", async () => await RotateAsync(index, -1)));
        menu.Items.Add(PageMenuItem("Extract this page...", "", async () => await ExtractAsync([index])));
        menu.Items.Add(PageMenuItem("Extract pages...", "", async () => await ExtractRangeAsync()));
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = PageMenuItem("Delete page", "", async () => await DeletePageAsync(index));
        delete.IsEnabled = _pages.Count > 1;
        menu.Items.Add(delete);
        menu.ShowAt(ThumbList, e.GetPosition(ThumbList));
    }

    private static MenuFlyoutItem PageMenuItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private async Task RotateAsync(int page, int quarterTurns)
    {
        if (!Permitted(_permissions.CanModify, "changing pages")) return;
        await EditAsync("Could not rotate the page", () => _doc.RotatePage(page, quarterTurns));
    }

    private async Task DeletePageAsync(int page)
    {
        if (!Permitted(_permissions.CanModify, "changing pages")) return;
        var confirm = Dialogs.Create(XamlRoot, $"Delete page {page + 1}?",
            new TextBlock { Text = "You can undo this with Ctrl+Z until you close the document.", TextWrapping = TextWrapping.Wrap }, "Delete");
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await EditAsync("Could not delete the page", () => _doc.DeletePage(page));
    }

    private async Task ExtractRangeAsync()
    {
        var options = await Dialogs.AskExportOptionsAsync(XamlRoot, "Extract pages to a new PDF", _doc.PageCount, images: false);
        if (options is null) return;
        await ExtractAsync(Core.Export.Converter.ParsePageRange(options.PageRange, _doc.PageCount));
    }

    private async Task ExtractAsync(IReadOnlyList<int> pages)
    {
        if (!Permitted(_permissions.CanCopy, "copying pages")) return;
        try
        {
            var extracted = await Task.Run(() => _doc.ExtractPages(pages));
            string name = $"{Path.GetFileNameWithoutExtension(_name)} (pages {(pages.Count == 1 ? $"{pages[0] + 1}" : $"{pages[0] + 1}-{pages[^1] + 1}")}).pdf";
            App.MainWindow.OpenDocument(extracted, name);
        }
        catch (PdfException ex)
        {
            ShowMessage("Could not extract the pages", ex.Message, InfoBarSeverity.Error);
        }
    }
}
