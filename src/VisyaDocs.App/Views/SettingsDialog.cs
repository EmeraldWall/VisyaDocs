using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VisyaDocs.App.Services;
using VisyaDocs.Platform;

namespace VisyaDocs.App.Views;

/// <summary>Settings: theme, page dimming, OCR language and comment author.</summary>
public static class SettingsDialog
{
    public static async Task ShowAsync(XamlRoot root)
    {
        var settings = AppSettings.Current;
        var panel = new StackPanel { Spacing = 16, MinWidth = 360 };

        var theme = new RadioButtons { Header = "Theme" };
        theme.Items.Add("System (follow Windows)");
        theme.Items.Add("Light (soft grey, easy on the eyes)");
        theme.Items.Add("Dark (black and dark grey)");
        theme.SelectedIndex = (int)settings.Theme;
        panel.Children.Add(theme);

        var dim = new ToggleSwitch
        {
            Header = "Dim pages in the dark theme",
            IsOn = settings.DimPagesInDark,
            OnContent = "On",
            OffContent = "Off",
        };
        panel.Children.Add(dim);

        var languages = OcrService.AvailableLanguages;
        var ocr = new ComboBox { Header = "Text recognition (OCR) language", MinWidth = 320 };
        ocr.Items.Add("Windows display language");
        foreach (var language in languages) ocr.Items.Add(language.DisplayName);
        int selected = languages.ToList().FindIndex(l => l.Tag == settings.OcrLanguage);
        ocr.SelectedIndex = selected + 1;
        panel.Children.Add(ocr);
        if (languages.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No OCR language is installed. Add a language with \"Optical character recognition\" in Windows Settings > Time & language.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            });
        }

        var author = new TextBox { Header = "Author name for comments", Text = settings.Author };
        panel.Children.Add(author);

        panel.Children.Add(new TextBlock
        {
            Text = $"VisyaDocs {typeof(SettingsDialog).Assembly.GetName().Version?.ToString(3)}. PDF engine: PDFium. OCR: Windows.",
            Opacity = 0.65,
            FontSize = 12,
        });

        var dialog = Dialogs.Create(root, "Settings", new ScrollViewer { Content = panel }, "Save");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        settings.Theme = (AppTheme)Math.Max(0, theme.SelectedIndex);
        settings.DimPagesInDark = dim.IsOn;
        settings.OcrLanguage = ocr.SelectedIndex > 0 ? languages[ocr.SelectedIndex - 1].Tag : null;
        settings.Author = author.Text.Trim();
        settings.Save();
    }
}
