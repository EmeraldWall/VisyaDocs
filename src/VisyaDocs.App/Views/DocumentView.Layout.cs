using Microsoft.UI.Xaml;
using VisyaDocs.App.Services;

namespace VisyaDocs.App.Views;

// Keeps everything inside its space when the window is resized: the page area never gets too narrow
// to read, and the floating tool bar, notices, search box and text options never overlap.
public sealed partial class DocumentView
{
    /// <summary>Narrowest page area worth reading in; side panes make way below this.</summary>
    private const double MinPageAreaWidth = 360;
    private const double OverlayGap = 12;

    /// <summary>The thumbnails were hidden to make room (not by the user), so they come back when there is room.</summary>
    private bool _thumbsAutoHidden;

    private double SidePaneWidth => SidePane.Visibility == Visibility.Visible ? SidePane.Width : 0;

    /// <summary>Hides or restores the thumbnails and narrows the side pane so the page area keeps its minimum width.</summary>
    private void FitPanes()
    {
        if (ActualWidth <= 0) return;
        // The side pane gives up width first (down to 240), then the thumbnails step aside.
        SidePane.Width = Math.Clamp(ActualWidth - MinPageAreaWidth - (LeftPane.Visibility == Visibility.Visible ? LeftPane.Width : 0), 240, 330);
        double free = ActualWidth - SidePaneWidth;
        if (LeftPane.Visibility == Visibility.Visible && free - LeftPane.Width < MinPageAreaWidth)
        {
            SetThumbnailsVisible(false);
            _thumbsAutoHidden = true;
        }
        else if (_thumbsAutoHidden && LeftPane.Visibility != Visibility.Visible && free - LeftPane.Width >= MinPageAreaWidth + 40)
        {
            _thumbsAutoHidden = false;
            SetThumbnailsVisible(true);
        }
    }

    /// <summary>
    /// Places the floating items in the part of the page area the tool bar leaves free: notices and text
    /// options are centered there, the search box shrinks (and drops its option toggles) when space is
    /// short, and the tool bar is never taller than the page area (it scrolls instead).
    /// </summary>
    private void ArrangeOverlays()
    {
        double width = PageArea.ActualWidth, height = PageArea.ActualHeight;
        if (width <= 0) return;
        bool railLeft = AppSettings.Current.RailSide == RailSide.Left;
        double left = railLeft ? Math.Max(RailFootprint, OverlayGap) : OverlayGap;
        double right = railLeft ? OverlayGap : Math.Max(RailFootprint, OverlayGap);
        double free = Math.Max(0, width - left - right);

        Rail.MaxHeight = Math.Max(80, height - 2 * RailMargin);

        SearchPanel.Margin = new Thickness(0, OverlayGap, right + 4, 0);
        bool roomForOptions = free >= 480;
        MatchCaseToggle.Visibility = WholeWordToggle.Visibility = roomForOptions ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Width = Math.Clamp(free - (roomForOptions ? 260 : 200), 100, 220);
        SearchPanel.MaxWidth = free;

        // Notices sit below the search box when both would share a narrow top edge.
        bool searchOpen = SearchPanel.Visibility == Visibility.Visible;
        double top = searchOpen && free < 1000 ? OverlayGap + 52 : OverlayGap;
        NoticeStack.Margin = new Thickness(left, top, right, 0);
        NoticeStack.MaxWidth = Math.Min(620, free);
        TextOptions.Margin = new Thickness(left, OverlayGap, right, 0);
        TextOptions.MaxWidth = free;
        StatusPill.MaxWidth = Math.Max(0, width - 2 * OverlayGap);
    }
}
