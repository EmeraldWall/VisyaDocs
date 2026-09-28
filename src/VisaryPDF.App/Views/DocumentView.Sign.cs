using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VisaryPDF.Core;
using Windows.Foundation;

namespace VisaryPDF.App.Views;

// E-signing: create a signature, then place, move and resize it on the page before committing.
public sealed partial class DocumentView
{
    private const string SignLayer = "sign";
    private const double DefaultSignatureWidthPt = 160;

    private SignatureImage? _pendingSignature;
    private Grid? _signPreview;
    private PageView? _signPage;
    private ViewRect _signRect;

    private async void Sign_Click(object sender, RoutedEventArgs e)
    {
        SignTool.IsChecked = _tool == EditTool.PlaceSignature;
        if (!Permitted(_permissions.CanModify, "changes")) return;
        var signature = await SignatureDialog.ShowAsync(XamlRoot);
        if (signature is null) return;
        _pendingSignature = signature;
        SetTool(EditTool.PlaceSignature);
        ShowMessage("Place your signature", "Click where it should go. Drag to move, drag the corner to resize, then press the check mark.",
            InfoBarSeverity.Informational, autoHide: true);
    }

    /// <summary>Signs into a signature form field: the signature fits the field box.</summary>
    private async Task SignIntoAsync(PageView page, PdfRect field)
    {
        var signature = await SignatureDialog.ShowAsync(XamlRoot);
        if (signature is null) return;
        // Fit the signature into the field as it appears on screen, keeping its proportions.
        var box = page.Geometry.ToView(field);
        double aspect = (double)signature.Height / signature.Width;
        double w = box.Width, h = w * aspect;
        if (h > box.Height)
        {
            h = box.Height;
            w = h / aspect;
        }
        var rect = new ViewRect(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
        await CommitSignatureAsync(page, signature, rect);
    }

    private void BeginSignaturePlacement(PageView page, Point dip)
    {
        if (_pendingSignature is not { } signature) return;
        CancelSignaturePlacement(keepPending: true);

        double width = DefaultSignatureWidthPt, height = width * signature.Height / signature.Width;
        var (x, y) = (dip.X / page.DipPerPoint, dip.Y / page.DipPerPoint);
        _signRect = new ViewRect(x - width / 2, y - height / 2, width, height);
        _signPage = page;

        var preview = new Grid
        {
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0xCC, 0x2B, 0x6F, 0xE0)),
            BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(ColorHelper.FromArgb(0x14, 0x2B, 0x6F, 0xE0)),
            ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY,
        };
        preview.Children.Add(new Image { Source = signature.ToBitmap(), Stretch = Stretch.Fill });

        var accept = SmallButton("", "Place signature (Enter)");
        var cancel = SmallButton("", "Cancel (Esc)");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -34, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        preview.Children.Add(buttons);

        var grip = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 0x2B, 0x6F, 0xE0)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -7, -7),
            ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY,
        };
        ToolTipService.SetToolTip(grip, "Resize");
        preview.Children.Add(grip);

        preview.PointerPressed += (_, e) => e.Handled = true;
        preview.ManipulationDelta += (_, e) =>
        {
            e.Handled = true;
            _signRect = _signRect with
            {
                X = _signRect.X + e.Delta.Translation.X / page.DipPerPoint,
                Y = _signRect.Y + e.Delta.Translation.Y / page.DipPerPoint,
            };
            page.UpdateHotspot(preview, _signRect);
        };
        grip.ManipulationDelta += (_, e) =>
        {
            e.Handled = true;
            double newWidth = Math.Max(30, _signRect.Width + e.Delta.Translation.X / page.DipPerPoint);
            _signRect = _signRect with { Width = newWidth, Height = newWidth * signature.Height / signature.Width };
            page.UpdateHotspot(preview, _signRect);
        };
        accept.Click += async (_, _) => await AcceptSignaturePlacementAsync();
        cancel.Click += (_, _) => SetTool(EditTool.Select);
        preview.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                await AcceptSignaturePlacementAsync();
            }
        };

        _signPreview = preview;
        page.AddHotspot(preview, _signRect, SignLayer);
    }

    private async Task AcceptSignaturePlacementAsync()
    {
        if (_signPage is not { } page || _pendingSignature is not { } signature) return;
        var rect = _signRect;
        CancelSignaturePlacement();
        SetTool(EditTool.Select);
        await CommitSignatureAsync(page, signature, rect);
    }

    private async Task CommitSignatureAsync(PageView page, SignatureImage signature, ViewRect rect)
    {
        await EditAsync("Could not place the signature", () =>
            _doc.AddImageStamp(page.Index, signature.Straight, signature.Width, signature.Height, rect));
        if (signature.AddDate)
        {
            string date = DateTime.Now.ToString("d", System.Globalization.CultureInfo.CurrentCulture);
            double size = Math.Clamp(rect.Height * 0.3, 7, 12);
            // Just below the signature as seen on screen (also on rotated pages).
            var (x, y) = page.Geometry.ToPage(rect.X, rect.Y + rect.Height + size * 1.1);
            await EditAsync("Could not add the date", () =>
                _doc.AddText(page.Index, x, y, date, new TextStyle(size, new PdfColor(40, 40, 40))));
        }
        ShowMessage("Signed", "The signature was added. Save to keep it.", InfoBarSeverity.Success, autoHide: true);
    }

    private void CancelSignaturePlacement(bool keepPending = false)
    {
        _signPage?.ClearHotspots(SignLayer);
        _signPage = null;
        _signPreview = null;
        if (!keepPending) _pendingSignature = null;
    }

    private static Button SmallButton(string glyph, string tip)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
        };
        ToolTipService.SetToolTip(button, tip);
        return button;
    }
}
