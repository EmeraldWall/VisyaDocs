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
    private const double MaxBitmapPixels = 16_000_000;
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
        // At least the viewport height, so Ctrl+wheel and pinch below a short document still reach the canvas.
        PagesCanvas.Height = Math.Max(contentHeight, Scroller.ViewportHeight);
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
        _pendingZoom = null;
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
        double current = _pendingZoom ?? _zoom;
        double next = direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > current + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(z => z < current - 0.001, ZoomSteps[0]);
        ZoomSmoothly(next, ZoomKind.Custom);
    }

    private double? _pendingZoom;
    private ZoomKind _pendingZoomKind;

    /// <summary>
    /// Zoom commands (buttons, Ctrl+plus/minus, fit) animate the view to the new size around the
    /// viewport center, like pinch zoom; the pages are re-rendered sharp when the animation ends.
    /// </summary>
    private void ZoomSmoothly(double zoom, ZoomKind kind)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        double factor = zoom / _zoom;
        if (!Motion.Enabled || IsSinglePage || _pages.Count == 0 || Math.Abs(factor - 1) < 0.01 || Math.Abs(Scroller.ZoomFactor - 1) > 0.005)
        {
            SetZoom(zoom, kind);
            return;
        }
        CommitEditor();
        Scroller.MinZoomFactor = (float)Math.Min(Scroller.MinZoomFactor, factor);
        Scroller.MaxZoomFactor = (float)Math.Max(Scroller.MaxZoomFactor, factor);
        double cx = Scroller.HorizontalOffset + Scroller.ViewportWidth / 2;
        double cy = Scroller.VerticalOffset + Scroller.ViewportHeight / 2;
        _pendingZoom = zoom;
        _pendingZoomKind = kind;
        ZoomText.Text = $"{zoom * 100:0}%";
        ShowStatusPill();
        if (!Scroller.ChangeView(Math.Max(0, cx * factor - Scroller.ViewportWidth / 2),
                Math.Max(0, cy * factor - Scroller.ViewportHeight / 2), (float)factor, false))
        {
            _pendingZoom = null;
            SetZoom(zoom, kind);
        }
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
        double zoom = _zoom * factor;
        var kind = ZoomKind.Custom;
        // An animated zoom command lands exactly on its target (and keeps "fit width" or "fit page").
        if (_pendingZoom is { } target && Math.Abs(target - zoom) / target < 0.02)
        {
            zoom = target;
            kind = _pendingZoomKind;
        }
        _pendingZoom = null;
        ApplyZoom(zoom, kind, anchor);
    }

    // Pinch and Ctrl+wheel -----------------------------------------------------------------------

    private bool _manipulating, _wheelZooming;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _wheelZoomTimer;

    private void Scroller_DirectManipulationStarted(object? sender, object e) => _manipulating = true;

    /// <summary>A touch or touchpad pinch has ended (including its inertia): render the pages sharp at the new size.</summary>
    private void Scroller_DirectManipulationCompleted(object? sender, object e)
    {
        _manipulating = false;
        if (!_wheelZooming && Math.Abs(Scroller.ZoomFactor - 1) > 0.005) FoldZoomFactor();
    }

    /// <summary>
    /// Ctrl+wheel, which is also how many touchpads report a pinch. The built-in zoom takes a full step
    /// for every event, and a touchpad sends dozens of tiny ones per pinch, so a small pinch zoomed far
    /// too much. Here the zoom follows the actual amount scrolled (one mouse wheel notch is about 15%),
    /// is centered on the pointer, and the pages are rendered sharp shortly after the pinch stops.
    /// </summary>
    private void PagesCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || _pages.Count == 0) return;
        var point = e.GetCurrentPoint(Scroller);
        if (point.Properties.IsHorizontalMouseWheel) return;
        e.Handled = true;

        float current = Scroller.ZoomFactor;
        double factor = Math.Pow(2, point.Properties.MouseWheelDelta / 600.0);
        float target = (float)Math.Clamp(current * factor, Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
        if (Math.Abs(target - current) < 0.0005) return;
        double x = (Scroller.HorizontalOffset + point.Position.X) / current;
        double y = (Scroller.VerticalOffset + point.Position.Y) / current;
        _wheelZooming = true;
        Scroller.ChangeView(Math.Max(0, x * target - point.Position.X), Math.Max(0, y * target - point.Position.Y), target, true);
        ZoomText.Text = $"{_zoom * target * 100:0}%";
        ShowStatusPill();

        if (_wheelZoomTimer is null)
        {
            _wheelZoomTimer = _dispatcher.CreateTimer();
            _wheelZoomTimer.Interval = TimeSpan.FromMilliseconds(220);
            _wheelZoomTimer.IsRepeating = false;
            _wheelZoomTimer.Tick += (_, _) =>
            {
                _wheelZooming = false;
                if (!_manipulating && !_disposed && Math.Abs(Scroller.ZoomFactor - 1) > 0.005) FoldZoomFactor();
            };
        }
        _wheelZoomTimer.Stop();
        _wheelZoomTimer.Start();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => StepZoom(+1);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => StepZoom(-1);

    private void FitWidth_Click(object sender, RoutedEventArgs e) => ZoomSmoothly(FitWidthZoom(), ZoomKind.FitWidth);

    private void FitPage_Click(object sender, RoutedEventArgs e) => ZoomSmoothly(FitPageZoom(), ZoomKind.FitPage);

    private void ZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
            ZoomSmoothly(zoom, ZoomKind.Custom);
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
        // Sharp re-render only once a pinch or wheel zoom is over, never in the middle of it: folding
        // mid-gesture made the rest of the gesture apply on top of the new size (far too much zoom).
        if (!e.IsIntermediate && !_manipulating && !_wheelZooming && Math.Abs(Scroller.ZoomFactor - 1) > 0.005)
        {
            FoldZoomFactor();
            return;
        }
        UpdateVisiblePages();
        if (!e.IsIntermediate)
        {
            ShowStatusPill();
            BitmapPool.TrimWhenIdle();
        }
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
            case VirtualKey.Space:
                // Space and Shift+Space move by a screen, like a web browser.
                bool back = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
                if (IsSinglePage) FlipPage(back ? -1 : +1);
                else Scroller.ChangeView(null, Math.Max(0, Scroller.VerticalOffset + (back ? -1 : 1) * Scroller.ViewportHeight * 0.9), null, !Motion.Enabled);
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
        if (_suspended || _pages.Count == 0 || _tops.Length != _pages.Count) return;
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
            // Render what is on screen plus a little ahead; free bitmaps once pages are well out of view.
            double keepFrom = top - height * 0.5, keepTo = top + height * 1.5;
            double dropFrom = top - height * 1.5, dropTo = top + height * 2.5;
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

                byte[] pixels = BitmapPool.RentBuffer(pw * ph * 4);
                try
                {
                    var (notes, fields, links) = await Task.Run(() =>
                    {
                        _doc.RenderPage(index, pixels, pw, ph);
                        return (needOverlays ? _doc.GetAnnotations(index) : null,
                            needOverlays && wantForm ? _doc.GetFormFields(index) : null,
                            needOverlays ? _doc.GetLinks(index) : null);
                    });

                    if (_disposed || _suspended || index >= _pages.Count || _pages[index] != page) continue;
                    // Reuse the page's own bitmap when the size is unchanged (content edits), else a pooled one.
                    var bitmap = page.Bitmap is { } own && own.PixelWidth == pw && own.PixelHeight == ph ? own : BitmapPool.Rent(pw, ph);
                    BitmapPool.Fill(bitmap, pixels);
                    page.SetBitmap(bitmap, stamp);
                    if (notes is not null && version == _contentVersion)
                    {
                        ShowNoteHotspots(page, notes, version);
                        page.Links = links ?? [];
                        page.FormFields = fields ?? [];
                        ShowFormOverlays(page);
                    }
                }
                finally
                {
                    BitmapPool.ReturnBuffer(pixels);
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

    private bool _suspended;

    /// <summary>
    /// A tab in the background or a minimized window shows nothing, so it gives its page and
    /// thumbnail images back; they are rendered again when it is shown.
    /// </summary>
    public void SetSuspended(bool suspended)
    {
        if (suspended == _suspended || _disposed) return;
        _suspended = suspended;
        if (suspended)
        {
            _renderQueue.Clear();
            _thumbQueue.Clear();
            foreach (var page in _pages) page.ClearBitmap();
            foreach (var thumb in _thumbs)
            {
                thumb.Image = null;
                thumb.IsRendering = false;
            }
            BitmapPool.Trim();
        }
        else
        {
            UpdateVisiblePages();
            if (LeftPane.Visibility == Visibility.Visible)
            {
                for (int i = 0; i < _thumbs.Count; i++)
                {
                    if (ThumbList.ContainerFromIndex(i) is not null) QueueThumbnail(_thumbs[i]);
                }
            }
        }
    }

    // Navigation ------------------------------------------------------------------------------

    private void GoToPage(int index, PdfRect? target = null)
    {
        if (_pages.Count == 0) return;
        index = Math.Clamp(index, 0, _pages.Count - 1);
        if (IsSinglePage)
        {
            int direction = Math.Sign(index - _currentPage);
            _currentPage = index;
            LayoutPages();
            Scroller.ChangeView(null, 0, null, true);
            UpdateVisiblePages();
            ShowStatusPill();
            // Flipping slides the new page in from the side it comes from.
            if (direction != 0) Motion.SlideIn(_pages[index], direction * 36);
            BitmapPool.TrimWhenIdle();
            return;
        }
        double y = _tops[index] - 8;
        if (target is { } rect && rect.Width > 0)
        {
            var dip = _pages[index].ToDip(rect);
            y = _tops[index] + dip.Y - Scroller.ViewportHeight / 3;
        }
        // Nearby jumps scroll smoothly; far jumps (for example to the last page) are instant.
        double to = Math.Max(0, y * Scroller.ZoomFactor);
        bool animate = Motion.Enabled && Math.Abs(to - Scroller.VerticalOffset) < 4 * Scroller.ViewportHeight;
        Scroller.ChangeView(null, to, null, !animate);
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
        _thumbsAutoHidden = false;
        // In a narrow window the thumbnails take the place of the side pane.
        if (show && ActualWidth - LeftPane.Width - SidePaneWidth < MinPageAreaWidth) CloseSidePane();
        SetThumbnailsVisible(show);
    }

    private void SetThumbnailsVisible(bool show)
    {
        ThumbsToggle.IsChecked = show;
        if (show && _thumbs.Count != _pages.Count) BuildThumbnails();
        LeftPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) SyncThumbnail();
        else foreach (var thumb in _thumbs) thumb.Image = null;
    }

    private int _reportedPage = -1;

    /// <summary>Selects and reveals the thumbnail of the page being read.</summary>
    private void SyncThumbnail()
    {
        if (LeftPane.Visibility != Visibility.Visible || _currentPage >= _thumbs.Count) return;
        _syncingThumbnail = true;
        if (ThumbList.SelectedIndex != _currentPage) ThumbList.SelectedIndex = _currentPage;
        _syncingThumbnail = false;
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
        if (args.Item is not ThumbnailItem thumb) return;
        // Only thumbnails in view keep their images.
        if (args.InRecycleQueue) thumb.Image = null;
        else if (thumb.Image is null) QueueThumbnail(thumb);
    }

    private void ThumbList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ThumbnailItem thumb) GoToPage(thumb.Index);
    }

    private bool _syncingThumbnail;

    /// <summary>Arrow keys in the thumbnail list move through the document too.</summary>
    private void ThumbList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingThumbnail || ThumbList.FocusState != FocusState.Keyboard) return;
        if (ThumbList.SelectedItem is ThumbnailItem thumb && thumb.Index != _currentPage) GoToPage(thumb.Index);
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
                if (thumb.Index >= _doc.PageCount || _suspended || LeftPane.Visibility != Visibility.Visible)
                {
                    thumb.IsRendering = false;
                    continue;
                }
                byte[] pixels = BitmapPool.RentBuffer(w * h * 4);
                try
                {
                    await Task.Run(() => _doc.RenderPage(thumb.Index, pixels, w, h));
                    if (_disposed) return;
                    var bitmap = new WriteableBitmap(w, h);
                    BitmapPool.Fill(bitmap, pixels);
                    thumb.Image = bitmap;
                    thumb.IsRendering = false;
                }
                finally
                {
                    BitmapPool.ReturnBuffer(pixels);
                }
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
