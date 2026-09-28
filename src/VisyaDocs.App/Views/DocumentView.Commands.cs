using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VisyaDocs.App.Services;

namespace VisyaDocs.App.Views;

/// <summary>Document commands offered by the window's File menu.</summary>
public enum DocCommand
{
    Save,
    SaveAs,
    Print,
    Properties,
    Ocr,
    ExtractText,
    ExportWord,
    ExportText,
    ExportPng,
    ExportJpeg,
    AppendPdf,
    AppendImages,
}

// File menu commands and the movable tool rail.
public sealed partial class DocumentView
{
    private const double RailMargin = 12;

    /// <summary>Runs a File menu command on this document (the same code as the shortcuts).</summary>
    public void Execute(DocCommand command)
    {
        var none = new RoutedEventArgs();
        switch (command)
        {
            case DocCommand.Save: Save_Click(this, none); break;
            case DocCommand.SaveAs: SaveAs_Click(this, none); break;
            case DocCommand.Print: Print_Click(this, none); break;
            case DocCommand.Properties: Properties_Click(this, none); break;
            case DocCommand.Ocr: MakeSearchable_Click(this, none); break;
            case DocCommand.ExtractText: ExtractText_Click(this, none); break;
            case DocCommand.ExportWord: ExportWord_Click(this, none); break;
            case DocCommand.ExportText: ExportText_Click(this, none); break;
            case DocCommand.ExportPng: ExportImages_Click(new Border { Tag = "Png" }, none); break;
            case DocCommand.ExportJpeg: ExportImages_Click(new Border { Tag = "Jpeg" }, none); break;
            case DocCommand.AppendPdf: AppendPdf_Click(this, none); break;
            case DocCommand.AppendImages: AppendImages_Click(this, none); break;
        }
    }

    /// <summary>Applies settings changed on the Settings page (dimming, tool bar position).</summary>
    public void OnSettingsChanged()
    {
        OnThemeChanged();
        SetRailCollapsed(_fullScreen || AppSettings.Current.RailCollapsed);
        PositionRail();
        LayoutPages();
        UpdateVisiblePages();
    }

    // Tool rail position --------------------------------------------------------------------

    private bool _draggingRail;
    private Windows.Foundation.Point _dragOffset;

    /// <summary>Space the docked rail takes from the page area, so pages are never laid out under it.</summary>
    private double RailFootprint => Rail.ActualWidth > 0 ? Rail.ActualWidth + RailMargin + 8 : 0;

    private double LeftInset => AppSettings.Current.RailSide == RailSide.Left ? RailFootprint : 0;

    private double RightInset => AppSettings.Current.RailSide == RailSide.Right ? RailFootprint : 0;

    /// <summary>Docks the rail to its side at the saved height, kept inside the page area.</summary>
    private void PositionRail()
    {
        if (_draggingRail) return;
        double free = Math.Max(0, PageArea.ActualHeight - Rail.ActualHeight - 2 * RailMargin);
        double top = RailMargin + Math.Round(Math.Clamp(AppSettings.Current.RailTop, 0, 1) * free);
        bool left = AppSettings.Current.RailSide == RailSide.Left;
        Rail.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Rail.Margin = left ? new Thickness(RailMargin, top, 0, 0) : new Thickness(0, top, RailMargin, 0);
    }

    private void Rail_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PositionRail();
        // The footprint changed (expanded or folded): lay the pages out around it again.
        if (_zoomKind == ZoomKind.FitWidth && _pages.Count > 0) SetZoom(FitWidthZoom(), ZoomKind.FitWidth);
        else
        {
            LayoutPages();
            UpdateVisiblePages();
        }
    }

    private void RailGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInside<Button>(source)) return;
        var point = e.GetCurrentPoint(PageArea).Position;
        var origin = Rail.TransformToVisual(PageArea).TransformPoint(new Windows.Foundation.Point(0, 0));
        _dragOffset = new Windows.Foundation.Point(point.X - origin.X, point.Y - origin.Y);
        _draggingRail = true;
        RailGrip.CapturePointer(e.Pointer);
        Rail.HorizontalAlignment = HorizontalAlignment.Left;
        Rail.Margin = new Thickness(origin.X, origin.Y, 0, 0);
        e.Handled = true;
    }

    private void RailGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingRail) return;
        var point = e.GetCurrentPoint(PageArea).Position;
        double x = Math.Clamp(point.X - _dragOffset.X, 0, Math.Max(0, PageArea.ActualWidth - Rail.ActualWidth));
        double y = Math.Clamp(point.Y - _dragOffset.Y, 0, Math.Max(0, PageArea.ActualHeight - Rail.ActualHeight));
        Rail.Margin = new Thickness(Math.Round(x), Math.Round(y), 0, 0);
        e.Handled = true;
    }

    private void RailGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingRail) return;
        _draggingRail = false;
        RailGrip.ReleasePointerCapture(e.Pointer);
        // Snap to the nearer side, keep the dropped height.
        double centerX = Rail.Margin.Left + Rail.ActualWidth / 2;
        double free = Math.Max(1, PageArea.ActualHeight - Rail.ActualHeight - 2 * RailMargin);
        var settings = AppSettings.Current;
        settings.RailSide = centerX < PageArea.ActualWidth / 2 ? RailSide.Left : RailSide.Right;
        settings.RailTop = Math.Clamp((Rail.Margin.Top - RailMargin) / free, 0, 1);
        settings.Save();
        PositionRail();
        LayoutPages();
        UpdateVisiblePages();
        e.Handled = true;
    }

    private static bool IsInside<T>(DependencyObject element) where T : DependencyObject
    {
        for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is T) return true;
        }
        return false;
    }
}
