using VisyaDocs.Core;
using VisyaDocs.Platform;
using Xunit;
using Xunit.Abstractions;

namespace VisyaDocs.Platform.Tests;

public class PlatformTests(ITestOutputHelper output)
{
    /// <summary>A "scanned" page: a text page rendered to pixels and stored as an image-only page.</summary>
    private static PdfDocument CreateScannedDocument()
    {
        using var source = PdfDocument.CreateEmpty();
        var white = Enumerable.Repeat((byte)255, 100 * 100 * 4).ToArray();
        source.AppendImagePage(white, 100, 100);
        source.AddText(0, 60, 480, "VISYADOCS OCR TEST", new TextStyle(40));
        source.AddText(0, 60, 400, "Scanned pages become searchable", new TextStyle(28));
        var g = source.GetGeometry(0);
        int w = (int)(g.ViewWidth * 200 / 72), h = (int)(g.ViewHeight * 200 / 72);
        byte[] pixels = source.RenderPage(0, w, h);

        var scanned = PdfDocument.CreateEmpty();
        scanned.AppendImagePage(pixels, w, h);
        return scanned;
    }

    [Fact]
    public async Task OcrRecognizesScannedPageAndMakesItSearchable()
    {
        if (!OcrService.IsAvailable)
        {
            output.WriteLine("No OCR language installed on this machine; skipping.");
            return;
        }
        using var doc = CreateScannedDocument();
        Assert.True(doc.IsLikelyScanned(0));

        var result = await OcrService.RecognizePageAsync(doc, 0);
        output.WriteLine(result.Text);
        Assert.Contains("TEST", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("searchable", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.All(result.Words, w => Assert.True(w.Bounds.Width > 0 && w.Bounds.Height > 0));

        string text = await OcrService.MakeSearchableAsync(doc, [0]);
        Assert.Contains("VISYADOCS", text, StringComparison.OrdinalIgnoreCase);
        using var reopened = PdfDocument.Load(doc.SaveToBytes());
        var hit = Assert.Single(reopened.Search("searchable"));
        // The invisible word sits where the visible pixels are (upper part of the page).
        Assert.InRange(hit.Rects[0].Bottom, 300, 500);
    }

    [Fact]
    public async Task JpegEncodeDecodeRoundTrip()
    {
        const int w = 64, h = 32;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (200, 100, 50, 255);

        string path = Path.Combine(Path.GetTempPath(), $"visya-{Guid.NewGuid():N}.jpg");
        try
        {
            using (var file = File.Create(path)) ImageTools.EncodeJpeg(file, bgra, w, h);
            var decoded = await ImageTools.DecodeAsync(path);
            Assert.Equal(w, decoded.Width);
            Assert.Equal(h, decoded.Height);
            Assert.InRange(decoded.Bgra[0], 185, 215);

            using var doc = PdfDocument.CreateEmpty();
            await ImageTools.AppendImagesAsync(doc, [path]);
            Assert.Equal(1, doc.PageCount);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
