using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using VisaryPDF.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using OcrWord = VisaryPDF.Core.OcrWord;

namespace VisaryPDF.Platform;

public sealed record OcrLanguage(string Tag, string DisplayName);

public sealed record OcrPageResult(int PageIndex, string Text, IReadOnlyList<OcrWord> Words);

public sealed class OcrUnavailableException(string message) : Exception(message);

/// <summary>
/// Text recognition with the OCR engine built into Windows. It uses the language packs the
/// user has installed, so VisaryPDF ships no OCR models of its own.
/// </summary>
public static class OcrService
{
    private const double TargetDpi = 300;

    public static IReadOnlyList<OcrLanguage> AvailableLanguages =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => new OcrLanguage(l.LanguageTag, l.DisplayName)).ToArray();

    public static bool IsAvailable => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    /// <summary>Recognizes the text of one page. <paramref name="languageTag"/> null means the user's profile languages.</summary>
    public static async Task<OcrPageResult> RecognizePageAsync(PdfDocument doc, int pageIndex, string? languageTag = null,
        CancellationToken ct = default)
    {
        var engine = CreateEngine(languageTag);
        var geometry = doc.GetGeometry(pageIndex);

        double scale = TargetDpi / 72;
        double maxSide = Math.Max(geometry.ViewWidth, geometry.ViewHeight) * scale;
        if (maxSide > OcrEngine.MaxImageDimension) scale *= OcrEngine.MaxImageDimension / maxSide;
        int width = Math.Max(1, (int)(geometry.ViewWidth * scale));
        int height = Math.Max(1, (int)(geometry.ViewHeight * scale));

        byte[] pixels = await Task.Run(() => doc.RenderPage(pageIndex, width, height, annotations: false), ct);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height,
            BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct);

        var words = new List<OcrWord>();
        var text = new StringBuilder();
        foreach (var line in result.Lines)
        {
            text.AppendLine(line.Text);
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                var view = new ViewRect(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale);
                words.Add(new OcrWord(word.Text, geometry.ToPage(view)));
            }
        }
        return new OcrPageResult(pageIndex, text.ToString().TrimEnd(), words);
    }

    /// <summary>
    /// Adds an invisible, searchable text layer to the given pages. Pages that already have
    /// real text are skipped. Returns the recognized text of all processed pages.
    /// </summary>
    public static async Task<string> MakeSearchableAsync(PdfDocument doc, IReadOnlyList<int> pages, string? languageTag = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        CreateEngine(languageTag);
        var all = new StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            int page = pages[i];
            if (await Task.Run(() => doc.IsLikelyScanned(page), ct))
            {
                var result = await RecognizePageAsync(doc, page, languageTag, ct);
                await Task.Run(() => doc.AddInvisibleTextLayer(page, result.Words), ct);
                if (all.Length > 0) all.AppendLine().AppendLine();
                all.Append(result.Text);
            }
            progress?.Report((i + 1.0) / pages.Count);
        }
        return all.ToString();
    }

    private static OcrEngine CreateEngine(string? languageTag)
    {
        var engine = languageTag is null
            ? OcrEngine.TryCreateFromUserProfileLanguages()
            : Language.IsWellFormed(languageTag) ? OcrEngine.TryCreateFromLanguage(new Language(languageTag)) : null;
        return engine ?? throw new OcrUnavailableException(
            "No text recognition language is installed. Add one in Windows Settings > Time & language > Language & region " +
            "(language options > Optical character recognition).");
    }
}
