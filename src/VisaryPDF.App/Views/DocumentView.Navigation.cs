using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using VisaryPDF.App.Services;
using VisaryPDF.Core;

namespace VisaryPDF.App.Views;

// Links, outline, reading position, author restrictions, accessibility and first-run help.
public sealed partial class DocumentView
{
    private PdfPermissions _permissions = new(true, true, true);
    private readonly Dictionary<TreeViewNode, OutlineItem> _outlineNodes = [];

    /// <summary>Puts keyboard focus on the pages so PageDown, arrows and shortcuts work at once.</summary>
    public void FocusViewer() => Scroller.Focus(FocusState.Programmatic);

    /// <summary>Areas F6 moves between, in order: tool bar, pages, thumbnails, side pane, search.</summary>
    public IEnumerable<UIElement> FocusRegions()
    {
        yield return RailPanel;
        yield return Scroller;
        if (LeftPane.Visibility == Visibility.Visible) yield return LeftPane;
        if (SidePane.Visibility == Visibility.Visible) yield return SidePane;
        if (SearchPanel.Visibility == Visibility.Visible) yield return SearchPanel;
    }

    private bool IsFocusWithin(DependencyObject region) =>
        XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsDescendant(focused, region);

    internal static bool IsDescendant(DependencyObject node, DependencyObject ancestor)
    {
        for (var n = node; n is not null; n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(n))
        {
            if (n == ancestor) return true;
        }
        return false;
    }

    /// <summary>True when the file is encrypted (open password, author restrictions or both).</summary>
    public bool CanRemoveProtection => _doc.IsEncrypted;

    /// <summary>
    /// Saves a copy without encryption: no password and no restrictions. The copy opens in a new
    /// tab; the original file is not changed.
    /// </summary>
    public async Task RemovePasswordAsync()
    {
        if (!_doc.IsEncrypted) return;
        if (!_permissions.All)
        {
            var confirm = Dialogs.Create(XamlRoot, "Remove the restrictions?", new TextBlock
            {
                Text = "The author of this PDF limited printing, copying or changes. VisaryPDF can save a copy without these "
                    + "restrictions and without a password. Only do this for documents you have the right to use this way.",
                TextWrapping = TextWrapping.Wrap,
            }, "Save unrestricted copy");
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }
        var path = await Pickers.SaveFileAsync(Path.GetFileNameWithoutExtension(_name) + " (unlocked)", "PDF document", ".pdf");
        if (path is null) return;
        try
        {
            await Task.Run(() => _doc.SaveUnprotectedCopy(path));
            ShowMessage("Unprotected copy saved", $"{path} opens in a new tab. The original file keeps its protection.", InfoBarSeverity.Success);
            App.MainWindow.OpenFiles([path]);
        }
        catch (Exception e) when (e is PdfException or IOException or UnauthorizedAccessException)
        {
            ShowMessage("Could not save the copy", e.Message, InfoBarSeverity.Error);
        }
    }

    /// <summary>Called once the pages are laid out: restores the reading position and sets up helpers.</summary>
    private async Task OnOpenedAsync()
    {
        _permissions = await Task.Run(() => _doc.Permissions);
        ApplyPermissions();
        await BuildOutlineAsync();
        if (_doc.FilePath is { } path && AppSettings.Current.GetPosition(path) is int page && page > 0 && page < _pages.Count)
            GoToPage(page);
        ApplyAutomationNames(Rail);
        ApplyAutomationNames(SearchPanel);
        ApplyAutomationNames(StatusPill);
        if (!AppSettings.Current.TipShown)
        {
            TipBar.IsOpen = true;
            AppSettings.Current.TipShown = true;
            AppSettings.Current.Save();
        }
        FocusViewer();
    }

    /// <summary>Remembers the page being read, so the file reopens there.</summary>
    private void RememberPosition()
    {
        if (_doc.FilePath is { } path && _pages.Count > 0) AppSettings.Current.SetPosition(path, _currentPage);
    }

    // Links -----------------------------------------------------------------------------------

    private async Task FollowLinkAsync(PdfLink link)
    {
        if (link.TargetPage is int page)
        {
            GoToPage(page, link.TargetY is double y ? new PdfRect(0, y - 1, 1, y) : null);
            return;
        }
        if (link.Uri is not { } address || !Uri.TryCreate(address, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https" or "mailto"))
        {
            ShowMessage("Link not opened", $"VisaryPDF only opens web and e-mail links. This one is: {address}", InfoBarSeverity.Warning);
            return;
        }
        var text = new TextBlock { Text = uri.ToString(), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var dialog = Dialogs.Create(XamlRoot, "Open this link in your browser?", text, "Open");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    // Outline ---------------------------------------------------------------------------------

    private async Task BuildOutlineAsync()
    {
        IReadOnlyList<OutlineItem> outline;
        try
        {
            outline = await Task.Run(_doc.GetOutline);
        }
        catch (PdfException)
        {
            outline = [];
        }
        OutlineTree.RootNodes.Clear();
        _outlineNodes.Clear();
        foreach (var item in outline) OutlineTree.RootNodes.Add(CreateNode(item, depth: 0));
        LeftPaneTabs.Visibility = outline.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (outline.Count > 0) ToolTipService.SetToolTip(ThumbsToggle, "Pages and outline");
    }

    private TreeViewNode CreateNode(OutlineItem item, int depth)
    {
        var node = new TreeViewNode { Content = item.Title, IsExpanded = depth == 0 && item.Children.Count <= 12 };
        _outlineNodes[node] = item;
        foreach (var child in item.Children) node.Children.Add(CreateNode(child, depth + 1));
        return node;
    }

    private void OutlineTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node && _outlineNodes.TryGetValue(node, out var item) && item.TargetPage is int page)
            GoToPage(page, item.TargetY is double y ? new PdfRect(0, y - 1, 1, y) : null);
    }

    private void LeftPaneTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        bool outline = sender.SelectedItem == OutlineTab;
        OutlineTree.Visibility = outline ? Visibility.Visible : Visibility.Collapsed;
        ThumbList.Visibility = outline ? Visibility.Collapsed : Visibility.Visible;
        if (!outline) SyncThumbnail();
    }

    // Author restrictions ---------------------------------------------------------------------

    /// <summary>
    /// PDFs can carry the author's restrictions (no printing, copying or changes). Editing and copying
    /// tools are turned off in the original; printing asks first; "Remove restrictions" saves a
    /// copy without them.
    /// </summary>
    private void ApplyPermissions()
    {
        var blocked = new List<string>();
        if (!_permissions.CanPrint) blocked.Add("printing");
        if (!_permissions.CanCopy) blocked.Add("copying text");
        if (!_permissions.CanModify) blocked.Add("changes");
        foreach (var tool in (ToggleButton[])[EditTextTool, AddTextTool, SignTool]) tool.IsEnabled = _permissions.CanModify;
        if (blocked.Count == 0) return;
        PermissionBar.Message = $"The author restricted {string.Join(", ", blocked)} for this PDF. You can print anyway, or save an unrestricted copy.";
        PermissionBar.IsOpen = true;
    }

    private async void RemoveRestrictions_Click(object sender, RoutedEventArgs e) => await RemovePasswordAsync();

    private bool Permitted(bool allowed, string action)
    {
        if (allowed) return true;
        ShowMessage("Not allowed for this PDF", $"The author of this PDF does not allow {action}.", InfoBarSeverity.Warning, autoHide: true);
        return false;
    }

    // Accessibility ---------------------------------------------------------------------------

    /// <summary>Gives icon-only buttons a name for screen readers, taken from their tooltip.</summary>
    private static void ApplyAutomationNames(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ButtonBase button && ToolTipService.GetToolTip(button) is string tip
                && string.IsNullOrEmpty(AutomationProperties.GetName(button)))
            {
                AutomationProperties.SetName(button, tip);
            }
            ApplyAutomationNames(child);
        }
    }

    public int PageCount => _pages.Count;

    /// <summary>Scrolls through every page at the given zoom (used by the CI memory check).</summary>
    internal async Task TourAsync(double zoom, int delayMs)
    {
        SetZoom(zoom);
        for (int i = 0; i < _pages.Count && !_disposed; i++)
        {
            GoToPage(i);
            await Task.Delay(delayMs);
        }
    }
}
