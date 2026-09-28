using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using VisyaDocs.Core;
using Windows.Foundation;

namespace VisyaDocs.App.Views;

/// <summary>
/// One page in the viewer: the rendered bitmap, a non-interactive layer for search hits and the
/// text selection (drawn in page points and scaled), and an interactive overlay in screen units
/// that receives pointer input and hosts editors and comment hotspots.
/// </summary>
public sealed partial class PageView : Grid
{
    private static readonly SolidColorBrush SearchBrush = new(ColorHelper.FromArgb(0x66, 0xFF, 0xB9, 0x00));
    private static readonly SolidColorBrush SearchCurrentBrush = new(ColorHelper.FromArgb(0xAA, 0xFF, 0x7A, 0x00));
    private static readonly SolidColorBrush SelectionBrush = new(ColorHelper.FromArgb(0x55, 0x2B, 0x7F, 0xFF));

    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _marks = new() { IsHitTestVisible = false };
    private readonly Canvas _search = new();
    private readonly Canvas _selection = new();
    private readonly ScaleTransform _marksScale = new();
    private readonly List<(FrameworkElement Element, ViewRect Rect, string Layer)> _hotspots = [];

    public PageView(int index, PageGeometry geometry)
    {
        Index = index;
        Geometry = geometry;
        BorderThickness = new Thickness(1);
        _marks.RenderTransform = _marksScale;
        _marks.Children.Add(_search);
        _marks.Children.Add(_selection);
        Overlay = new Canvas { Background = new SolidColorBrush(Colors.Transparent) };
        Children.Add(_image);
        Children.Add(_marks);
        Children.Add(Overlay);
    }

    public int Index { get; }
    public PageGeometry Geometry { get; }

    /// <summary>Screen units (DIPs) per PDF point.</summary>
    public double DipPerPoint { get; private set; } = 1;

    /// <summary>Identifies the zoom and DPI the current bitmap was rendered for.</summary>
    public double RenderedStamp { get; private set; }

    /// <summary>Interactive layer, in DIPs. Editors and hotspots are placed here.</summary>
    public Canvas Overlay { get; }

    public void SetScale(double scale)
    {
        DipPerPoint = scale;
        Width = Math.Round(Geometry.ViewWidth * scale);
        Height = Math.Round(Geometry.ViewHeight * scale);
        _marksScale.ScaleX = _marksScale.ScaleY = scale;
        foreach (var (element, rect, _) in _hotspots) Place(element, rect);
    }

    public void SetBitmap(ImageSource? source, double stamp)
    {
        _image.Source = source;
        RenderedStamp = source is null ? 0 : stamp;
    }

    public void ClearBitmap() => SetBitmap(null, 0);

    public void SetCursor(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);

    /// <summary>Content version the comment hotspots were built for.</summary>
    public int HotspotVersion { get; set; } = -1;

    /// <summary>Dark mode dimming: the page shows through a black backing at reduced opacity.</summary>
    public void ApplyAppearance(bool dark, bool dim, Brush border)
    {
        Background = new SolidColorBrush(dark && dim ? Colors.Black : Colors.White);
        _image.Opacity = dark && dim ? 0.84 : 1;
        BorderBrush = border;
    }

    public Point ToPage(Point dip)
    {
        var (x, y) = Geometry.ToPage(dip.X / DipPerPoint, dip.Y / DipPerPoint);
        return new Point(x, y);
    }

    public Rect ToDip(PdfRect rect)
    {
        var v = Geometry.ToView(rect).Scale(DipPerPoint);
        return new Rect(v.X, v.Y, v.Width, v.Height);
    }

    public void ShowSearchHits(IEnumerable<PdfRect> hits, IReadOnlyList<PdfRect>? current)
    {
        _search.Children.Clear();
        foreach (var r in hits) AddRect(_search, r, SearchBrush);
        if (current is not null) foreach (var r in current) AddRect(_search, r, SearchCurrentBrush);
    }

    public void ShowSelection(IReadOnlyList<PdfRect> rects)
    {
        _selection.Children.Clear();
        foreach (var r in rects) AddRect(_selection, r, SelectionBrush);
    }

    public void ClearSelection() => _selection.Children.Clear();

    /// <summary>Form fields of this page, refreshed with the comment hotspots.</summary>
    public IReadOnlyList<FormField> FormFields { get; set; } = [];

    /// <summary>
    /// Adds an interactive element over a page space rectangle (a sticky note hotspot, a form field
    /// editor, a signature preview). It follows zoom changes. Layers can be cleared separately.
    /// </summary>
    public void AddHotspot(FrameworkElement element, PdfRect rect, string layer = "notes")
    {
        var view = Geometry.ToView(rect);
        _hotspots.Add((element, view, layer));
        Overlay.Children.Add(element);
        Place(element, view);
    }

    /// <summary>Adds an element over a rectangle given in view points (top-left origin).</summary>
    public void AddHotspot(FrameworkElement element, ViewRect view, string layer)
    {
        _hotspots.Add((element, view, layer));
        Overlay.Children.Add(element);
        Place(element, view);
    }

    /// <summary>Moves or resizes an element added with <see cref="AddHotspot(FrameworkElement, ViewRect, string)"/>.</summary>
    public void UpdateHotspot(FrameworkElement element, ViewRect view)
    {
        int i = _hotspots.FindIndex(h => h.Element == element);
        if (i < 0) return;
        _hotspots[i] = (element, view, _hotspots[i].Layer);
        Place(element, view);
    }

    public void ClearHotspots(string layer = "notes")
    {
        foreach (var (element, _, _) in _hotspots.Where(h => h.Layer == layer)) Overlay.Children.Remove(element);
        _hotspots.RemoveAll(h => h.Layer == layer);
    }

    private void Place(FrameworkElement element, ViewRect view)
    {
        var r = view.Scale(DipPerPoint);
        Canvas.SetLeft(element, r.X);
        Canvas.SetTop(element, r.Y);
        element.Width = Math.Max(12, r.Width);
        element.Height = Math.Max(12, r.Height);
    }

    private void AddRect(Canvas canvas, PdfRect rect, Brush brush)
    {
        var v = Geometry.ToView(rect);
        var shape = new Rectangle { Width = v.Width, Height = v.Height, Fill = brush, RadiusX = 1, RadiusY = 1 };
        Canvas.SetLeft(shape, v.X);
        Canvas.SetTop(shape, v.Y);
        canvas.Children.Add(shape);
    }
}
