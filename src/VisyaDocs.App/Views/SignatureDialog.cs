using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using VisyaDocs.App.Services;
using VisyaDocs.Core.Export;
using VisyaDocs.Platform;
using Windows.Foundation;
using Path = System.IO.Path;

namespace VisyaDocs.App.Views;

/// <summary>A signature image with transparent background.</summary>
/// <param name="Straight">Top-down BGRA, straight alpha (what the PDF gets).</param>
/// <param name="Premultiplied">Top-down BGRA, premultiplied alpha (what XAML shows).</param>
public sealed record SignatureImage(byte[] Straight, byte[] Premultiplied, int Width, int Height, bool AddDate)
{
    public WriteableBitmap ToBitmap()
    {
        var bitmap = new WriteableBitmap(Width, Height);
        Premultiplied.CopyTo(bitmap.PixelBuffer);
        bitmap.Invalidate();
        return bitmap;
    }
}

/// <summary>Create a signature by drawing, typing or picking an image, or reuse a saved one.</summary>
public static class SignatureDialog
{
    private const int MaxSaved = 3;
    private static readonly string[] ScriptFonts = ["Segoe Script", "Ink Free", "Lucida Handwriting", "Brush Script MT", "Segoe Print"];

    public static async Task<SignatureImage?> ShowAsync(XamlRoot root)
    {
        SignatureImage? result = null;
        var pen = new SolidColorBrush(ColorHelper.FromArgb(255, 20, 30, 80));

        // Draw ---------------------------------------------------------------------------------
        var inkCanvas = new Canvas { Background = new SolidColorBrush(Colors.Transparent), Width = 460, Height = 170 };
        Polyline? stroke = null;
        inkCanvas.PointerPressed += (_, e) =>
        {
            inkCanvas.CapturePointer(e.Pointer);
            stroke = new Polyline
            {
                Stroke = pen,
                StrokeThickness = 2.6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            stroke.Points.Add(e.GetCurrentPoint(inkCanvas).Position);
            inkCanvas.Children.Add(stroke);
        };
        inkCanvas.PointerMoved += (_, e) =>
        {
            if (stroke is null) return;
            foreach (var p in e.GetIntermediatePoints(inkCanvas).Reverse()) stroke.Points.Add(p.Position);
        };
        inkCanvas.PointerReleased += (_, e) =>
        {
            stroke = null;
            inkCanvas.ReleasePointerCapture(e.Pointer);
        };
        var clear = new HyperlinkButton { Content = "Clear" };
        clear.Click += (_, _) => inkCanvas.Children.Clear();
        var drawPanel = new StackPanel { Spacing = 6 };
        drawPanel.Children.Add(Paper(inkCanvas));
        drawPanel.Children.Add(Row(new TextBlock { Text = "Sign with mouse, pen or finger.", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center }, clear));

        // Type ---------------------------------------------------------------------------------
        var typedPreview = new TextBlock
        {
            Text = AppSettings.Current.Author,
            FontSize = 44,
            Foreground = pen,
            FontFamily = new FontFamily(ScriptFonts[0]),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var nameBox = new TextBox { Header = "Your name", Text = AppSettings.Current.Author };
        nameBox.TextChanged += (_, _) => typedPreview.Text = nameBox.Text;
        var fontBox = new ComboBox { Header = "Style", MinWidth = 200 };
        foreach (var font in ScriptFonts) fontBox.Items.Add(font);
        fontBox.SelectedIndex = 0;
        fontBox.SelectionChanged += (_, _) => typedPreview.FontFamily = new FontFamily((string)fontBox.SelectedItem);
        var typePanel = new StackPanel { Spacing = 8 };
        typePanel.Children.Add(Paper(new Grid { Width = 460, Height = 120, Children = { typedPreview } }));
        typePanel.Children.Add(Row(nameBox, fontBox));

        // Image --------------------------------------------------------------------------------
        byte[]? pickedStraight = null, pickedPremultiplied = null;
        int pickedW = 0, pickedH = 0;
        var pickedPreview = new Image { Height = 120, Stretch = Stretch.Uniform };
        var pick = new Button { Content = "Choose image..." };
        pick.Click += async (_, _) =>
        {
            var path = await Pickers.OpenFileAsync(".png", ".jpg", ".jpeg", ".bmp");
            if (path is null) return;
            var image = await ImageTools.DecodeAsync(path);
            (pickedPremultiplied, pickedStraight) = RemovePaper(image.Bgra);
            (pickedW, pickedH) = (image.Width, image.Height);
            pickedPreview.Source = new SignatureImage(pickedStraight, pickedPremultiplied, pickedW, pickedH, false).ToBitmap();
        };
        var imagePanel = new StackPanel { Spacing = 8 };
        imagePanel.Children.Add(Paper(new Grid { Width = 460, Height = 130, Children = { pickedPreview } }));
        imagePanel.Children.Add(Row(pick, new TextBlock { Text = "White paper around the signature becomes transparent.", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 }));

        // Tabs ---------------------------------------------------------------------------------
        var tabs = new SelectorBar();
        var drawTab = new SelectorBarItem { Text = "Draw", IsSelected = true };
        var typeTab = new SelectorBarItem { Text = "Type" };
        var imageTab = new SelectorBarItem { Text = "Image" };
        tabs.Items.Add(drawTab);
        tabs.Items.Add(typeTab);
        tabs.Items.Add(imageTab);
        var body = new Grid { MinHeight = 230 };
        body.Children.Add(drawPanel);
        body.Children.Add(typePanel);
        body.Children.Add(imagePanel);
        void ShowTab()
        {
            drawPanel.Visibility = tabs.SelectedItem == drawTab ? Visibility.Visible : Visibility.Collapsed;
            typePanel.Visibility = tabs.SelectedItem == typeTab ? Visibility.Visible : Visibility.Collapsed;
            imagePanel.Visibility = tabs.SelectedItem == imageTab ? Visibility.Visible : Visibility.Collapsed;
        }
        tabs.SelectionChanged += (_, _) => ShowTab();
        ShowTab();

        var remember = new CheckBox { Content = "Remember this signature", IsChecked = true };
        var addDate = new CheckBox { Content = "Add today's date next to it" };
        var colorBlue = new RadioButton { Content = "Blue ink", IsChecked = true, GroupName = "ink" };
        var colorBlack = new RadioButton { Content = "Black ink", GroupName = "ink" };
        colorBlue.Checked += (_, _) => SetInk(ColorHelper.FromArgb(255, 20, 30, 80));
        colorBlack.Checked += (_, _) => SetInk(Colors.Black);
        void SetInk(Windows.UI.Color color)
        {
            pen.Color = color;
        }

        var panel = new StackPanel { Spacing = 10, MinWidth = 480 };
        ContentDialog? dialog = null;

        // Saved signatures: one click to reuse.
        var saved = await LoadSavedAsync();
        if (saved.Count > 0)
        {
            var savedRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var signature in saved)
            {
                var button = new Button
                {
                    Content = new Image { Source = signature.ToBitmap(), Height = 44, MaxWidth = 150, Stretch = Stretch.Uniform },
                    Background = new SolidColorBrush(Colors.White),
                    Padding = new Thickness(6),
                };
                ToolTipService.SetToolTip(button, "Use this signature");
                button.Click += (_, _) =>
                {
                    result = signature with { AddDate = addDate.IsChecked == true };
                    dialog?.Hide();
                };
                savedRow.Children.Add(button);
            }
            panel.Children.Add(new TextBlock { Text = "Saved signatures", Opacity = 0.7 });
            panel.Children.Add(new ScrollViewer { Content = savedRow, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled });
        }
        panel.Children.Add(tabs);
        panel.Children.Add(body);
        panel.Children.Add(Row(colorBlue, colorBlack));
        panel.Children.Add(Row(remember, addDate));

        dialog = Dialogs.Create(root, "Signature", new ScrollViewer { Content = panel }, "Place signature");
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                SignatureImage? made = null;
                if (tabs.SelectedItem == drawTab && inkCanvas.Children.Count > 0) made = await RenderAsync(inkCanvas);
                else if (tabs.SelectedItem == typeTab && typedPreview.Text.Trim().Length > 0) made = await RenderAsync(typedPreview);
                else if (tabs.SelectedItem == imageTab && pickedStraight is not null)
                    made = new SignatureImage(pickedStraight, pickedPremultiplied!, pickedW, pickedH, false);

                if (made is null)
                {
                    e.Cancel = true;
                    return;
                }
                result = made with { AddDate = addDate.IsChecked == true };
                if (remember.IsChecked == true) Save(result);
            }
            finally
            {
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
        return result;
    }

    private static Border Paper(UIElement content) => new()
    {
        Child = content,
        Background = new SolidColorBrush(Colors.White),
        BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 200, 200, 200)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    /// <summary>Renders an element (transparent background) and crops it to the drawn pixels.</summary>
    private static async Task<SignatureImage?> RenderAsync(UIElement element)
    {
        var target = new RenderTargetBitmap();
        await target.RenderAsync(element);
        byte[] pixels = (await target.GetPixelsAsync()).ToArray();
        int w = target.PixelWidth, h = target.PixelHeight;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (pixels[(y * w + x) * 4 + 3] > 8)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        if (maxX < 0) return null;
        const int margin = 4;
        minX = Math.Max(0, minX - margin); minY = Math.Max(0, minY - margin);
        maxX = Math.Min(w - 1, maxX + margin); maxY = Math.Min(h - 1, maxY + margin);
        int cw = maxX - minX + 1, ch = maxY - minY + 1;
        var premultiplied = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++)
            Array.Copy(pixels, ((minY + y) * w + minX) * 4, premultiplied, y * cw * 4, cw * 4);
        return new SignatureImage(Unpremultiply(premultiplied), premultiplied, cw, ch, false);
    }

    /// <summary>Makes near-white paper transparent, softly, so pen edges stay smooth.</summary>
    private static (byte[] Premultiplied, byte[] Straight) RemovePaper(byte[] bgra)
    {
        var straight = new byte[bgra.Length];
        var premultiplied = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            int lightness = Math.Min(r, Math.Min(g, b));
            int alpha = Math.Clamp((250 - lightness) * 255 / 70, 0, 255);   // >= 250 clear, <= 180 solid
            straight[i] = b; straight[i + 1] = g; straight[i + 2] = r; straight[i + 3] = (byte)alpha;
            premultiplied[i] = (byte)(b * alpha / 255);
            premultiplied[i + 1] = (byte)(g * alpha / 255);
            premultiplied[i + 2] = (byte)(r * alpha / 255);
            premultiplied[i + 3] = (byte)alpha;
        }
        return (premultiplied, straight);
    }

    private static byte[] Unpremultiply(byte[] premultiplied)
    {
        var straight = new byte[premultiplied.Length];
        for (int i = 0; i < premultiplied.Length; i += 4)
        {
            int a = premultiplied[i + 3];
            straight[i + 3] = (byte)a;
            if (a == 0) continue;
            straight[i] = (byte)Math.Min(255, premultiplied[i] * 255 / a);
            straight[i + 1] = (byte)Math.Min(255, premultiplied[i + 1] * 255 / a);
            straight[i + 2] = (byte)Math.Min(255, premultiplied[i + 2] * 255 / a);
        }
        return straight;
    }

    private static void Save(SignatureImage signature)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SignaturesFolder);
            string path = Path.Combine(AppSettings.SignaturesFolder, $"signature-{DateTime.Now:yyyyMMddHHmmss}.png");
            using (var file = File.Create(path)) PngEncoder.Write(file, signature.Straight, signature.Width, signature.Height, alpha: true);
            foreach (var old in new DirectoryInfo(AppSettings.SignaturesFolder).GetFiles("signature-*.png")
                         .OrderByDescending(f => f.Name).Skip(MaxSaved))
                old.Delete();
        }
        catch (IOException)
        {
            // Remembering is a convenience; the signature is still placed.
        }
    }

    private static async Task<List<SignatureImage>> LoadSavedAsync()
    {
        var list = new List<SignatureImage>();
        if (!Directory.Exists(AppSettings.SignaturesFolder)) return list;
        foreach (var file in Directory.GetFiles(AppSettings.SignaturesFolder, "signature-*.png").OrderByDescending(f => f).Take(MaxSaved))
        {
            try
            {
                var image = await ImageTools.DecodeAsync(file);
                list.Add(new SignatureImage(Unpremultiply(image.Bgra), image.Bgra, image.Width, image.Height, false));
            }
            catch (Exception e) when (e is IOException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
            }
        }
        return list;
    }
}
