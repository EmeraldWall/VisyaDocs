using System.Collections.ObjectModel;
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

// Page layout, lazy rendering, zoom, navigation, thumbnails and search.
public sealed partial class DocumentView
{
    private const double PtToDip = 96.0 / 72.0;
    private const double PagePadding = 24, PageSpacing = 14;
    private const double MaxBitmapPixels = 24_000_000;
    private static readonly double[] ZoomSteps = [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5];
    private static readonly double[] ZoomBoxValues = [0, 0, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    private readonly List<PageView> _pages = [];
    private readonly HashSet<int> _renderQueue = [];
    private readonly ObservableCollection<ThumbnailItem> _thumbs = [];
    private readonly Queue<ThumbnailItem> _thumbQueue = new();
    private double[] _tops = [];
    private double _zoom = 1;
    private ZoomKind _zoomKind = ZoomKind.FitWidth;
    private int _currentPage;
    private int _contentVersion;
    private bool _rendering, _renderingThumbs, _updatingZoomBox;

    private IReadOnlyList<SearchHit> _hits = [];
    private int _hitIndex = -1;
    private string _lastQuery = string.Empty;
    private CancellationTokenSource? _searchCts;

    private double RasterScale => XamlRoot?.RasterizationScale ?? 1;
    private double CurrentStamp => _contentVersion * 1e4 + Math.Round(_zoom * RasterScale, 4);

    // Layout ----------------------------------------------------------------------------------

    private async Task BuildPagesAsync()
    {
        int count = _doc.PageCount;
        var geometries = await Task.Run(() => Enumerable.Range(0, count).Select(_doc.GetGeometry).ToArray());
        if (_disposed) return;

        PagesPanel.Children.Clear();
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
            page.SetScale(_zoom * PtToDip);
            ApplyAppearance(page);
            _pages.Add(page);
            PagesPanel.Children.Add(page);
        }
        SetTool(_tool);
        PageCountText.Text = $"of {count}";
        RecomputeTops();
    }

    private void ApplyAppearance(PageView page)
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        page.ApplyAppearance(dark, AppSettings.Current.DimPagesInDark,
            new SolidColorBrush(dark ? ColorHelper.FromArgb(255, 51, 51, 51) : ColorHelper.FromArgb(255, 200, 200, 200)));
    }

    private void RecomputeTops()
    {
        _tops = new double[_pages.Count];
        double y = PagePadding;
        for (int i = 0; i < _pages.Count; i++)
        {
            _tops[i] = y;
            y += _pages[i].Height + PageSpacing;
        }
    }

    // Zoom ------------------------------------------------------------------------------------

    private double FitWidthZoom()
    {
        double widest = _pages.Count == 0 ? 612 : _pages.Max(p => p.Geometry.ViewWidth);
        double available = Math.Max(100, Scroller.ViewportWidth - 2 * PagePadding - 16);
        return available / (widest * PtToDip);
    }

    private double FitPageZoom()
    {
        if (_pages.Count == 0) return 1;
        var g = _pages[Math.Clamp(_currentPage, 0, _pages.Count - 1)].Geometry;
        double available = Math.Max(100, Scroller.ViewportHeight - 2 * PagePadding);
        return Math.Min(FitWidthZoom(), available / (g.ViewHeight * PtToDip));
    }

    private void SetZoom(double zoom, ZoomKind kind = ZoomKind.Custom)
    {
        zoom = Math.Clamp(zoom, 0.1, 6);
        CommitEditor();
        double ratio = Scroller.ExtentHeight > 0 ? Scroller.VerticalOffset / Scroller.ExtentHeight : 0;
        _zoom = zoom;
        _zoomKind = kind;
        foreach (var page in _pages) page.SetScale(zoom * PtToDip);
        RecomputeTops();
        UpdateZoomBox();
        PagesPanel.UpdateLayout();
        Scroller.ChangeView(null, ratio * Scroller.ExtentHeight, null, true);
        UpdateVisiblePages();
    }

    private void StepZoom(int direction)
    {
        double next = direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > _zoom + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(z => z < _zoom - 0.001, ZoomSteps[0]);
        SetZoom(next);
    }

    private void UpdateZoomBox()
    {
        _updatingZoomBox = true;
        int index = _zoomKind switch
        {
            ZoomKind.FitWidth => 0,
            ZoomKind.FitPage => 1,
            _ => Array.FindIndex(ZoomBoxValues, 2, v => Math.Abs(v - _zoom) < 0.001),
        };
        ZoomBox.SelectedIndex = index;
        ZoomBox.PlaceholderText = $"{_zoom * 100:0}%";
        _updatingZoomBox = false;
    }

    private void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingZoomBox || ZoomBox.SelectedIndex < 0) return;
        switch (ZoomBox.SelectedIndex)
        {
            case 0: SetZoom(FitWidthZoom(), ZoomKind.FitWidth); break;
            case 1: SetZoom(FitPageZoom(), ZoomKind.FitPage); break;
            default: SetZoom(ZoomBoxValues[ZoomBox.SelectedIndex]); break;
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => StepZoom(+1);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => StepZoom(-1);

    private void Scroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if (!ctrl.HasFlag(CoreVirtualKeyStates.Down)) return;
        StepZoom(e.GetCurrentPoint(Scroller).Properties.MouseWheelDelta > 0 ? +1 : -1);
        e.Handled = true;
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pages.Count == 0) return;
        if (_zoomKind == ZoomKind.FitWidth) SetZoom(FitWidthZoom(), ZoomKind.FitWidth);
        else if (_zoomKind == ZoomKind.FitPage) SetZoom(FitPageZoom(), ZoomKind.FitPage);
        else UpdateVisiblePages();
    }

    // Visible pages and rendering -------------------------------------------------------------

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateVisiblePages();

    /// <summary>
    /// Queues rendering for pages in or near the viewport and drops bitmaps of pages far away,
    /// so memory stays flat no matter how long the document is.
    /// </summary>
    private void UpdateVisiblePages()
    {
        if (_pages.Count == 0 || _tops.Length != _pages.Count) return;
        double top = Scroller.VerticalOffset, height = Math.Max(1, Scroller.ViewportHeight);
        double keepFrom = top - height, keepTo = top + 2 * height;
        double dropFrom = top - 4 * height, dropTo = top + 5 * height;
        double stamp = CurrentStamp;
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
        if (PageBox.FocusState == FocusState.Unfocused) PageBox.Text = (_currentPage + 1).ToString();
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
                bool needNotes = page.HotspotVersion != version;

                var (pixels, notes) = await Task.Run(() => (
                    _doc.RenderPage(index, pw, ph),
                    needNotes ? _doc.GetAnnotations(index) : null));

                if (_disposed || index >= _pages.Count || _pages[index] != page) continue;
                var bitmap = new WriteableBitmap(pw, ph);
                pixels.CopyTo(bitmap.PixelBuffer);
                bitmap.Invalidate();
                page.SetBitmap(bitmap, stamp);
                if (notes is not null && version == _contentVersion) ShowNoteHotspots(page, notes, version);
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
        double y = _tops[index] - 8;
        if (target is { } rect)
        {
            var dip = _pages[index].ToDip(rect);
            y = _tops[index] + dip.Y - Scroller.ViewportHeight / 3;
        }
        Scroller.ChangeView(null, Math.Max(0, y), null, true);
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
        ThumbList.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
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
            SearchBox.Text = string.Empty;
            ClearSearchMarks();
            Scroller.Focus(FocusState.Programmatic);
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

    private async Task RunSearchAsync(string query)
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
            var hits = await Task.Run(() => _doc.Search(query, cancellationToken: cts.Token), cts.Token);
            if (cts.IsCancellationRequested || _disposed) return;
            _hits = hits;
            _hitIndex = hits.Count == 0 ? -1 : Math.Max(0, hits.ToList().FindIndex(h => h.PageIndex >= _currentPage));
            ShowSearchMarks();
            if (_hitIndex >= 0) GoToPage(_hits[_hitIndex].PageIndex, _hits[_hitIndex].Rects.FirstOrDefault());
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
