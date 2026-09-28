using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace VisyaDocs.App.Services;

/// <summary>
/// Small, quick animations for panels and pages (fade and slide), run by the compositor so they stay
/// smooth while the app is busy. They are skipped when "Animation effects" is off in Windows settings.
/// </summary>
internal static class Motion
{
    private static readonly Windows.UI.ViewManagement.UISettings s_settings = new();

    /// <summary>False when the user turned animations off in Windows (Accessibility > Visual effects).</summary>
    public static bool Enabled
    {
        get
        {
            try
            {
                return s_settings.AnimationsEnabled;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    private static CompositionEasingFunction EaseOut(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

    /// <summary>
    /// Fades (and optionally slides) the element in whenever it becomes visible, and out when it is
    /// collapsed. The slide starts at the given offset. The element's own Z translation (used for
    /// its shadow) is kept.
    /// </summary>
    public static void ShowHide(UIElement element, float dx = 0, float dy = 0, int showMs = 180, int hideMs = 110)
    {
        if (!Enabled) return;
        var c = ElementCompositionPreview.GetElementVisual(element).Compositor;
        float z = element.Translation.Z;
        var rest = new Vector3(0, 0, z);
        var start = new Vector3(dx, dy, z);
        bool slide = dx != 0 || dy != 0;
        if (slide) ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        var show = c.CreateAnimationGroup();
        show.Add(Scalar(c, "Opacity", 0, 1, showMs));
        if (slide) show.Add(Vector(c, "Translation", start, rest, showMs));
        ElementCompositionPreview.SetImplicitShowAnimation(element, show);

        if (hideMs <= 0) return;
        var hide = c.CreateAnimationGroup();
        hide.Add(Scalar(c, "Opacity", 1, 0, hideMs));
        if (slide) hide.Add(Vector(c, "Translation", rest, new Vector3(dx / 2, dy / 2, z), hideMs));
        ElementCompositionPreview.SetImplicitHideAnimation(element, hide);
    }

    /// <summary>Slides an element in from a horizontal offset while fading it up (page flips).</summary>
    public static void SlideIn(UIElement element, float dx, int ms = 200)
    {
        if (!Enabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        float z = element.Translation.Z;
        visual.StartAnimation("Translation", Vector(c, "Translation", new Vector3(dx, 0, z), new Vector3(0, 0, z), ms));
        visual.StartAnimation("Opacity", Scalar(c, "Opacity", 0.35f, 1, ms));
    }

    /// <summary>Fades an element in once (for example a page bitmap that just arrived).</summary>
    public static void FadeIn(UIElement element, int ms = 160)
    {
        if (!Enabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StartAnimation("Opacity", Scalar(visual.Compositor, "Opacity", 0, (float)element.Opacity, ms));
    }

    private static ScalarKeyFrameAnimation Scalar(Compositor c, string target, float from, float to, int ms)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        a.Target = target;
        a.InsertKeyFrame(0, from);
        a.InsertKeyFrame(1, to, EaseOut(c));
        a.Duration = TimeSpan.FromMilliseconds(ms);
        return a;
    }

    private static Vector3KeyFrameAnimation Vector(Compositor c, string target, Vector3 from, Vector3 to, int ms)
    {
        var a = c.CreateVector3KeyFrameAnimation();
        a.Target = target;
        a.InsertKeyFrame(0, from);
        a.InsertKeyFrame(1, to, EaseOut(c));
        a.Duration = TimeSpan.FromMilliseconds(ms);
        return a;
    }
}
