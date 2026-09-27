using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VisyaDocs.App.Services;

public sealed record ExportOptions(string PageRange, int Dpi);

/// <summary>Small content dialogs, themed to match the window.</summary>
public static class Dialogs
{
    public static ContentDialog Create(XamlRoot root, string title, object content, string primary, string? close = "Cancel")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Primary,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        // Dialogs live in a popup layer, so pass the window's effective theme on explicitly.
        if (root.Content is FrameworkElement fe) dialog.RequestedTheme = fe.ActualTheme;
        return dialog;
    }

    public static async Task ShowMessageAsync(XamlRoot root, string title, string message)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        await Create(root, title, text, "OK", null).ShowAsync();
    }

    /// <summary>Asks whether to save changes. Returns Primary (save), Secondary (discard) or None (cancel).</summary>
    public static async Task<ContentDialogResult> ConfirmSaveAsync(XamlRoot root, string documentName)
    {
        var dialog = Create(root, "Save changes?", new TextBlock
        {
            Text = $"\"{documentName}\" has unsaved changes. Save them before closing?",
            TextWrapping = TextWrapping.Wrap,
        }, "Save");
        dialog.SecondaryButtonText = "Don't save";
        return await dialog.ShowAsync();
    }

    public static async Task<string?> AskPasswordAsync(XamlRoot root, string fileName, bool retry)
    {
        var box = new PasswordBox { PlaceholderText = "Password" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = retry ? "The password was not correct. Try again." : $"\"{fileName}\" is password protected.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);
        var dialog = Create(root, "Password required", panel, "Open");
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
    }

    public static async Task<string?> AskTextAsync(XamlRoot root, string title, string initial, string primary, bool multiline = true)
    {
        var box = new TextBox
        {
            Text = initial,
            AcceptsReturn = multiline,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = multiline ? 100 : 0,
            MinWidth = 320,
        };
        var dialog = Create(root, title, box, primary);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    public static async Task<ExportOptions?> AskExportOptionsAsync(XamlRoot root, string title, int pageCount, bool images)
    {
        var range = new TextBox
        {
            Header = "Pages",
            PlaceholderText = $"All pages (1-{pageCount}), or for example 1-3, 5",
            MinWidth = 320,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(range);
        var dpi = new ComboBox { Header = "Resolution" };
        foreach (string label in (string[])["96 DPI (screen)", "150 DPI", "300 DPI (print)"]) dpi.Items.Add(label);
        dpi.SelectedIndex = 1;
        if (images) panel.Children.Add(dpi);
        var error = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);

        var dialog = Create(root, title, panel, "Export");
        dialog.PrimaryButtonClick += (_, e) =>
        {
            try
            {
                Core.Export.Converter.ParsePageRange(range.Text, pageCount);
            }
            catch (FormatException ex)
            {
                error.Text = ex.Message;
                e.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return new ExportOptions(range.Text, dpi.SelectedIndex switch { 0 => 96, 2 => 300, _ => 150 });
    }
}
