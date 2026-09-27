using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace VisyaDocs.App.Services;

/// <summary>Applies the System / Light / Dark choice to the window, including the caption buttons.</summary>
public static class ThemeService
{
    public static ElementTheme ToElementTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static void Apply(Window window)
    {
        if (window.Content is not FrameworkElement root) return;
        root.RequestedTheme = ToElementTheme(AppSettings.Current.Theme);
        UpdateCaptionButtons(window.AppWindow, root.ActualTheme);
    }

    public static void UpdateCaptionButtons(AppWindow appWindow, ElementTheme theme)
    {
        var bar = appWindow.TitleBar;
        bool dark = theme == ElementTheme.Dark;
        var foreground = dark ? ColorHelper.FromArgb(255, 230, 230, 230) : ColorHelper.FromArgb(255, 30, 30, 30);
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = dark ? ColorHelper.FromArgb(255, 120, 120, 120) : ColorHelper.FromArgb(255, 140, 140, 140);
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonHoverBackgroundColor = dark ? ColorHelper.FromArgb(255, 46, 46, 46) : ColorHelper.FromArgb(255, 212, 212, 212);
        bar.ButtonPressedForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = dark ? ColorHelper.FromArgb(255, 58, 58, 58) : ColorHelper.FromArgb(255, 196, 196, 196);
    }
}
