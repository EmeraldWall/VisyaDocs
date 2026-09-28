using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    /// <summary>A compact tab in the title bar and the document it shows.</summary>
    private sealed class DocTab(DocumentView view, Border header, TextBlock title)
    {
        public DocumentView View { get; } = view;
        public Border Header { get; } = header;
        public TextBlock Title { get; } = title;
    }

    private readonly List<DocTab> _tabs = [];
    private DocTab? _active;
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "VisyaDocs.ico"));
        AppWindow.Resize(new SizeInt32(1320, 900));
        AppWindow.Closing += AppWindow_Closing;

        ThemeService.Apply(this);
        Root.ActualThemeChanged += (_, _) =>
        {
            ThemeService.UpdateCaptionButtons(AppWindow, Root.ActualTheme);
            foreach (var tab in _tabs) tab.View.OnThemeChanged();
            UpdateTabVisuals();
        };
        Root.SizeChanged += (_, _) => UpdateTitleBarRegions();
        Root.Loaded += (_, _) => UpdateTitleBarRegions();
        TabStrip.SizeChanged += (_, _) => UpdateTitleBarRegions();
        TabScroller.SizeChanged += (_, _) => UpdateTitleBarRegions();

        RefreshRecent();
    }

    /// <summary>The document in the active tab, if any.</summary>
    internal DocumentView? ActiveView => _active?.View;

    public bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

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

    /// <summary>Switches between full screen and a normal window. Full screen hides the title bar.</summary>
    public void ToggleFullScreen() => SetFullScreen(!IsFullScreen);

    public void SetFullScreen(bool fullScreen)
    {
        if (fullScreen == IsFullScreen) return;
        AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        TitleRow.Height = new GridLength(fullScreen ? 0 : 32);
        AppTitleBar.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        foreach (var tab in _tabs) tab.View.OnFullScreenChanged(fullScreen);
        UpdateTitleBarRegions();
    }

    /// <summary>Shows a document created elsewhere in the app (for example extracted pages) in a new tab.</summary>
    public void OpenDocument(PdfDocument doc, string name) => AddDocumentTab(doc, name);

    /// <summary>Restores and focuses the window (used when another launch hands us a file).</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial int SetForegroundWindow(nint hwnd);

    private static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private async Task OpenPdfAsync(string path)
    {
        var existing = _tabs.FirstOrDefault(t => string.Equals(t.View.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Activate(existing);
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

    // Tabs ------------------------------------------------------------------------------------

    private void AddDocumentTab(PdfDocument doc, string name)
    {
        var view = new DocumentView(doc, name) { Visibility = Visibility.Collapsed };
        view.OnFullScreenChanged(IsFullScreen);
        DocumentHost.Children.Add(view);

        var title = new TextBlock
        {
            Text = view.Title,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 150,
        };
        var close = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 9 },
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(close, "Close (Ctrl+W)");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new AppIcon { Icon = "pdf", Size = 14, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(title);
        content.Children.Add(close);
        var header = new Border
        {
            Child = content,
            Height = 26,
            Padding = new Thickness(10, 0, 4, 0),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 90,
            MaxWidth = 210,
        };
        ToolTipService.SetToolTip(header, doc.FilePath ?? name);

        var tab = new DocTab(view, header, title);
        header.Tapped += (_, _) => Activate(tab);
        header.PointerReleased += async (_, e) =>
        {
            // Middle click closes, like browsers.
            if (e.GetCurrentPoint(header).Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased) await CloseTabAsync(tab);
        };
        close.Click += async (_, _) => await CloseTabAsync(tab);
        view.TitleChanged += (_, _) =>
        {
            title.Text = view.Title;
            UpdateWindowTitle();
        };

        _tabs.Add(tab);
        TabStrip.Children.Add(header);
        Activate(tab);
        UpdateHomeVisibility();
    }

    private void Activate(DocTab tab)
    {
        CloseSettings();
        _active = tab;
        foreach (var t in _tabs) t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
        UpdateTabVisuals();
        UpdateWindowTitle();
        tab.Header.StartBringIntoView();
        DispatcherQueue.TryEnqueue(tab.View.FocusViewer);
    }

    private void UpdateTabVisuals()
    {
        var selected = new SolidColorBrush(ThemeService.SelectedTabColor(Root.ActualTheme));
        foreach (var t in _tabs)
        {
            t.Header.Background = t == _active ? selected : new SolidColorBrush(Colors.Transparent);
            t.Title.FontWeight = t == _active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    private async Task<bool> CloseTabAsync(DocTab tab)
    {
        if (tab.View.IsDirty)
        {
            Activate(tab);
            var answer = await Dialogs.ConfirmSaveAsync(Root.XamlRoot, tab.View.Title.TrimEnd('*', ' '));
            if (answer == ContentDialogResult.None) return false;
            if (answer == ContentDialogResult.Primary && !await tab.View.SaveAsync()) return false;
        }
        int index = _tabs.IndexOf(tab);
        tab.View.Dispose();
        DocumentHost.Children.Remove(tab.View);
        TabStrip.Children.Remove(tab.Header);
        _tabs.Remove(tab);
        if (_active == tab)
        {
            _active = null;
            if (_tabs.Count > 0) Activate(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        UpdateHomeVisibility();
        return true;
    }

    private void UpdateHomeVisibility()
    {
        HomePanel.Visibility = _tabs.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_tabs.Count == 0)
        {
            RefreshRecent();
            if (IsFullScreen) SetFullScreen(false);
        }
        UpdateWindowTitle();
        UpdateTitleBarRegions();
    }

    private void UpdateWindowTitle()
    {
        string? title = _active?.View.Title;
        Title = title is null ? "VisyaDocs" : $"{title} - VisyaDocs";
    }

    /// <summary>
    /// The title bar row is the drag area; the File button, the tabs and the + button must stay
    /// clickable, so they are registered as pass-through regions (in physical pixels).
    /// </summary>
    private void UpdateTitleBarRegions()
    {
        if (Root.XamlRoot is null) return;
        double scale = Root.XamlRoot.RasterizationScale;
        CaptionInset.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
        var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        if (IsFullScreen)
        {
            source.ClearRegionRects(NonClientRegionKind.Passthrough);
            return;
        }
        RectInt32 Rect(FrameworkElement e, double width)
        {
            var p = e.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            return new RectInt32((int)Math.Round(p.X * scale), (int)Math.Round(p.Y * scale),
                (int)Math.Round(width * scale), (int)Math.Round(e.ActualHeight * scale));
        }
        // Only the part of the tab area that actually holds tabs is clickable; the rest stays draggable.
        double tabsWidth = Math.Min(TabScroller.ActualWidth, TabStrip.ActualWidth);
        var rects = new List<RectInt32> { Rect(MenuButton, MenuButton.ActualWidth), Rect(NewTabButton, NewTabButton.ActualWidth) };
        if (tabsWidth > 0) rects.Add(Rect(TabScroller, tabsWidth));
        source.SetRegionRects(NonClientRegionKind.Passthrough, [.. rects]);
    }

    // Settings page ---------------------------------------------------------------------------

    private SettingsPage? _settingsPage;

    private void OpenSettings()
    {
        if (_settingsPage is null)
        {
            _settingsPage = new SettingsPage();
            _settingsPage.CloseRequested += (_, _) => CloseSettings();
            _settingsPage.SettingsChanged += (_, _) =>
            {
                ThemeService.Apply(this);
                UpdateTabVisuals();
                foreach (var tab in _tabs) tab.View.OnSettingsChanged();
            };
            SettingsHost.Children.Add(_settingsPage);
        }
        SettingsHost.Visibility = Visibility.Visible;
    }

    private void CloseSettings()
    {
        if (_settingsPage is null) return;
        SettingsHost.Visibility = Visibility.Collapsed;
        SettingsHost.Children.Clear();
        _settingsPage = null;
    }

    // Menu ------------------------------------------------------------------------------------

    private void RefreshRecent()
    {
        RecentList.Children.Clear();
        RecentMenu.Items.Clear();
        var recent = AppSettings.Current.RecentFiles.Where(File.Exists).Take(8).ToList();
        foreach (var path in recent)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            content.Children.Add(new AppIcon { Icon = "pdf", Size = 16 });
            content.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = Path.GetDirectoryName(path), Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new Button
            {
                Content = content,
                Style = (Style)Application.Current.Resources["ToolButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += async (_, _) => await OpenPdfAsync(path);
            RecentList.Children.Add(button);

            var item = new MenuFlyoutItem { Text = Path.GetFileName(path) };
            ToolTipService.SetToolTip(item, path);
            item.Click += async (_, _) => await OpenPdfAsync(path);
            RecentMenu.Items.Add(item);
        }
        RecentSection.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentMenu.IsEnabled = recent.Count > 0;
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


    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void AppMenu_Opening(object? sender, object e)
    {
        bool hasDocument = _active is not null;
        SaveItem.IsEnabled = SaveAsItem.IsEnabled = PrintItem.IsEnabled = PropertiesItem.IsEnabled = ConvertMenu.IsEnabled = hasDocument;
        // Only for files opened with their password and full rights (see PdfDocument.CanRemovePassword).
        RemovePasswordItem.Visibility = _active?.View.CanRemovePassword == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Shortcuts_Click(object sender, RoutedEventArgs e) => await ShowShortcutsAsync();

    private async void ShortcutsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ShowShortcutsAsync();
    }

    private static readonly (string Keys, string Action)[] Shortcuts =
    [
        ("Ctrl+O", "Open a PDF"),
        ("Ctrl+S / Ctrl+Shift+S", "Save / Save as"),
        ("Ctrl+P", "Print"),
        ("Ctrl+W / Ctrl+Tab", "Close tab / Next tab"),
        ("Ctrl+F, Enter, Shift+Enter", "Search, next and previous match"),
        ("Ctrl+Z / Ctrl+Y", "Undo / Redo"),
        ("Ctrl+C / Ctrl+A", "Copy selected text / Select page text"),
        ("Ctrl+Plus / Ctrl+Minus / Ctrl+0", "Zoom in / out / Fit width"),
        ("Ctrl+Wheel or pinch", "Zoom"),
        ("PageUp / PageDown / Home / End", "Move through pages"),
        ("Ctrl+D", "Document properties"),
        ("F11 / Esc", "Full screen / Leave full screen, cancel"),
        ("F1", "This list"),
    ];

    private async Task ShowShortcutsAsync()
    {
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < Shortcuts.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = Shortcuts[i].Keys, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var action = new TextBlock { Text = Shortcuts[i].Action, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(keys);
            grid.Children.Add(action);
        }
        await Dialogs.Create(Root.XamlRoot, "Keyboard shortcuts", new ScrollViewer { Content = grid }, "Close", null).ShowAsync();
    }

    private void DocCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null || sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<DocCommand>(tag, out var command)) return;
        CloseSettings();
        _active.View.Execute(command);
    }

    private async void OpenAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        var path = await Pickers.OpenFileAsync(".pdf");
        if (path is not null) await OpenPdfAsync(path);
    }

    private async void CloseTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_active is not null) await CloseTabAsync(_active);
    }

    private void NextTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_tabs.Count > 1 && _active is not null) Activate(_tabs[(_tabs.IndexOf(_active) + 1) % _tabs.Count]);
    }

    private void FullScreenAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_tabs.Count > 0 || IsFullScreen) ToggleFullScreen();
    }

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
        if (_closingConfirmed || !_tabs.Any(t => t.View.IsDirty)) return;
        args.Cancel = true;
        foreach (var tab in _tabs.ToList())
        {
            if (!await CloseTabAsync(tab)) return;
        }
        _closingConfirmed = true;
        Close();
    }
}
