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
}

/// <summary>
/// One open document: toolbar, page viewer, thumbnails and side pane. The partial files split
/// it into viewing (DocumentView.Viewer), editing (DocumentView.Editing) and conversion
/// (DocumentView.Convert).
/// </summary>
public sealed partial class DocumentView : UserControl, IDisposable
{
    private readonly PdfDocument _doc;
    private readonly DispatcherQueue _dispatcher;
    private string _name;
    private bool _disposed;
    private EditTool _tool = EditTool.Select;
    private EditTool _lastEditTool = EditTool.EditText;
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
        AddMainKeyboardZoomAccelerators();
        BuildColorMenu();
        UpdateUndoRedo();
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _operation?.Cancel();
        _searchCts?.Cancel();
        _doc.Dispose();
    }

    private async void DocumentView_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= DocumentView_Loaded;
        await BuildPagesAsync();
        SetZoom(Math.Min(FitWidthZoom(), 1.5), ZoomKind.FitWidth);
        await CheckScannedAsync();
    }

    private void OnDocumentChanged()
    {
        if (_disposed) return;
        _contentVersion++;
        TitleChanged?.Invoke(this, EventArgs.Empty);
        UpdateUndoRedo();
        ClearSelection();
        ClearSearchMarks();
        if (_doc.PageCount != _pages.Count)
        {
            _ = RebuildAfterPageCountChangeAsync();
            return;
        }
        UpdateVisiblePages();
        RefreshThumbnails();
        if (SidePane.Visibility == Visibility.Visible && CommentsPanel.Visibility == Visibility.Visible) _ = RefreshCommentsAsync();
    }

    private async Task RebuildAfterPageCountChangeAsync()
    {
        await BuildPagesAsync();
        SetZoom(_zoom, _zoomKind);
        if (_thumbs.Count > 0) BuildThumbnails();
    }

    private void UpdateUndoRedo()
    {
        UndoButton.IsEnabled = _doc.CanUndo;
        RedoButton.IsEnabled = _doc.CanRedo;
    }

    // Modes and tools -------------------------------------------------------------------------

    private void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var item = sender.SelectedItem;
        EditTools.Visibility = item == EditModeItem ? Visibility.Visible : Visibility.Collapsed;
        ConvertTools.Visibility = item == ConvertModeItem ? Visibility.Visible : Visibility.Collapsed;
        SetTool(item == EditModeItem ? _lastEditTool : EditTool.Select);
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<EditTool>(tag, out var tool))
        {
            _lastEditTool = tool;
            SetTool(tool);
        }
    }

    private void SetTool(EditTool tool)
    {
        CommitEditor();
        _tool = tool;
        SelectTool.IsChecked = tool == EditTool.Select;
        EditTextTool.IsChecked = tool == EditTool.EditText;
        AddTextTool.IsChecked = tool == EditTool.AddText;
        CommentTool.IsChecked = tool == EditTool.Comment;
        HighlightTool.IsChecked = tool == EditTool.Highlight;
        var cursor = tool switch
        {
            EditTool.AddText or EditTool.Comment => InputSystemCursorShape.Cross,
            EditTool.EditText => InputSystemCursorShape.Hand,
            _ => InputSystemCursorShape.IBeam,
        };
        foreach (var page in _pages) page.SetCursor(cursor);
        if (tool != EditTool.Select && tool != EditTool.Highlight) ClearSelection();
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
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
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
        if (_editor is not null)
        {
            CancelEditor();
            args.Handled = true;
        }
        else if (_selection is not null)
        {
            ClearSelection();
            args.Handled = true;
        }
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
