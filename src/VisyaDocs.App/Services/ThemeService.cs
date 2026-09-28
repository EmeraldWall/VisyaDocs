using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace VisyaDocs.App.Services;

/// <summary>
/// Applies the System / Light / Dark / Black choice. Black is the Dark theme with the pure black
/// palette (Themes/PaletteBlack.xaml) merged over the dark grey one.
/// </summary>
public static class ThemeService
{
    private static ResourceDictionary? s_blackPalette;

    public static bool IsBlack => AppSettings.Current.Theme == AppTheme.Black;

    public static ElementTheme ToElementTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark or AppTheme.Black => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static void Apply(Window window)
    {
        if (window.Content is not FrameworkElement root) return;
        bool paletteChanged = SetBlackPalette(IsBlack);
        var target = ToElementTheme(AppSettings.Current.Theme);
        if (paletteChanged)
        {
            // Theme resources are re-read only when the theme changes, so flip it once.
            root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        }
        root.RequestedTheme = target;
        UpdateCaptionButtons(window.AppWindow, root.ActualTheme);
    }

    private static bool SetBlackPalette(bool black)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        bool present = s_blackPalette is not null && merged.Contains(s_blackPalette);
        if (black == present) return false;
        if (black)
        {
            s_blackPalette ??= new ResourceDictionary { Source = new Uri("ms-appx:///Themes/PaletteBlack.xaml") };
            merged.Add(s_blackPalette);
        }
        else
        {
            merged.Remove(s_blackPalette!);
        }
        return true;
    }

    /// <summary>Background of the selected tab: matches the page area of the active palette.</summary>
    public static Color SelectedTabColor(ElementTheme actual) =>
        actual != ElementTheme.Dark ? ColorHelper.FromArgb(255, 250, 250, 250)
        : IsBlack ? ColorHelper.FromArgb(255, 26, 26, 26)
        : ColorHelper.FromArgb(255, 45, 45, 45);

    public static void UpdateCaptionButtons(AppWindow appWindow, ElementTheme theme)
    {
        var bar = appWindow.TitleBar;
        bool dark = theme == ElementTheme.Dark;
        var foreground = dark ? ColorHelper.FromArgb(255, 235, 235, 235) : ColorHelper.FromArgb(255, 30, 30, 30);
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = dark ? ColorHelper.FromArgb(255, 120, 120, 120) : ColorHelper.FromArgb(255, 140, 140, 140);
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonHoverBackgroundColor = !dark ? ColorHelper.FromArgb(255, 212, 212, 212)
            : IsBlack ? ColorHelper.FromArgb(255, 38, 38, 38) : ColorHelper.FromArgb(255, 56, 56, 56);
        bar.ButtonPressedForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = !dark ? ColorHelper.FromArgb(255, 196, 196, 196)
            : IsBlack ? ColorHelper.FromArgb(255, 50, 50, 50) : ColorHelper.FromArgb(255, 68, 68, 68);
    }
}
