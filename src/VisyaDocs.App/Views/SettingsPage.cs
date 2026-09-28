using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VisyaDocs.App.Services;
using VisyaDocs.Platform;

namespace VisyaDocs.App.Views;

/// <summary>
/// Settings as a page inside the window (Windows 11 style cards). Every change applies at once.
/// </summary>
public sealed partial class SettingsPage : UserControl
{
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly StackPanel _signatures = new() { Spacing = 8 };

    public SettingsPage()
    {
        var content = new StackPanel { Spacing = 16, MaxWidth = 820, Margin = new Thickness(32, 20, 32, 40) };

        var back = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Style = (Style)Application.Current.Resources["TitleBarButtonStyle"],
        };
        ToolTipService.SetToolTip(back, "Back");
        back.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(back);
        header.Children.Add(new TextBlock { Text = "Settings", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        content.Children.Add(header);

        content.Children.Add(Section("Appearance"));
        content.Children.Add(Card("settings", "Theme", "Light is a soft grey that is easy on the eyes. Black saves power on OLED screens.", ThemeChooser()));
        var dim = new ToggleSwitch { IsOn = _settings.DimPagesInDark, OnContent = "On", OffContent = "Off" };
        dim.Toggled += (_, _) =>
        {
            _settings.DimPagesInDark = dim.IsOn;
            _settings.Save();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        };
        content.Children.Add(Card("view-single", "Dim pages in Dark and Black", "Lowers page brightness slightly for comfortable night reading. The file is not changed.", dim));

        content.Children.Add(Section("Reading"));
        var layout = new ComboBox { MinWidth = 220 };
        foreach (var name in (string[])["Continuous scroll", "Two pages side by side", "Two pages, cover alone", "Single page"]) layout.Items.Add(name);
        layout.SelectedIndex = (int)_settings.Layout;
        layout.SelectionChanged += (_, _) =>
        {
            _settings.Layout = (ViewLayout)Math.Max(0, layout.SelectedIndex);
            _settings.Save();
        };
        content.Children.Add(Card("view-continuous", "Page layout for new documents", "You can change the layout of an open document from the tool bar.", layout));
        var reset = new Button { Content = "Reset position" };
        reset.Click += (_, _) =>
        {
            _settings.RailSide = RailSide.Right;
            _settings.RailTop = 0;
            _settings.RailCollapsed = false;
            _settings.Save();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        };
        content.Children.Add(Card("select", "Tool bar", "Drag the tool bar by its handle to either side of the page area. Reset puts it back at the top right, open.", reset));

        var association = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        var associate = new Button { Content = FileAssociation.IsRegistered ? "Choose in Windows settings" : "Set up" };
        var status = new TextBlock { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
        associate.Click += async (_, _) =>
        {
            try
            {
                FileAssociation.Register();
                status.Text = "In the window that opens, choose VisyaDocs for .pdf.";
                associate.Content = "Choose in Windows settings";
                await FileAssociation.OpenDefaultAppsSettingsAsync();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                status.Text = "Windows did not allow the change: " + ex.Message;
            }
        };
        association.Children.Add(associate);
        association.Children.Add(status);
        content.Children.Add(Card("pdf", "Open PDFs with VisyaDocs",
            "Makes VisyaDocs available for PDF files in Windows. Windows then asks you to confirm it as the default app.", association));

        content.Children.Add(Section("Text recognition"));
        var languages = OcrService.AvailableLanguages;
        var ocr = new ComboBox { MinWidth = 220 };
        ocr.Items.Add("Windows display language");
        foreach (var language in languages) ocr.Items.Add(language.DisplayName);
        ocr.SelectedIndex = languages.ToList().FindIndex(l => l.Tag == _settings.OcrLanguage) + 1;
        ocr.SelectionChanged += (_, _) =>
        {
            _settings.OcrLanguage = ocr.SelectedIndex > 0 ? languages[ocr.SelectedIndex - 1].Tag : null;
            _settings.Save();
        };
        var addLanguage = new HyperlinkButton { Content = "Add a language in Windows", NavigateUri = new Uri("ms-settings:regionlanguage") };
        var ocrPanel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        ocrPanel.Children.Add(ocr);
        ocrPanel.Children.Add(addLanguage);
        content.Children.Add(Card("ocr", "OCR language",
            languages.Count == 0
                ? "No OCR language is installed. Add a language with \"Optical character recognition\" in Windows settings."
                : "Used to turn scanned pages into searchable text.", ocrPanel));

        content.Children.Add(Section("Signing and comments"));
        var author = new TextBox { Text = _settings.Author, MinWidth = 220, PlaceholderText = "Your name" };
        author.TextChanged += (_, _) =>
        {
            _settings.Author = author.Text.Trim();
            _settings.Save();
        };
        content.Children.Add(Card("comment", "Your name", "Shown as the author of your comments and highlights.", author));
        content.Children.Add(Card("sign", "Saved signatures", "Signatures you chose to remember. They are stored only on this PC.", _signatures));
        RefreshSignatures();

        content.Children.Add(Section("About"));
        var version = typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "";
        content.Children.Add(Card("properties", $"VisyaDocs {version}",
            "PDF engine: PDFium (BSD/Apache 2.0). Text recognition: Windows OCR. Icons: Microsoft Fluent UI System Icons (MIT).", null));

        Content = new ScrollViewer { Content = content };
    }

    /// <summary>Raised when the back button is pressed.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised after a change that open windows and documents must apply (theme, dimming, tool bar).</summary>
    public event EventHandler? SettingsChanged;

    private UIElement ThemeChooser()
    {
        var choices = new RadioButtons { MaxColumns = 4 };
        foreach (var name in (string[])["System", "Light", "Dark", "Black"]) choices.Items.Add(name);
        choices.SelectedIndex = (int)_settings.Theme;
        choices.SelectionChanged += (_, _) =>
        {
            if (choices.SelectedIndex < 0) return;
            _settings.Theme = (AppTheme)choices.SelectedIndex;
            _settings.Save();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        };
        return choices;
    }

    private void RefreshSignatures()
    {
        _signatures.Children.Clear();
        var files = Directory.Exists(AppSettings.SignaturesFolder)
            ? Directory.GetFiles(AppSettings.SignaturesFolder, "signature-*.png").OrderByDescending(f => f).ToArray()
            : [];
        if (files.Length == 0)
        {
            _signatures.Children.Add(new TextBlock { Text = "None yet", Opacity = 0.6 });
            return;
        }
        foreach (var file in files)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            row.Children.Add(new Border
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6),
                Child = new Image { Source = new BitmapImage(new Uri(file)), Height = 36, MaxWidth = 160 },
            });
            var delete = new Button { Content = "Delete" };
            delete.Click += (_, _) =>
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
                RefreshSignatures();
            };
            row.Children.Add(delete);
            _signatures.Children.Add(row);
        }
    }

    private static TextBlock Section(string title) => new()
    {
        Text = title,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 12, 0, 0),
    };

    /// <summary>A settings card: icon, title and description on the left, the control on the right.</summary>
    private static Border Card(string icon, string title, string description, UIElement? control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new AppIcon { Icon = icon, Size = 24, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = description, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(fe, 2);
            grid.Children.Add(fe);
        }
        return new Border
        {
            Child = grid,
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            // Translucent neutrals read well on every palette (Light, Dark, Black) without theme lookups.
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x14, 0x80, 0x80, 0x80)),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x33, 0x80, 0x80, 0x80)),
        };
    }
}
