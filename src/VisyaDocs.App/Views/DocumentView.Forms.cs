using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VisyaDocs.Core;

namespace VisyaDocs.App.Views;

// Filling interactive PDF forms: native controls laid over the form fields of visible pages.
public sealed partial class DocumentView
{
    private const string FormLayer = "form";
    private static readonly SolidColorBrush FieldBackground = new(ColorHelper.FromArgb(0xFF, 0xF3, 0xF8, 0xFF));
    private static readonly SolidColorBrush FieldBorder = new(ColorHelper.FromArgb(0x99, 0x2B, 0x6F, 0xE0));
    private static readonly SolidColorBrush FieldHover = new(ColorHelper.FromArgb(0x33, 0x2B, 0x6F, 0xE0));

    private void RefreshFormOverlays()
    {
        foreach (var page in _pages) ShowFormOverlays(page);
    }

    private void ShowFormOverlays(PageView page)
    {
        // Keep the field being typed in: re-rendering after a commit must not steal focus or text.
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as TextBox;
        var focusedField = focused?.Tag as FormField;
        bool keepFocus = focusedField is not null && focusedField.PageIndex == page.Index;
        string? typed = keepFocus ? focused!.Text : null;
        int caret = keepFocus ? focused!.SelectionStart : 0;

        page.ClearHotspots(FormLayer);
        if (_tool != EditTool.FillForm) return;

        foreach (var field in page.FormFields)
        {
            if (field.ReadOnly && field.Kind != FormFieldKind.Signature) continue;
            FrameworkElement? element = field.Kind switch
            {
                FormFieldKind.Text => CreateTextField(page, field),
                FormFieldKind.CheckBox or FormFieldKind.RadioButton => CreateToggleField(page, field),
                FormFieldKind.ComboBox or FormFieldKind.ListBox => CreateChoiceField(page, field),
                FormFieldKind.Signature => CreateSignatureField(page, field),
                _ => null,
            };
            if (element is null) continue;
            page.AddHotspot(element, field.Bounds, FormLayer);

            if (keepFocus && element is TextBox box && field.AnnotIndex == focusedField!.AnnotIndex)
            {
                box.Text = typed!;
                box.Loaded += (_, _) =>
                {
                    box.Focus(FocusState.Programmatic);
                    box.SelectionStart = Math.Min(caret, box.Text.Length);
                };
            }
        }
    }

    private double FieldFontSize(PageView page, FormField field) =>
        Math.Clamp(field.Bounds.Height * 0.62, 6, 12) * page.DipPerPoint;

    private TextBox CreateTextField(PageView page, FormField field)
    {
        var box = new TextBox
        {
            Tag = field,
            Text = field.Value,
            FontSize = FieldFontSize(page, field),
            AcceptsReturn = field.Multiline,
            TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Padding = new Thickness(3, 0, 3, 0),
            MinHeight = 0,
            MinWidth = 0,
            BorderThickness = new Thickness(1),
            BorderBrush = FieldBorder,
            Background = FieldBackground,
            Foreground = new SolidColorBrush(Colors.Black),
            RequestedTheme = ElementTheme.Light,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(box, FieldLabel(field));
        box.LostFocus += async (_, _) =>
        {
            if (box.Text == field.Value) return;
            string text = box.Text;
            await EditAsync("Could not fill the field", () => _doc.SetFieldText(page.Index, field.AnnotIndex, text));
        };
        box.PointerPressed += (_, e) => e.Handled = true;
        return box;
    }

    private Border CreateToggleField(PageView page, FormField field)
    {
        var area = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = FieldBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(field.Kind == FormFieldKind.RadioButton ? 100 : 2),
        };
        ToolTipService.SetToolTip(area, FieldLabel(field));
        area.PointerEntered += (_, _) => area.Background = FieldHover;
        area.PointerExited += (_, _) => area.Background = new SolidColorBrush(Colors.Transparent);
        area.PointerPressed += (_, e) => e.Handled = true;
        area.Tapped += async (_, e) =>
        {
            e.Handled = true;
            await EditAsync("Could not change the field", () => _doc.ToggleCheck(page.Index, field.AnnotIndex));
        };
        return area;
    }

    private ComboBox CreateChoiceField(PageView page, FormField field)
    {
        var combo = new ComboBox
        {
            FontSize = FieldFontSize(page, field),
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(4, 0, 0, 0),
            BorderBrush = FieldBorder,
            Background = FieldBackground,
            RequestedTheme = ElementTheme.Light,
        };
        foreach (var option in field.Options) combo.Items.Add(option);
        combo.SelectedIndex = field.SelectedIndex;
        ToolTipService.SetToolTip(combo, FieldLabel(field));
        combo.SelectionChanged += async (_, _) =>
        {
            int index = combo.SelectedIndex;
            if (index < 0 || index == field.SelectedIndex) return;
            await EditAsync("Could not change the field", () => _doc.SelectOption(page.Index, field.AnnotIndex, index));
        };
        combo.PointerPressed += (_, e) => e.Handled = true;
        return combo;
    }

    private Button CreateSignatureField(PageView page, FormField field)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new AppIcon { Icon = "sign", Size = 16 });
        content.Children.Add(new TextBlock { Text = "Sign here", Foreground = new SolidColorBrush(Colors.Black) });
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(0),
            Background = FieldBackground,
            BorderBrush = FieldBorder,
            RequestedTheme = ElementTheme.Light,
        };
        button.Click += async (_, _) => await SignIntoAsync(page, field.Bounds);
        return button;
    }

    private static string FieldLabel(FormField field) =>
        (field.Name.Length > 0 ? field.Name : "Field") + (field.Required ? " (required)" : "");
}
