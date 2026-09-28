using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VisyaDocs.App.Services;
using VisyaDocs.Core;
using Windows.System;
using Windows.UI.Core;

namespace VisyaDocs.App.Views;

public enum ZoomKind
{
    Custom,
    FitWidth,
    FitPage,
}

// Page layouts, lazy rendering, zoom (including pinch), navigation, thumbnails and search.
public sealed partial class DocumentView
{
    private const double PtToDip = 96.0 / 72.0;
    private const double PagePadding = 24, PageSpacing = 14, PageGap = 12;
    private const double MaxBitmapPixels = 24_000_000;
    private static readonly double[] ZoomSteps = [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6];

    private readonly List<PageView> _pages = [];
    private readonly HashSet<int> _renderQueue = [];
    private readonly ObservableCollection<ThumbnailItem> _thumbs = [];
    private readonly Queue<ThumbnailItem> _thumbQueue = new();
    private readonly Stopwatch _flipThrottle = Stopwatch.StartNew();
    private double[] _tops = [];
    private double _zoom = 1;
    private ZoomKind _zoomKind = ZoomKind.FitWidth;
    private ViewLayout _layout = AppSettings.Current.Layout;
    private int _currentPage;
    private int _contentVersion;
    private bool _rendering, _renderingThumbs;

    private IReadOnlyList<SearchHit> _hits = [];
    private int _hitIndex = -1;
    private string _lastQuery = string.Empty;
    private CancellationTokenSource? _searchCts;

    private double RasterScale => XamlRoot?.RasterizationScale ?? 1;
    private double CurrentStamp => _contentVersion * 1e4 + Math.Round(_zoom * RasterScale, 4);
    private bool IsSinglePage => _layout == ViewLayout.SinglePage;

    // Pages -----------------------------------------------------------------------------------

    private async Task BuildPagesAsync()
    {
        int count = _doc.PageCount;
        var geometries = await Task.Run(() => Enumerable.Range(0, count).Select(_doc.GetGeometry).ToArray());
        if (_disposed) return;

        PagesCanvas.Children.Clear();
        _pages.Clear();
        _renderQueue.Clear();
        for (int i = 0; i < count; i++)
        {
            var page = new PageView(i, geometries[i]);
            page.Overlay.PointerPressed += Overlay_PointerPressed;
            page.Overlay.PointerMoved += Overlay_PointerMoved;
            page.Overlay.PointerReleased += Overlay_PointerReleased;
            page.Overlay.PointerCaptureLost += Overlay_PointerCaptureLost;
            page.Overlay.DoubleTapped += Overlay_DoubleTapped;
            page.Overlay.RightTapped += Overlay_RightTapped;
            page.Overlay.PointerExited += Overlay_PointerExited;
            page.SetScale(_zoom * PtToDip);
            ApplyAppearance(page);
            _pages.Add(page);
            PagesCanvas.Children.Add(page);
        }
        _currentPage = Math.Clamp(_currentPage, 0, Math.Max(0, count - 1));
        SetTool(_tool);
        PageCountText.Text = $"/ {count}";
        LayoutPages();
    }

    private void ApplyAppearance(PageView page)
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        page.ApplyAppearance(dark, AppSettings.Current.DimPagesInDark,
            new SolidColorBrush(dark ? ColorHelper.FromArgb(255, 51, 51, 51) : ColorHelper.FromArgb(255, 200, 200, 200)));
    }

    // Layouts ---------------------------------------------------------------------------------

    /// <summary>Groups pages into rows: one page per row, or pairs side by side (optionally with the cover alone).</summary>
    private List<int[]> BuildRows()
    {
        var rows = new List<int[]>();
        int n = _pages.Count;
        switch (_layout)
        {
            case ViewLayout.TwoPages:
                for (int i = 0; i < n; i += 2) rows.Add(i + 1 < n ? [i, i + 1] : [i]);
                break;
            case ViewLayout.TwoPagesCover:
                if (n > 0) rows.Add([0]);
                for (int i = 1; i < n; i += 2) rows.Add(i + 1 < n ? [i, i + 1] : [i]);
                break;
            default:
                for (int i = 0; i < n; i++) rows.Add([i]);
                break;
        }
        return rows;
    }

    /// <summary>Positions every page on the canvas for the current layout and zoom.</summary>
    private void LayoutPages()
    {
        if (_pages.Count == 0) return;
        var rows = BuildRows();
        double viewport = Math.Max(1, Scroller.ViewportWidth / Math.Max(0.01, Scroller.ZoomFactor));
        double widest = rows.Max(r => r.Sum(i => _pages[i].Width) + PageGap * (r.Length - 1));
        // Pages are centered in the space the docked tool rail leaves free.
        double left = LeftInset, right = RightInset;
        double canvasWidth = Math.Max(viewport, widest + 2 * PagePadding + left + right);
        _tops = new double[_pages.Count];

        double y = PagePadding, contentHeight = 0;
        foreach (var row in rows)
        {
            double rowWidth = row.Sum(i => _pages[i].Width) + PageGap * (row.Length - 1);
            double rowHeight = row.Max(i => _pages[i].Height);
            double x = left + (canvasWidth - left - right - rowWidth) / 2;
            double top = IsSinglePage ? PagePadding : y;
            foreach (int i in row)
            {
                var page = _pages[i];
                Canvas.SetLeft(page, x);
                Canvas.SetTop(page, top + (rowHeight - page.Height) / 2);
                _tops[i] = top + (rowHeight - page.Height) / 2;
                page.Visibility = !IsSinglePage || i == _currentPage ? Visibility.Visible : Visibility.Collapsed;
                x += page.Width + PageGap;
            }
            contentHeight = Math.Max(contentHeight, top + rowHeight + PagePadding);
            y += rowHeight + PageSpacing;
        }
        PagesCanvas.Width = canvasWidth;
        PagesCanvas.Height = contentHeight;
    }

    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<ViewLayout>(tag, out var layout)) return;
        _layout = layout;
        AppSettings.Current.Layout = layout;
        AppSettings.Current.Save();
        UpdateLayoutMenu();
        if (layout == ViewLayout.SinglePage) SetZoom(FitPageZoom(), ZoomKind.FitPage);
        else if (_zoomKind == ZoomKind.FitPage) SetZoom(FitPageZoom(), ZoomKind.FitPage);
        else SetZoom(FitWidthZoom(), ZoomKind.FitWidth);
        GoToPage(_currentPage);
    }

    private void UpdateLayoutMenu()
    {
        LayoutContinuousItem.IsChecked = _layout == ViewLayout.Continuous;
        LayoutTwoItem.IsChecked = _layout == ViewLayout.TwoPages;
        LayoutCoverItem.IsChecked = _layout == ViewLayout.TwoPagesCover;
        LayoutSingleItem.IsChecked = _layout == ViewLayout.SinglePage;
        string icon = _layout switch
        {
            ViewLayout.TwoPages or ViewLayout.TwoPagesCover => "view-two-page",
            ViewLayout.SinglePage => "view-single",
            _ => "view-continuous",
        };
        ViewButtonIcon.Icon = icon;
    }

    // Zoom ------------------------------------------------------------------------------------

    private double FitWidthZoom()
    {
        if (_pages.Count == 0) return 1;
        var rows = BuildRows();
        double widestPt = rows.Max(r => r.Sum(i => _pages[i].Geometry.ViewWidth));
        int maxGaps = rows.Max(r => r.Length - 1);
        double available = Math.Max(100, Scroller.ViewportWidth - 2 * PagePadding - 16 - PageGap * maxGaps - LeftInset - RightInset);
        return available / (widestPt * PtToDip);
    }

    private double FitPageZoom()
    {
        if (_pages.Count == 0) return 1;
        var row = BuildRows().FirstOrDefault(r => r.Contains(_currentPage)) ?? [0];
        double tallestPt = row.Max(i => _pages[i].Geometry.ViewHeight);
        double available = Math.Max(100, Scroller.ViewportHeight - 2 * PagePadding);
        return Math.Min(FitWidthZoom(), available / (tallestPt * PtToDip));
    }

    /// <summary>A spot on a page (relative position) used to keep the view steady across zoom changes.</summary>
    private readonly record struct ZoomAnchor(int Page, double U, double V, double ScreenX, double ScreenY);

    /// <summary>Finds the page point under a viewport position (default: the viewport center).</summary>
    private ZoomAnchor? CaptureAnchor(double screenX, double screenY)
    {
        if (_pages.Count == 0 || _tops.Length != _pages.Count) return null;
        var p = Scroller.TransformToVisual(PagesCanvas).TransformPoint(new Windows.Foundation.Point(screenX, screenY));
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            if (page.Visibility != Visibility.Visible) continue;
            double l = Canvas.GetLeft(page), t = Canvas.GetTop(page);
            double dx = Math.Max(0, Math.Max(l - p.X, p.X - (l + page.Width)));
            double dy = Math.Max(0, Math.Max(t - p.Y, p.Y - (t + page.Height)));
            double d = dx * dx + dy * dy;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
                if (d == 0) break;
            }
        }
        if (best < 0) return null;
        var hit = _pages[best];
        return new ZoomAnchor(best,
            (p.X - Canvas.GetLeft(hit)) / Math.Max(1, hit.Width),
            (p.Y - Canvas.GetTop(hit)) / Math.Max(1, hit.Height),
            screenX, screenY);
    }

    /// <summary>Scrolls so the anchored page point is back under the same viewport position, at zoom factor 1.</summary>
    private void RestoreAnchor(ZoomAnchor? anchor)
    {
        if (anchor is not { } a || a.Page >= _pages.Count)
        {
            Scroller.ChangeView(null, null, 1f, true);
            return;
        }
        var page = _pages[a.Page];
        double x = Canvas.GetLeft(page) + a.U * page.Width - a.ScreenX;
        double y = Canvas.GetTop(page) + a.V * page.Height - a.ScreenY;
        Scroller.ChangeView(Math.Max(0, x), Math.Max(0, y), 1f, true);
    }

    /// <summary>Applies a new zoom and relayouts, keeping the page point under the viewport center steady.</summary>
    private void SetZoom(double zoom, ZoomKind kind = ZoomKind.Custom) =>
        ApplyZoom(zoom, kind, CaptureAnchor(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2));

    private void ApplyZoom(double zoom, ZoomKind kind, ZoomAnchor? anchor)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        CommitEditor();
        _zoom = zoom;
        _zoomKind = kind;
        foreach (var page in _pages) page.SetScale(zoom * PtToDip);
        LayoutPages();
        ZoomText.Text = $"{zoom * 100:0}%";
        PagesCanvas.UpdateLayout();
        if (IsSinglePage) Scroller.ChangeView(null, 0, 1f, true);
        else RestoreAnchor(anchor);
        UpdateZoomLimits();
        ShowStatusPill();
        UpdateVisiblePages();
        if (_tool == EditTool.FillForm) RefreshFormOverlays();
    }

    private const double MinZoom = 0.1, MaxZoom = 8;

    /// <summary>
    /// The ScrollViewer's own zoom (pinch, Ctrl+wheel) is relative to the current page zoom; keep its
    /// limits matched to the absolute limits so a gesture never overshoots and snaps back.
    /// </summary>
    private void UpdateZoomLimits()
    {
        Scroller.MinZoomFactor = (float)Math.Clamp(MinZoom / _zoom, 0.1, 1);
        Scroller.MaxZoomFactor = (float)Math.Clamp(MaxZoom / _zoom, 1, 10);
    }

    private void StepZoom(int direction)
    {
        double next = direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > _zoom + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(z => z < _zoom - 0.001, ZoomSteps[0]);
        SetZoom(next);
    }

    /// <summary>
    /// Pinch (touch or touchpad) and Ctrl+wheel zoom the ScrollViewer smoothly, which only stretches
    /// the bitmaps. When the gesture ends, the factor is folded into the page zoom and the pages are
    /// re-rendered sharp, with the page point at the viewport center kept exactly in place.
    /// </summary>
    private void FoldZoomFactor()
    {
        float factor = Scroller.ZoomFactor;
        var anchor = CaptureAnchor(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        ApplyZoom(_zoom * factor, ZoomKind.Custom, anchor);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => StepZoom(+1);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => StepZoom(-1);

    private void FitWidth_Click(object sender, RoutedEventArgs e) => SetZoom(FitWidthZoom(), ZoomKind.FitWidth);

    private void FitPage_Click(object sender, RoutedEventArgs e) => SetZoom(FitPageZoom(), ZoomKind.FitPage);

    private void ZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
            SetZoom(zoom);
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pages.Count == 0) return;
        if (_zoomKind == ZoomKind.FitWidth) SetZoom(FitWidthZoom(), ZoomKind.FitWidth);
        else if (_zoomKind == ZoomKind.FitPage) SetZoom(FitPageZoom(), ZoomKind.FitPage);
        else
        {
            LayoutPages();
            UpdateVisiblePages();
        }
    }

    // Scrolling, single page flipping and visible pages ---------------------------------------

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate && Math.Abs(Scroller.ZoomFactor - 1) > 0.005)
        {
            FoldZoomFactor();
            return;
        }
        UpdateVisiblePages();
        if (!e.IsIntermediate) ShowStatusPill();
    }

    private void Scroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!IsSinglePage) return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if (ctrl.HasFlag(CoreVirtualKeyStates.Down) || _flipThrottle.ElapsedMilliseconds < 300) return;
        int delta = e.GetCurrentPoint(Scroller).Properties.MouseWheelDelta;
        // Flip once the page is scrolled to its edge (or fits entirely).
        if (delta < 0 && Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1) FlipPage(+1);
        else if (delta > 0 && Scroller.VerticalOffset <= 1) FlipPage(-1);
    }

    private void Scroller_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Keys typed into a form field, an editor or a control on the page belong to that control.
        if (e.OriginalSource is TextBox or ComboBox or ComboBoxItem or Button or PasswordBox) return;
        switch (e.Key)
        {
            case VirtualKey.Home:
                GoToPage(0);
                break;
            case VirtualKey.End:
                GoToPage(_pages.Count - 1);
                break;
            case VirtualKey.PageDown or VirtualKey.Right or VirtualKey.Down when IsSinglePage && (e.Key == VirtualKey.PageDown || Scroller.ScrollableWidth < 1):
                FlipPage(+1);
                break;
            case VirtualKey.PageUp or VirtualKey.Left or VirtualKey.Up when IsSinglePage && (e.Key == VirtualKey.PageUp || Scroller.ScrollableWidth < 1):
                FlipPage(-1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void FlipPage(int direction)
    {
        _flipThrottle.Restart();
        GoToPage(_currentPage + direction);
    }

    /// <summary>
    /// Queues rendering for pages in or near the viewport and drops bitmaps of pages far away,
    /// so memory stays flat no matter how long the document is.
    /// </summary>
    private void UpdateVisiblePages()
    {
        if (_pages.Count == 0 || _tops.Length != _pages.Count) return;
        double stamp = CurrentStamp;
        if (IsSinglePage)
        {
            for (int i = 0; i < _pages.Count; i++)
            {
                bool near = Math.Abs(i - _currentPage) <= 1;
                if (near && _pages[i].RenderedStamp != stamp) _renderQueue.Add(i);
                else if (!near && _pages[i].RenderedStamp != 0) _pages[i].ClearBitmap();
            }
        }
        else
        {
            double factor = Math.Max(0.01, Scroller.ZoomFactor);
            double top = Scroller.VerticalOffset / factor, height = Math.Max(1, Scroller.ViewportHeight / factor);
            double keepFrom = top - height, keepTo = top + 2 * height;
            double dropFrom = top - 4 * height, dropTo = top + 5 * height;
            int current = -1;
            for (int i = 0; i < _pages.Count; i++)
            {
                var page = _pages[i];
                double pTop = _tops[i], pBottom = pTop + page.Height;
                if (current < 0 && pBottom > top + height * 0.3) current = i;
                if (pBottom >= keepFrom && pTop <= keepTo)
                {
                    if (page.RenderedStamp != stamp) _renderQueue.Add(i);
                }
                else if ((pBottom < dropFrom || pTop > dropTo) && page.RenderedStamp != 0)
                {
                    page.ClearBitmap();
                }
            }
            _currentPage = Math.Max(0, current);
        }
        if (_currentPage != _reportedPage)
        {
            _reportedPage = _currentPage;
            SyncThumbnail();
        }

        if (PageBox.FocusState == FocusState.Unfocused) PageBox.Text = (_currentPage + 1).ToString(CultureInfo.CurrentCulture);
        _ = RenderLoopAsync();
    }

    private async Task RenderLoopAsync()
    {
        if (_rendering) return;
        _rendering = true;
        try
        {
            while (_renderQueue.Count > 0 && !_disposed)
            {
                int index = _renderQueue.MinBy(i => Math.Abs(i - _currentPage));
                _renderQueue.Remove(index);
                if (index >= _pages.Count) continue;
                var page = _pages[index];
                double stamp = CurrentStamp;
                if (page.RenderedStamp == stamp) continue;

                double scale = RasterScale;
                double w = page.Width * scale, h = page.Height * scale;
                if (w * h > MaxBitmapPixels)
                {
                    double f = Math.Sqrt(MaxBitmapPixels / (w * h));
                    w *= f;
                    h *= f;
                }
                int pw = Math.Max(1, (int)w), ph = Math.Max(1, (int)h);
                int version = _contentVersion;
                bool needOverlays = page.HotspotVersion != version;
                bool wantForm = _doc.HasForm;

                var (pixels, notes, fields, links) = await Task.Run(() => (
                    _doc.RenderPage(index, pw, ph),
                    needOverlays ? _doc.GetAnnotations(index) : null,
                    needOverlays && wantForm ? _doc.GetFormFields(index) : null,
                    needOverlays ? _doc.GetLinks(index) : null));

                if (_disposed || index >= _pages.Count || _pages[index] != page) continue;
                var bitmap = new WriteableBitmap(pw, ph);
                pixels.CopyTo(bitmap.PixelBuffer);
                bitmap.Invalidate();
                page.SetBitmap(bitmap, stamp);
                if (notes is not null && version == _contentVersion)
                {
                    ShowNoteHotspots(page, notes, version);
                    page.Links = links ?? [];
                    page.FormFields = fields ?? [];
                    ShowFormOverlays(page);
                }
            }
        }
        catch (Exception e) when (e is PdfException or ObjectDisposedException or ArgumentException)
        {
            if (!_disposed) ShowMessage("Could not display a page", e.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            _rendering = false;
        }
    }

    // Navigation ------------------------------------------------------------------------------

    private void GoToPage(int index, PdfRect? target = null)
    {
        if (_pages.Count == 0) return;
        index = Math.Clamp(index, 0, _pages.Count - 1);
        if (IsSinglePage)
        {
            _currentPage = index;
            LayoutPages();
            Scroller.ChangeView(null, 0, null, true);
            UpdateVisiblePages();
            ShowStatusPill();
            return;
        }
        double y = _tops[index] - 8;
        if (target is { } rect && rect.Width > 0)
        {
            var dip = _pages[index].ToDip(rect);
            y = _tops[index] + dip.Y - Scroller.ViewportHeight / 3;
        }
        Scroller.ChangeView(null, Math.Max(0, y * Scroller.ZoomFactor), null, true);
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e) => GoToPage(_currentPage - 1);

    private void NextPage_Click(object sender, RoutedEventArgs e) => GoToPage(_currentPage + 1);

    private void PageBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        if (int.TryParse(PageBox.Text, out int page)) GoToPage(page - 1);
        Scroller.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    // Thumbnails ------------------------------------------------------------------------------

    private void ThumbsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = ThumbsToggle.IsChecked == true;
        if (show && _thumbs.Count != _pages.Count) BuildThumbnails();
        LeftPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) SyncThumbnail();
    }

    private int _reportedPage = -1;

    /// <summary>Selects and reveals the thumbnail of the page being read.</summary>
    private void SyncThumbnail()
    {
        if (LeftPane.Visibility != Visibility.Visible || _currentPage >= _thumbs.Count) return;
        if (ThumbList.SelectedIndex != _currentPage) ThumbList.SelectedIndex = _currentPage;
        ThumbList.ScrollIntoView(_thumbs[_currentPage]);
    }

    private void BuildThumbnails()
    {
        _thumbs.Clear();
        _thumbQueue.Clear();
        foreach (var page in _pages)
        {
            double w = 124, h = w * page.Geometry.ViewHeight / page.Geometry.ViewWidth;
            if (h > 170)
            {
                w *= 170 / h;
                h = 170;
            }
            _thumbs.Add(new ThumbnailItem(page.Index, Math.Round(w), Math.Round(h)));
        }
    }

    private void RefreshThumbnails()
    {
        foreach (var thumb in _thumbs.Where(t => t.Image is not null)) QueueThumbnail(thumb);
    }

    private void ThumbList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is ThumbnailItem { Image: null } thumb) QueueThumbnail(thumb);
    }

    private void ThumbList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ThumbnailItem thumb) GoToPage(thumb.Index);
    }

    private void QueueThumbnail(ThumbnailItem thumb)
    {
        if (thumb.IsRendering) return;
        thumb.IsRendering = true;
        _thumbQueue.Enqueue(thumb);
        _ = RenderThumbnailsAsync();
    }

    private async Task RenderThumbnailsAsync()
    {
        if (_renderingThumbs) return;
        _renderingThumbs = true;
        try
        {
            while (_thumbQueue.Count > 0 && !_disposed)
            {
                var thumb = _thumbQueue.Dequeue();
                int w = Math.Max(1, (int)(thumb.Width * RasterScale)), h = Math.Max(1, (int)(thumb.Height * RasterScale));
                if (thumb.Index >= _doc.PageCount) continue;
                byte[] pixels = await Task.Run(() => _doc.RenderPage(thumb.Index, w, h));
                if (_disposed) return;
                var bitmap = new WriteableBitmap(w, h);
                pixels.CopyTo(bitmap.PixelBuffer);
                bitmap.Invalidate();
                thumb.Image = bitmap;
                thumb.IsRendering = false;
            }
        }
        catch (Exception e) when (e is PdfException or ObjectDisposedException or ArgumentException)
        {
            // Thumbnails are best effort; the main view reports page errors.
        }
        finally
        {
            _renderingThumbs = false;
        }
    }

    // Search ----------------------------------------------------------------------------------

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            bool back = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
            if (SearchBox.Text == _lastQuery && _hits.Count > 0) StepHit(back ? -1 : +1);
            else _ = RunSearchAsync(SearchBox.Text);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
    }

    private void SearchNext_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text != _lastQuery) _ = RunSearchAsync(SearchBox.Text);
        else StepHit(+1);
    }

    private void SearchPrev_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text != _lastQuery) _ = RunSearchAsync(SearchBox.Text);
        else StepHit(-1);
    }

    private void SearchOption_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) _ = RunSearchAsync(SearchBox.Text);
    }

    /// <summary>Searches the document. After an edit the search is re-run quietly (no jump).</summary>
    private async Task RunSearchAsync(string query, bool navigate = true)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        _lastQuery = query;
        if (string.IsNullOrWhiteSpace(query))
        {
            ClearSearchMarks();
            return;
        }
        SearchStatus.Text = "...";
        try
        {
            bool matchCase = MatchCaseToggle.IsChecked == true, wholeWord = WholeWordToggle.IsChecked == true;
            int previous = _hitIndex;
            var hits = await Task.Run(() => _doc.Search(query, matchCase, wholeWord, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || _disposed) return;
            _hits = hits;
            _hitIndex = hits.Count == 0 ? -1
                : !navigate && previous >= 0 ? Math.Min(previous, hits.Count - 1)
                : Math.Max(0, hits.ToList().FindIndex(h => h.PageIndex >= _currentPage));
            ShowSearchMarks();
            if (navigate && _hitIndex >= 0) GoToPage(_hits[_hitIndex].PageIndex, _hits[_hitIndex].Rects.FirstOrDefault());
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StepHit(int direction)
    {
        if (_hits.Count == 0) return;
        _hitIndex = (_hitIndex + direction + _hits.Count) % _hits.Count;
        ShowSearchMarks();
        var hit = _hits[_hitIndex];
        GoToPage(hit.PageIndex, hit.Rects.FirstOrDefault());
    }

    private void ShowSearchMarks()
    {
        var byPage = _hits.ToLookup(h => h.PageIndex);
        var current = _hitIndex >= 0 ? _hits[_hitIndex] : null;
        foreach (var page in _pages)
        {
            var hits = byPage[page.Index].ToList();
            page.ShowSearchHits(hits.Where(h => h != current).SelectMany(h => h.Rects),
                current?.PageIndex == page.Index ? current.Rects : null);
        }
        SearchStatus.Text = _hits.Count == 0 ? "No results" : $"{_hitIndex + 1} of {_hits.Count}";
    }

    private void ClearSearchMarks()
    {
        _hits = [];
        _hitIndex = -1;
        _lastQuery = string.Empty;
        foreach (var page in _pages) page.ShowSearchHits([], null);
        SearchStatus.Text = string.Empty;
    }
}
