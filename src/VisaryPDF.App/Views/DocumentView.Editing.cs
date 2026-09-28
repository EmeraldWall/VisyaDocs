using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VisaryPDF.App.Services;
using VisaryPDF.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace VisaryPDF.App.Views;

// Text selection, editing existing text, adding text, comments and highlights.
public sealed partial class DocumentView
{
    private static readonly (string Name, PdfColor Color)[] TextColors =
    [
        ("Black", new PdfColor(0, 0, 0)),
        ("Dark grey", new PdfColor(80, 80, 80)),
        ("Blue", new PdfColor(30, 90, 200)),
        ("Red", new PdfColor(200, 30, 30)),
        ("Green", new PdfColor(20, 130, 60)),
        ("Orange", new PdfColor(220, 110, 0)),
    ];

    private readonly ObservableCollection<CommentItem> _comments = [];
    private readonly Stopwatch _moveThrottle = Stopwatch.StartNew();
    private PdfColor _textColor = PdfColor.Black;

    private TextSelection? _selection;
    private PageView? _selectionPage;
    private int _selectionStart = -1;
    private bool _selecting;

    private TextBox? _editor;
    private Func<string, Task>? _editorCommit;

    private double NewTextSize => double.TryParse(FontSizeBox.SelectedItem as string, out double s) ? s : 12;

    // Pointer input ---------------------------------------------------------------------------

    private static PageView? PageOf(object sender) => (sender as FrameworkElement)?.Parent as PageView;

    private void Overlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (PageOf(sender) is not { } page) return;
        var point = e.GetCurrentPoint(page.Overlay);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (_editor is not null)
        {
            // First click outside an open editor just finishes it.
            CommitEditor();
            e.Handled = true;
            return;
        }
        Scroller.Focus(FocusState.Pointer);
        var pagePoint = page.ToPage(point.Position);

        // Links work while reading (Select tool): a click follows them instead of starting a selection.
        if (_tool == EditTool.Select && page.LinkAt(pagePoint.X, pagePoint.Y) is { } link)
        {
            e.Handled = true;
            _ = FollowLinkAsync(link);
            return;
        }

        switch (_tool)
        {
            case EditTool.Select:
            case EditTool.Highlight:
                ClearSelection();
                int index = SafeCharIndex(page, pagePoint);
                if (index >= 0)
                {
                    _selectionPage = page;
                    _selectionStart = index;
                    _selecting = true;
                    page.Overlay.CapturePointer(e.Pointer);
                }
                break;
            case EditTool.EditText:
                BeginEditText(page, pagePoint);
                break;
            case EditTool.AddText:
                BeginAddText(page, point.Position);
                break;
            case EditTool.Comment:
                BeginComment(page, point.Position, pagePoint);
                break;
            case EditTool.PlaceSignature:
                BeginSignaturePlacement(page, point.Position);
                break;
        }
        e.Handled = true;
    }

    private void Overlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting)
        {
            if (PageOf(sender) is { } hovered) UpdateHover(hovered, e);
            return;
        }
        if (PageOf(sender) is not { } page || page != _selectionPage) return;
        if (_moveThrottle.ElapsedMilliseconds < 35) return;
        _moveThrottle.Restart();
        var pagePoint = page.ToPage(e.GetCurrentPoint(page.Overlay).Position);
        int index = SafeCharIndex(page, pagePoint, tolerance: 12);
        if (index < 0) return;
        _selection = _doc.GetSelection(page.Index, _selectionStart, index);
        page.ShowSelection(_selection.Rects);
    }

    private async void Overlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        if (_tool == EditTool.Highlight && _selection is { Count: > 0 } selection)
        {
            ClearSelection();
            await EditAsync("Could not highlight", () => _doc.AddHighlight(selection.PageIndex, selection.Rects, author: AppSettings.Current.Author));
        }
    }

    private void Overlay_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _selecting = false;

    private readonly Stopwatch _hoverThrottle = Stopwatch.StartNew();
    private PageView? _hoverPage;

    /// <summary>
    /// Pointer feedback without clicking: a hand over links while reading, and in Edit text mode an
    /// outline around the text run that a click would edit.
    /// </summary>
    private void UpdateHover(PageView page, PointerRoutedEventArgs e)
    {
        if (_hoverThrottle.ElapsedMilliseconds < 60 || _editor is not null) return;
        _hoverThrottle.Restart();
        var p = page.ToPage(e.GetCurrentPoint(page.Overlay).Position);
        if (_tool == EditTool.Select)
        {
            page.SetCursor(page.LinkAt(p.X, p.Y) is null ? InputSystemCursorShape.IBeam : InputSystemCursorShape.Hand);
        }
        else if (_tool == EditTool.EditText)
        {
            if (_hoverPage is not null && _hoverPage != page) _hoverPage.ShowHover(null);
            _hoverPage = page;
            TextObjectInfo? info = null;
            try
            {
                info = _doc.FindTextObjectAt(page.Index, p.X, p.Y);
            }
            catch (PdfException)
            {
            }
            page.ShowHover(info?.Bounds);
        }
    }

    private void Overlay_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (PageOf(sender) is { } page) page.ShowHover(null);
    }

    private void Overlay_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_tool != EditTool.Select || PageOf(sender) is not { } page) return;
        int index = SafeCharIndex(page, page.ToPage(e.GetPosition(page.Overlay)));
        if (index < 0) return;
        ClearSelection();
        _selection = _doc.GetWordAt(page.Index, index);
        _selectionPage = page;
        page.ShowSelection(_selection.Rects);
        e.Handled = true;
    }

    private void Overlay_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (PageOf(sender) is not { } page || _selection is not { Count: > 0 } selection || _selectionPage != page) return;
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Copy", "", (_, _) => CopySelection()));
        menu.Items.Add(MenuItem("Highlight", "", async (_, _) =>
        {
            ClearSelection();
            await EditAsync("Could not highlight", () => _doc.AddHighlight(selection.PageIndex, selection.Rects, author: AppSettings.Current.Author));
        }));
        menu.Items.Add(MenuItem("Highlight with comment", "", async (_, _) =>
        {
            var text = await Dialogs.AskTextAsync(XamlRoot, "Comment on highlight", string.Empty, "Add");
            if (text is null) return;
            ClearSelection();
            await EditAsync("Could not highlight", () => _doc.AddHighlight(selection.PageIndex, selection.Rects, text, AppSettings.Current.Author));
        }));
        menu.ShowAt(page.Overlay, e.GetPosition(page.Overlay));
        e.Handled = true;
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, RoutedEventHandler click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += click;
        return item;
    }

    private int SafeCharIndex(PageView page, Point pagePoint, double tolerance = 4)
    {
        try
        {
            return _doc.GetCharIndexAt(page.Index, pagePoint.X, pagePoint.Y, tolerance);
        }
        catch (PdfException)
        {
            return -1;
        }
    }

    // Selection -------------------------------------------------------------------------------

    private void ClearSelection()
    {
        _selectionPage?.ClearSelection();
        _selection = null;
        _selectionPage = null;
        _selecting = false;
    }

    private bool CopySelection()
    {
        if (_selection is not { Count: > 0 } selection) return false;
        if (!_permissions.CanCopy)
        {
            ShowMessage("Copying is not allowed", "The author of this PDF does not allow copying its text.", InfoBarSeverity.Warning, autoHide: true);
            return true;
        }
        var package = new DataPackage();
        package.SetText(selection.Text);
        Clipboard.SetContent(package);
        ShowMessage("Copied", $"{selection.Text.Length} characters copied to the clipboard.", InfoBarSeverity.Success, autoHide: true);
        return true;
    }

    private void SelectAllOnCurrentPage()
    {
        ClearSelection();
        var page = _pages[Math.Clamp(_currentPage, 0, _pages.Count - 1)];
        _selection = _doc.SelectAll(page.Index);
        _selectionPage = page;
        page.ShowSelection(_selection.Rects);
    }

    // Inline editors ---------------------------------------------------------------------------

    private void BeginEditText(PageView page, Point pagePoint)
    {
        TextObjectInfo? info;
        try
        {
            info = _doc.FindTextObjectAt(page.Index, pagePoint.X, pagePoint.Y);
        }
        catch (PdfException)
        {
            info = null;
        }
        if (info is null)
        {
            ShowMessage("No text here", "Click directly on existing text to change it. To place new text, use Add text.",
                InfoBarSeverity.Informational, autoHide: true);
            return;
        }
        if (_doc.IsLikelyScanned(page.Index))
        {
            ShowMessage("Scanned page", "This text comes from OCR and is invisible. Use Add text to write visible text on a scanned page.",
                InfoBarSeverity.Informational, autoHide: true);
            return;
        }

        var rect = page.ToDip(info.Bounds);
        var box = CreateEditor(info.Text, info.FontSize * page.DipPerPoint, multiline: false, new SolidColorBrush(Colors.Black));
        box.MinWidth = rect.Width + 32;
        Canvas.SetLeft(box, rect.X - 4);
        Canvas.SetTop(box, rect.Y - 4);
        OpenEditor(page, box, async text =>
        {
            if (text == info.Text) return;
            await EditAsync("Could not edit the text", () => _doc.ReplaceText(page.Index, info.ObjectIndex, text));
        });
        box.SelectAll();
    }

    private void BeginAddText(PageView page, Point dip)
    {
        double size = NewTextSize;
        var color = _textColor;
        var box = CreateEditor(string.Empty, size * page.DipPerPoint, multiline: true,
            new SolidColorBrush(ColorHelper.FromArgb(255, color.R, color.G, color.B)));
        box.MinWidth = 160;
        box.PlaceholderText = "Type text, Ctrl+Enter to place";
        Canvas.SetLeft(box, dip.X - 4);
        Canvas.SetTop(box, dip.Y - 2);
        OpenEditor(page, box, async text =>
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // Put the PDF baseline where the editor showed the first line (top padding + ascent).
            var baseline = page.ToPage(new Point(dip.X, dip.Y + 1 + size * page.DipPerPoint * 1.02));
            await EditAsync("Could not add the text", () =>
                _doc.AddText(page.Index, baseline.X, baseline.Y, text.TrimEnd(), new TextStyle(size, color)));
        });
    }

    private TextBox CreateEditor(string text, double fontSize, bool multiline, Brush foreground)
    {
        var box = new TextBox
        {
            Text = text,
            FontSize = Math.Max(8, fontSize),
            AcceptsReturn = multiline,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(3, 1, 3, 1),
            MinHeight = 0,
            // Editors sit on the (white) page, so they always use light styling.
            RequestedTheme = ElementTheme.Light,
            Background = new SolidColorBrush(ColorHelper.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            Foreground = foreground,
            FontFamily = new FontFamily("Arial"),
        };
        box.KeyDown += Editor_KeyDown;
        box.LostFocus += (_, _) => { if (_editor == box) CommitEditor(); };
        return box;
    }

    private void OpenEditor(PageView page, TextBox box, Func<string, Task> commit)
    {
        _editor = box;
        _editorCommit = commit;
        page.Overlay.Children.Add(box);
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var box = (TextBox)sender;
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Enter && (!box.AcceptsReturn || ctrl))
        {
            CommitEditor();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            CancelEditor();
            e.Handled = true;
        }
    }

    private async void CommitEditor()
    {
        if (_editor is not { } box) return;
        var commit = _editorCommit;
        CloseEditor();
        if (commit is not null) await commit(box.Text);
    }

    private void CancelEditor()
    {
        if (_editor is null) return;
        CloseEditor();
    }

    private void CloseEditor()
    {
        var box = _editor;
        _editor = null;
        _editorCommit = null;
        if (box?.Parent is Panel panel) panel.Children.Remove(box);
        Scroller.Focus(FocusState.Programmatic);
    }

    /// <summary>Runs a document edit off the UI thread; the Changed event refreshes the view.</summary>
    private async Task EditAsync(string failure, Action edit)
    {
        try
        {
            await Task.Run(edit);
        }
        catch (Exception e) when (e is PdfException or ArgumentException)
        {
            ShowMessage(failure, e.Message, InfoBarSeverity.Error);
        }
    }

    private void BuildColorMenu()
    {
        foreach (var (name, color) in TextColors)
        {
            var item = new MenuFlyoutItem
            {
                Text = name,
                Icon = new FontIcon { Glyph = "", Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, color.R, color.G, color.B)) },
            };
            item.Click += (_, _) =>
            {
                _textColor = color;
                ColorSwatch.Fill = new SolidColorBrush(ColorHelper.FromArgb(255, color.R, color.G, color.B));
            };
            ColorMenu.Items.Add(item);
        }
    }

    // Comments --------------------------------------------------------------------------------

    private void BeginComment(PageView page, Point dip, Point pagePoint)
    {
        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = 260, Height = 110, PlaceholderText = "Write a comment" };
        var add = new Button { Content = "Add comment", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(box);
        panel.Children.Add(add);
        var flyout = new Flyout { Content = panel };
        add.Click += async (_, _) =>
        {
            flyout.Hide();
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            string text = box.Text.Trim();
            await EditAsync("Could not add the comment", () => _doc.AddNote(page.Index, pagePoint.X, pagePoint.Y, text, AppSettings.Current.Author));
        };
        flyout.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        flyout.ShowAt(page.Overlay, new FlyoutShowOptions { Position = dip });
    }

    /// <summary>Clickable areas over sticky notes that open the comment for reading, editing or deleting.</summary>
    private void ShowNoteHotspots(PageView page, IReadOnlyList<AnnotationInfo> annotations, int version)
    {
        page.ClearHotspots();
        page.HotspotVersion = version;
        foreach (var note in annotations.Where(a => a.Kind == AnnotationKind.Note))
        {
            var spot = new Border { Background = new SolidColorBrush(Colors.Transparent) };
            ToolTipService.SetToolTip(spot, note.Contents.Length > 0 ? note.Contents : "Comment");
            spot.Tapped += (_, e) =>
            {
                e.Handled = true;
                ShowNoteFlyout(page, spot, note);
            };
            spot.PointerPressed += (_, e) => e.Handled = true;
            page.AddHotspot(spot, note.Bounds);
        }
    }

    private void ShowNoteFlyout(PageView page, FrameworkElement anchor, AnnotationInfo note)
    {
        var box = new TextBox { Text = note.Contents, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = 260, Height = 110 };
        var save = new Button { Content = "Save", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var delete = new Button { Content = "Delete" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(delete);
        buttons.Children.Add(save);
        var panel = new StackPanel { Spacing = 8 };
        if (note.Author.Length > 0) panel.Children.Add(new TextBlock { Text = note.Author, Opacity = 0.7, FontSize = 12 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var flyout = new Flyout { Content = panel };
        save.Click += async (_, _) =>
        {
            flyout.Hide();
            string text = box.Text;
            if (text != note.Contents)
                await EditAsync("Could not update the comment", () => _doc.UpdateAnnotationText(page.Index, note.Index, text));
        };
        delete.Click += async (_, _) =>
        {
            flyout.Hide();
            await EditAsync("Could not delete the comment", () => _doc.RemoveAnnotation(page.Index, note.Index));
        };
        flyout.ShowAt(anchor);
    }

    private async void CommentsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (CommentsToggle.IsChecked == true)
        {
            ShowSidePane("Comments", comments: true);
            await RefreshCommentsAsync();
        }
        else
        {
            CloseSidePane();
        }
    }

    private async Task RefreshCommentsAsync()
    {
        try
        {
            var all = await Task.Run(() => Enumerable.Range(0, _doc.PageCount).SelectMany(_doc.GetAnnotations).ToList());
            _comments.Clear();
            foreach (var a in all) _comments.Add(new CommentItem(a));
            NoCommentsText.Visibility = _comments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception e) when (e is PdfException or ObjectDisposedException)
        {
            if (!_disposed) ShowMessage("Could not read comments", e.Message, InfoBarSeverity.Warning);
        }
    }

    private void CommentsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CommentItem item) GoToPage(item.Info.PageIndex, item.Info.Bounds);
    }

    private async void CommentEdit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        var text = await Dialogs.AskTextAsync(XamlRoot, "Edit comment", item.Info.Contents, "Save");
        if (text is null || text == item.Info.Contents) return;
        await EditAsync("Could not update the comment", () => _doc.UpdateAnnotationText(item.Info.PageIndex, item.Info.Index, text));
    }

    private async void CommentDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        await EditAsync("Could not delete the comment", () => _doc.RemoveAnnotation(item.Info.PageIndex, item.Info.Index));
    }
}
