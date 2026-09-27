using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VisyaDocs.App.Services;
using VisyaDocs.App.Views;
using VisyaDocs.Core;
using VisyaDocs.Platform;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;

namespace VisyaDocs.App;

public sealed partial class MainWindow : Window
{
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "VisyaDocs.ico"));
        AppWindow.Resize(new SizeInt32(1320, 900));
        AppWindow.Closing += AppWindow_Closing;

        ThemeService.Apply(this);
        Root.ActualThemeChanged += (_, _) =>
        {
            ThemeService.UpdateCaptionButtons(AppWindow, Root.ActualTheme);
            foreach (var view in DocumentViews) view.OnThemeChanged();
        };
        Root.SizeChanged += (_, _) => UpdateCaptionInset();
        Root.Loaded += (_, _) => UpdateCaptionInset();

        UpdateThemeMenu();
        RefreshRecent();
    }

    private IEnumerable<DocumentView> DocumentViews =>
        Tabs.TabItems.OfType<TabViewItem>().Select(t => t.Content).OfType<DocumentView>();

    private DocumentView? ActiveView => (Tabs.SelectedItem as TabViewItem)?.Content as DocumentView;

    /// <summary>Opens PDFs in tabs; image files are combined into a new PDF.</summary>
    public async void OpenFiles(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        foreach (var pdf in list.Where(IsPdf)) await OpenPdfAsync(pdf);
        var images = list.Where(p => ImageTools.SupportedImageExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())).ToList();
        if (images.Count > 0) await CreateFromImagesAsync(images);
    }

    public void ShowError(string title, string message)
    {
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private async Task OpenPdfAsync(string path)
    {
        var existing = Tabs.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(t => t.Content is DocumentView v && string.Equals(v.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Tabs.SelectedItem = existing;
            return;
        }

        string? password = null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var doc = await Task.Run(() => PdfDocument.Open(path, password));
                AddDocumentTab(doc, Path.GetFileName(path));
                AppSettings.Current.AddRecent(path);
                RefreshRecent();
                return;
            }
            catch (PdfPasswordException)
            {
                password = await Dialogs.AskPasswordAsync(Root.XamlRoot, Path.GetFileName(path), attempt > 0);
                if (password is null) return;
            }
            catch (Exception e) when (e is PdfException or IOException or UnauthorizedAccessException)
            {
                ShowError($"Could not open {Path.GetFileName(path)}", e.Message);
                return;
            }
        }
    }

    private void AddDocumentTab(PdfDocument doc, string name)
    {
        var view = new DocumentView(doc, name);
        var tab = new TabViewItem
        {
            Header = view.Title,
            IconSource = new FontIconSource { Glyph = "" },
            Content = view,
        };
        ToolTipService.SetToolTip(tab, doc.FilePath ?? name);
        view.TitleChanged += (_, _) =>
        {
            tab.Header = view.Title;
            UpdateWindowTitle();
        };
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
        UpdateHomeVisibility();
    }

    private async Task CreateFromImagesAsync(IReadOnlyList<string> images)
    {
        try
        {
            var doc = PdfDocument.CreateEmpty();
            await ImageTools.AppendImagesAsync(doc, images);
            AddDocumentTab(doc, Path.GetFileNameWithoutExtension(images[0]) + ".pdf");
        }
        catch (Exception e)
        {
            ShowError("Could not create a PDF from the images", e.Message);
        }
    }

    private async Task CreateMergedAsync(IReadOnlyList<string> pdfs)
    {
        var merged = PdfDocument.CreateEmpty();
        try
        {
            foreach (var path in pdfs)
            {
                using var part = await Task.Run(() => PdfDocument.Open(path));
                await Task.Run(() => merged.AppendDocument(part));
            }
            AddDocumentTab(merged, "Merged.pdf");
        }
        catch (Exception e)
        {
            merged.Dispose();
            ShowError("Could not merge the PDFs", e is PdfPasswordException
                ? "One of the files is password protected. Open it, save it without a password, then merge."
                : e.Message);
        }
    }

    private async Task<bool> CloseTabAsync(TabViewItem tab)
    {
        if (tab.Content is DocumentView view)
        {
            if (view.IsDirty)
            {
                Tabs.SelectedItem = tab;
                var answer = await Dialogs.ConfirmSaveAsync(Root.XamlRoot, view.Title.TrimEnd('*', ' '));
                if (answer == ContentDialogResult.None) return false;
                if (answer == ContentDialogResult.Primary && !await view.SaveAsync()) return false;
            }
            view.Dispose();
        }
        Tabs.TabItems.Remove(tab);
        UpdateHomeVisibility();
        return true;
    }

    private void UpdateHomeVisibility()
    {
        bool any = Tabs.TabItems.Count > 0;
        Tabs.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        HomePanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any) RefreshRecent();
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        var view = ActiveView;
        TitleDocument.Text = view is null ? string.Empty : view.Title;
        Title = view is null ? "VisyaDocs" : $"{view.Title} - VisyaDocs";
    }

    private void RefreshRecent()
    {
        RecentList.Children.Clear();
        foreach (var path in AppSettings.Current.RecentFiles.Where(File.Exists).Take(8))
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            content.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
            content.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(new TextBlock
            {
                Text = Path.GetDirectoryName(path),
                Opacity = 0.65,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var button = new Button
            {
                Content = content,
                Style = (Style)Application.Current.Resources["ToolButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += async (_, _) => await OpenPdfAsync(path);
            RecentList.Children.Add(button);
        }
        RecentSection.Visibility = RecentList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCaptionInset()
    {
        if (Root.XamlRoot is null) return;
        CaptionInset.Width = new GridLength(AppWindow.TitleBar.RightInset / Root.XamlRoot.RasterizationScale);
    }

    private void UpdateThemeMenu()
    {
        ThemeSystemItem.IsChecked = AppSettings.Current.Theme == AppTheme.System;
        ThemeLightItem.IsChecked = AppSettings.Current.Theme == AppTheme.Light;
        ThemeDarkItem.IsChecked = AppSettings.Current.Theme == AppTheme.Dark;
    }

    // Event handlers --------------------------------------------------------------------------

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var path = await Pickers.OpenFileAsync(".pdf");
        if (path is not null) await OpenPdfAsync(path);
    }

    private async void ImagesToPdf_Click(object sender, RoutedEventArgs e)
    {
        var images = await Pickers.OpenFilesAsync(ImageTools.SupportedImageExtensions);
        if (images.Count > 0) await CreateFromImagesAsync(images);
    }

    private async void MergePdfs_Click(object sender, RoutedEventArgs e)
    {
        var pdfs = await Pickers.OpenFilesAsync(".pdf");
        if (pdfs.Count > 0) await CreateMergedAsync(pdfs);
    }

    private void ThemeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<AppTheme>(tag, out var theme))
        {
            AppSettings.Current.Theme = theme;
            AppSettings.Current.Save();
            ThemeService.Apply(this);
            UpdateThemeMenu();
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await SettingsDialog.ShowAsync(Root.XamlRoot);
        ThemeService.Apply(this);
        UpdateThemeMenu();
        foreach (var view in DocumentViews) view.OnThemeChanged();
    }

    private async void OpenAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        var path = await Pickers.OpenFileAsync(".pdf");
        if (path is not null) await OpenPdfAsync(path);
    }

    private async void CloseTabAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Tabs.SelectedItem is TabViewItem tab) await CloseTabAsync(tab);
    }

    private async void Tabs_AddTabButtonClick(TabView sender, object args)
    {
        var path = await Pickers.OpenFileAsync(".pdf");
        if (path is not null) await OpenPdfAsync(path);
    }

    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) =>
        await CloseTabAsync(args.Tab);

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateWindowTitle();

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open with VisyaDocs";
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            OpenFiles(items.OfType<StorageFile>().Select(f => f.Path));
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingConfirmed || !DocumentViews.Any(v => v.IsDirty)) return;
        args.Cancel = true;
        foreach (var tab in Tabs.TabItems.OfType<TabViewItem>().ToList())
        {
            if (!await CloseTabAsync(tab)) return;
        }
        _closingConfirmed = true;
        Close();
    }
}
