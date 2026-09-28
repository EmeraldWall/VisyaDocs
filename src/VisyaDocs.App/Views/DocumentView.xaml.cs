using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VisyaDocs.App.Services;
using VisyaDocs.Core;
using Windows.System;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace VisyaDocs.App.Views;

public enum EditTool
{
    Select,
    EditText,
    AddText,
    Comment,
    Highlight,
    FillForm,
    PlaceSignature,
}

/// <summary>
/// One open document: page viewer with a floating tool rail, thumbnails and side pane. The
/// partial files split it into viewing (Viewer), editing (Editing), forms (Forms), signing (Sign),
/// printing (Print) and conversion (Convert).
/// </summary>
public sealed partial class DocumentView : UserControl, IDisposable
{
    private readonly PdfDocument _doc;
    private readonly DispatcherQueue _dispatcher;
    private string _name;
    private bool _disposed;
    private EditTool _tool = EditTool.Select;
    private CancellationTokenSource? _operation;
    private int _messageVersion;

    public DocumentView(PdfDocument doc, string name)
    {
        _doc = doc;
        _name = name;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();

        _doc.Changed += (_, _) => _dispatcher.TryEnqueue(OnDocumentChanged);
        Loaded += DocumentView_Loaded;
        ThumbList.ItemsSource = _thumbs;
        CommentsList.ItemsSource = _comments;
        Scroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(Scroller_PointerWheelChanged), true);
        Scroller.AddHandler(KeyDownEvent, new KeyEventHandler(Scroller_KeyDown), true);
        AddMainKeyboardZoomAccelerators();
        BuildColorMenu();
        UpdateUndoRedo();
        UpdateLayoutMenu();
        SetRailCollapsed(AppSettings.Current.RailCollapsed);
        PageArea.SizeChanged += (_, _) => PositionRail();
    }

    public event EventHandler? TitleChanged;

    public string Title => _doc.IsDirty ? $"{_name} *" : _name;
    public bool IsDirty => _doc.IsDirty;
    public string? FilePath => _doc.FilePath;

    public async Task<bool> SaveAsync(bool saveAs = false)
    {
        CommitEditor();
        string? path = _doc.FilePath;
        if (saveAs || path is null)
        {
            path = await Pickers.SaveFileAsync(Path.GetFileNameWithoutExtension(_name), "PDF document", ".pdf");
            if (path is null) return false;
        }
        try
        {
            await Task.Run(() => _doc.Save(path));
            _name = Path.GetFileName(path);
            AppSettings.Current.AddRecent(path);
            TitleChanged?.Invoke(this, EventArgs.Empty);
            ShowMessage("Saved", path, InfoBarSeverity.Success, autoHide: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfException)
        {
            ShowMessage("Could not save", e.Message, InfoBarSeverity.Error);
            return false;
        }
    }

    /// <summary>Re-applies page appearance after the app theme or dimming setting changes.</summary>
    public void OnThemeChanged()
    {
        foreach (var page in _pages) ApplyAppearance(page);
    }

    /// <summary>Full screen hides the title bar; the rail folds to its handle to keep the page clear.</summary>
    public void OnFullScreenChanged(bool fullScreen)
    {
        _fullScreen = fullScreen;
        FullScreenIcon.Icon = fullScreen ? "fullscreen-exit" : "fullscreen";
        SetRailCollapsed(fullScreen || AppSettings.Current.RailCollapsed);
    }

    public void Dispose()
    {
        if (_disposed) return;
        RememberPosition();
        _disposed = true;
        _operation?.Cancel();
        _searchCts?.Cancel();
        _doc.Dispose();
    }

    private async void DocumentView_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= DocumentView_Loaded;
        await BuildPagesAsync();
        if (_layout == ViewLayout.SinglePage) SetZoom(FitPageZoom(), ZoomKind.FitPage);
        else SetZoom(Math.Min(FitWidthZoom(), 1.5), ZoomKind.FitWidth);
        _layoutVersion = _doc.LayoutVersion;
        await OnOpenedAsync();
        if (_doc.HasForm)
        {
            FormTool.Visibility = Visibility.Visible;
            FormBar.IsOpen = true;
            SetTool(EditTool.FillForm);
        }
        await CheckScannedAsync();
    }

    private void OnDocumentChanged()
    {
        if (_disposed) return;
        _contentVersion++;
        TitleChanged?.Invoke(this, EventArgs.Empty);
        UpdateUndoRedo();
        ClearSelection();
        // Keep search results visible after an edit (for example after filling a form field).
        if (SearchPanel.Visibility == Visibility.Visible && _lastQuery.Length > 0) _ = RunSearchAsync(_lastQuery, navigate: false);
        else ClearSearchMarks();
        if (_doc.PageCount != _pages.Count || _doc.LayoutVersion != _layoutVersion)
        {
            // Pages were added, removed or rotated: measure them again.
            _layoutVersion = _doc.LayoutVersion;
            _ = RebuildAfterPageCountChangeAsync();
            return;
        }
        UpdateVisiblePages();
        RefreshThumbnails();
        if (SidePane.Visibility == Visibility.Visible && CommentsPanel.Visibility == Visibility.Visible) _ = RefreshCommentsAsync();
    }

    private int _layoutVersion;

    private async Task RebuildAfterPageCountChangeAsync()
    {
        int page = _currentPage;
        await BuildPagesAsync();
        SetZoom(_zoomKind switch { ZoomKind.FitWidth => FitWidthZoom(), ZoomKind.FitPage => FitPageZoom(), _ => _zoom }, _zoomKind);
        if (_thumbs.Count > 0) BuildThumbnails();
        GoToPage(Math.Min(page, _pages.Count - 1));
    }

    private void UpdateUndoRedo()
    {
        UndoButton.IsEnabled = _doc.CanUndo;
        RedoButton.IsEnabled = _doc.CanRedo;
    }

    // Modes and tools -------------------------------------------------------------------------

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<EditTool>(tag, out var tool))
        {
            if (tool is EditTool.EditText or EditTool.AddText && !Permitted(_permissions.CanModify, "changes"))
            {
                SetTool(_tool);
                return;
            }
            SetTool(tool == _tool && tool != EditTool.Select ? EditTool.Select : tool);
        }
    }

    private void SetTool(EditTool tool)
    {
        CommitEditor();
        _hoverPage?.ShowHover(null);
        if (_tool == EditTool.PlaceSignature && tool != EditTool.PlaceSignature) CancelSignaturePlacement();
        bool formChanged = (_tool == EditTool.FillForm) != (tool == EditTool.FillForm);
        _tool = tool;
        SelectTool.IsChecked = tool == EditTool.Select;
        EditTextTool.IsChecked = tool == EditTool.EditText;
        AddTextTool.IsChecked = tool == EditTool.AddText;
        CommentTool.IsChecked = tool == EditTool.Comment;
        HighlightTool.IsChecked = tool == EditTool.Highlight;
        FormTool.IsChecked = tool == EditTool.FillForm;
        SignTool.IsChecked = tool == EditTool.PlaceSignature;
        TextOptions.Visibility = tool == EditTool.AddText ? Visibility.Visible : Visibility.Collapsed;
        var cursor = tool switch
        {
            EditTool.AddText or EditTool.Comment or EditTool.PlaceSignature => InputSystemCursorShape.Cross,
            EditTool.EditText => InputSystemCursorShape.Hand,
            EditTool.FillForm => InputSystemCursorShape.Arrow,
            _ => InputSystemCursorShape.IBeam,
        };
        foreach (var page in _pages) page.SetCursor(cursor);
        if (tool != EditTool.Select && tool != EditTool.Highlight) ClearSelection();
        if (formChanged) RefreshFormOverlays();
    }

    // Rail, full screen and status pill -------------------------------------------------------

    private bool _fullScreen;
    private int _pillVersion;

    private void RailCollapse_Click(object sender, RoutedEventArgs e)
    {
        bool collapse = RailItems.Visibility == Visibility.Visible;
        SetRailCollapsed(collapse);
        if (!_fullScreen)
        {
            AppSettings.Current.RailCollapsed = collapse;
            AppSettings.Current.Save();
        }
    }

    private void SetRailCollapsed(bool collapsed)
    {
        RailItems.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        RailCollapseGlyph.Glyph = collapsed ? "\uE70D" : "\uE70E";
        ToolTipService.SetToolTip(RailCollapseButton, collapsed ? "Show tools" : "Hide tools");
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => App.MainWindow.ToggleFullScreen();

    /// <summary>Shows the page/zoom pill, then fades it out after a moment so the page stays clear.</summary>
    private void ShowStatusPill()
    {
        StatusPill.Opacity = 1;
        int version = ++_pillVersion;
        _ = Task.Delay(2500).ContinueWith(_ => _dispatcher.TryEnqueue(() =>
        {
            if (version == _pillVersion && PageBox.FocusState == FocusState.Unfocused) StatusPill.Opacity = 0.0;
        }));
    }

    private void StatusPill_PointerEntered(object sender, PointerRoutedEventArgs e) => ShowStatusPill();

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Bring the pill back when the pointer nears the bottom edge.
        if (e.GetCurrentPoint(Root).Position.Y > Root.ActualHeight - 90 && StatusPill.Opacity < 1) ShowStatusPill();
    }

    private async void Properties_Click(object sender, RoutedEventArgs e) => await ShowPropertiesAsync();

    private async void PropertiesAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ShowPropertiesAsync();
    }

    private async Task ShowPropertiesAsync()
    {
        try
        {
            var properties = await Task.Run(_doc.GetProperties);
            await PropertiesDialog.ShowAsync(XamlRoot, properties);
        }
        catch (PdfException ex)
        {
            ShowMessage("Could not read the properties", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => OpenSearch();

    private void OpenSearch()
    {
        SearchPanel.Visibility = Visibility.Visible;
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
    }

    private void SearchClose_Click(object sender, RoutedEventArgs e) => CloseSearch();

    private void CloseSearch()
    {
        SearchPanel.Visibility = Visibility.Collapsed;
        SearchBox.Text = string.Empty;
        ClearSearchMarks();
        Scroller.Focus(FocusState.Programmatic);
    }

    // Messages and long operations ------------------------------------------------------------

    private void ShowMessage(string title, string message, InfoBarSeverity severity, bool autoHide = false)
    {
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
        int version = ++_messageVersion;
        if (autoHide)
        {
            _ = Task.Delay(3500).ContinueWith(_ => _dispatcher.TryEnqueue(() =>
            {
                if (version == _messageVersion) MessageBar.IsOpen = false;
            }));
        }
    }

    /// <summary>Runs a cancellable operation with a progress bar; reports failures in the message bar.</summary>
    private async Task<bool> RunOperationAsync(string label, Func<IProgress<double>, CancellationToken, Task> work)
    {
        if (_operation is not null)
        {
            ShowMessage("Busy", "Wait for the current operation to finish or cancel it.", InfoBarSeverity.Warning, autoHide: true);
            return false;
        }
        CommitEditor();
        using var cts = new CancellationTokenSource();
        _operation = cts;
        ProgressText.Text = label;
        Progress.Value = 0;
        ProgressPanel.Visibility = Visibility.Visible;
        try
        {
            await work(new Progress<double>(v => Progress.Value = v), cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            ShowMessage("Cancelled", label, InfoBarSeverity.Informational, autoHide: true);
        }
        catch (Platform.OcrUnavailableException e)
        {
            ShowMessage("Text recognition is not available", e.Message, InfoBarSeverity.Warning);
        }
        catch (Exception e) when (!_disposed)
        {
            ShowMessage($"{label} failed", e.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _operation = null;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
        return false;
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();

    // Side pane -------------------------------------------------------------------------------

    private void ShowSidePane(string title, bool comments)
    {
        SidePaneTitle.Text = title;
        CommentsPanel.Visibility = comments ? Visibility.Visible : Visibility.Collapsed;
        TextPanel.Visibility = comments ? Visibility.Collapsed : Visibility.Visible;
        SidePane.Visibility = Visibility.Visible;
        CommentsToggle.IsChecked = comments;
    }

    private void CloseSidePane_Click(object sender, RoutedEventArgs e)
    {
        SidePane.Visibility = Visibility.Collapsed;
        CommentsToggle.IsChecked = false;
    }

    // Commands --------------------------------------------------------------------------------

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsync(saveAs: true);

    private async void Undo_Click(object sender, RoutedEventArgs e) => await UndoRedoAsync(undo: true);

    private async void Redo_Click(object sender, RoutedEventArgs e) => await UndoRedoAsync(undo: false);

    private async Task UndoRedoAsync(bool undo)
    {
        CancelEditor();
        try
        {
            await Task.Run(() => { if (undo) _doc.Undo(); else _doc.Redo(); });
        }
        catch (PdfException ex)
        {
            ShowMessage(undo ? "Could not undo" : "Could not redo", ex.Message, InfoBarSeverity.Error);
        }
    }

    // Keyboard --------------------------------------------------------------------------------

    private static bool TextInputHasFocus(XamlRoot? root) =>
        root is not null && FocusManager.GetFocusedElement(root) is TextBox or PasswordBox or RichEditBox;

    private async void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveAsync();
    }

    private async void SaveAsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveAsync(saveAs: true);
    }

    private void FindAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenSearch();
    }

    private async void UndoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TextInputHasFocus(XamlRoot)) return;
        args.Handled = true;
        await UndoRedoAsync(undo: true);
    }

    private async void RedoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TextInputHasFocus(XamlRoot)) return;
        args.Handled = true;
        await UndoRedoAsync(undo: false);
    }

    private void CopyAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TextInputHasFocus(XamlRoot)) return;
        args.Handled = CopySelection();
    }

    private void SelectAllAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TextInputHasFocus(XamlRoot) || _pages.Count == 0) return;
        args.Handled = true;
        SelectAllOnCurrentPage();
    }

    private void ZoomInAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        StepZoom(+1);
    }

    private void ZoomOutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        StepZoom(-1);
    }

    private void FitWidthAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetZoom(FitWidthZoom(), ZoomKind.FitWidth);
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_editor is not null) CancelEditor();
        else if (_tool == EditTool.PlaceSignature) SetTool(EditTool.Select);
        else if (_selection is not null) ClearSelection();
        else if (SearchPanel.Visibility == Visibility.Visible) CloseSearch();
        else if (_fullScreen) App.MainWindow.SetFullScreen(false);
        else args.Handled = false;
    }

    // Ctrl with the main keyboard's plus and minus keys (the XAML accelerators cover the number pad).
    private void AddMainKeyboardZoomAccelerators()
    {
        var zoomIn = new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = (VirtualKey)187 };
        zoomIn.Invoked += ZoomInAccelerator_Invoked;
        var zoomOut = new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = (VirtualKey)189 };
        zoomOut.Invoked += ZoomOutAccelerator_Invoked;
        Root.KeyboardAccelerators.Add(zoomIn);
        Root.KeyboardAccelerators.Add(zoomOut);
    }
}
