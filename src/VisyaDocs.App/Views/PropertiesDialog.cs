using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VisyaDocs.App.Services;
using VisyaDocs.Core;

namespace VisyaDocs.App.Views;

/// <summary>Read-only document properties: description and advanced details.</summary>
public static class PropertiesDialog
{
    public static async Task ShowAsync(XamlRoot root, PdfProperties p)
    {
        var description = Table(
            ("File", p.FilePath is null ? "Not saved yet" : Path.GetFileName(p.FilePath)),
            ("Location", p.FilePath is null ? "" : Path.GetDirectoryName(p.FilePath) ?? ""),
            ("Title", p.Title),
            ("Author", p.Author),
            ("Subject", p.Subject),
            ("Keywords", p.Keywords),
            ("Created", Date(p.Created)),
            ("Modified", Date(p.Modified)));

        var advanced = Table(
            ("Pages", p.PageCount.ToString(CultureInfo.CurrentCulture)),
            ("Page size", PageSize(p.PageWidth, p.PageHeight)),
            ("File size", p.FileSize is long size ? FileSize(size) : ""),
            ("PDF version", p.PdfVersion),
            ("Application", p.Creator),
            ("PDF producer", p.Producer),
            ("Form", p.HasForm ? "Yes (fillable)" : "No"),
            ("Security", p.Encrypted ? "Password protected or encrypted" : "None"),
            ("Printing", p.CanPrint ? "Allowed" : "Not allowed"),
            ("Copying text", p.CanCopy ? "Allowed" : "Not allowed"),
            ("Changing", p.CanModify ? "Allowed" : "Not allowed"));

        var tabs = new SelectorBar();
        var descriptionTab = new SelectorBarItem { Text = "Description", IsSelected = true };
        var advancedTab = new SelectorBarItem { Text = "Advanced" };
        tabs.Items.Add(descriptionTab);
        tabs.Items.Add(advancedTab);
        advanced.Visibility = Visibility.Collapsed;
        tabs.SelectionChanged += (_, _) =>
        {
            description.Visibility = tabs.SelectedItem == descriptionTab ? Visibility.Visible : Visibility.Collapsed;
            advanced.Visibility = tabs.SelectedItem == advancedTab ? Visibility.Visible : Visibility.Collapsed;
        };

        var body = new Grid { MinHeight = 300 };
        body.Children.Add(description);
        body.Children.Add(advanced);
        var panel = new StackPanel { Spacing = 12, MinWidth = 440 };
        panel.Children.Add(tabs);
        panel.Children.Add(body);
        await Dialogs.Create(root, "Document properties", new ScrollViewer { Content = panel }, "Close", null).ShowAsync();
    }

    private static Grid Table(params (string Label, string Value)[] rows)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int r = 0;
        foreach (var (label, value) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Opacity = 0.7 };
            var text = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(value) ? "-" : value,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontWeight = FontWeights.SemiBold,
            };
            Grid.SetRow(name, r);
            Grid.SetRow(text, r);
            Grid.SetColumn(text, 1);
            grid.Children.Add(name);
            grid.Children.Add(text);
            r++;
        }
        return grid;
    }

    private static string Date(DateTimeOffset? date) =>
        date is { } d ? d.ToLocalTime().ToString("f", CultureInfo.CurrentCulture) : "";

    private static string FileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.##} MB",
    };

    private static string PageSize(double w, double h)
    {
        if (w <= 0 || h <= 0) return "";
        string size = $"{w / 72 * 25.4:0} x {h / 72 * 25.4:0} mm ({w / 72:0.##} x {h / 72:0.##} in)";
        static bool Near(double a, double b) => Math.Abs(a - b) < 3;
        double s = Math.Min(w, h), l = Math.Max(w, h);
        string? name = Near(s, 595.3) && Near(l, 841.9) ? "A4" : Near(s, 612) && Near(l, 792) ? "Letter"
            : Near(s, 612) && Near(l, 1008) ? "Legal" : Near(s, 841.9) && Near(l, 1190.6) ? "A3" : Near(s, 419.5) && Near(l, 595.3) ? "A5" : null;
        return name is null ? size : $"{name}, {size}";
    }
}
