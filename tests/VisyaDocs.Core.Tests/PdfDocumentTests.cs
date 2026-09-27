using System.IO.Compression;
using VisyaDocs.Core;
using VisyaDocs.Core.Export;

namespace VisyaDocs.Core.Tests;

public class PdfDocumentTests
{
    private const int PagePx = 200;

    /// <summary>A one page document (white image page) with a line of text, reopened from bytes.</summary>
    internal static PdfDocument CreateDocWithText(string text = "Hello VisyaDocs", double fontSize = 24)
    {
        using var doc = PdfDocument.CreateEmpty();
        doc.AppendImagePage(White(PagePx, PagePx), PagePx, PagePx);
        doc.AddText(0, 72, 500, text, new TextStyle(fontSize));
        return PdfDocument.Load(doc.SaveToBytes());
    }

    private static byte[] White(int w, int h) => Enumerable.Repeat((byte)255, w * h * 4).ToArray();

    [Fact]
    public void AddedTextSurvivesSaveAndReload()
    {
        using var doc = CreateDocWithText();
        Assert.Equal(1, doc.PageCount);
        Assert.Contains("Hello VisyaDocs", doc.GetPageText(0));
        Assert.False(doc.IsLikelyScanned(0));   // a large image, but real text on it
    }

    [Fact]
    public void ImageOnlyPageIsDetectedAsScanned()
    {
        using var doc = PdfDocument.CreateEmpty();
        doc.AppendImagePage(White(50, 50), 50, 50);
        Assert.True(doc.IsLikelyScanned(0));
        doc.AddText(0, 72, 72, "12", new TextStyle());   // a stamped page number keeps it a scan
        Assert.True(doc.IsLikelyScanned(0));
    }

    [Fact]
    public void GeometryMapsPageSpaceToTopLeftViewSpace()
    {
        using var doc = CreateDocWithText();
        var g = doc.GetGeometry(0);
        Assert.Equal(595.28, g.ViewWidth, 1);
        Assert.Equal(595.28, g.ViewHeight, 1);
        var (x, y) = g.ToView(0, g.ViewHeight);
        Assert.Equal(0, x, 1);
        Assert.Equal(0, y, 1);
        var (px, py) = g.ToPage(100, 50);
        Assert.Equal(100, px, 1);
        Assert.Equal(g.ViewHeight - 50, py, 1);
    }

    [Fact]
    public void SearchFindsTextWithRectangles()
    {
        using var doc = CreateDocWithText();
        var hits = doc.Search("visyadocs");
        var hit = Assert.Single(hits);
        Assert.Equal(0, hit.PageIndex);
        Assert.Equal(9, hit.Length);
        Assert.NotEmpty(hit.Rects);
        Assert.Empty(doc.Search("visyadocs", matchCase: true));
    }

    [Fact]
    public void SelectionAndWordLookupReturnText()
    {
        using var doc = CreateDocWithText();
        var hit = doc.Search("VisyaDocs")[0];
        var r = hit.Rects[0];
        int index = doc.GetCharIndexAt(0, (r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        Assert.InRange(index, hit.CharIndex, hit.CharIndex + hit.Length - 1);
        Assert.Equal("VisyaDocs", doc.GetWordAt(0, index).Text);
        Assert.Equal("Hello", doc.GetSelection(0, 4, 0).Text);
    }

    [Fact]
    public void EditTextReusingOriginalFont()
    {
        using var doc = CreateDocWithText();
        var obj = FindObject(doc, "Hello VisyaDocs");
        doc.ReplaceText(0, obj.ObjectIndex, "Hello Docs");
        using var reopened = PdfDocument.Load(doc.SaveToBytes());
        Assert.Contains("Hello Docs", reopened.GetPageText(0));
        Assert.DoesNotContain("VisyaDocs", reopened.GetPageText(0));
    }

    [Fact]
    public void EditTextWithNewCharactersRebuildsTheRun()
    {
        using var doc = CreateDocWithText();
        var obj = FindObject(doc, "Hello VisyaDocs");
        doc.ReplaceText(0, obj.ObjectIndex, "Quartz 2026!");
        using var reopened = PdfDocument.Load(doc.SaveToBytes());
        Assert.Contains("Quartz 2026!", reopened.GetPageText(0));
        var edited = FindObject(reopened, "Quartz 2026!");
        Assert.InRange(edited.Bounds.Left, obj.Bounds.Left - 2, obj.Bounds.Left + 2);  // glyph side bearings differ
        Assert.Equal(obj.FontSize, edited.FontSize, 1);
    }

    [Fact]
    public void EmptyReplacementDeletesTheText()
    {
        using var doc = CreateDocWithText();
        doc.ReplaceText(0, FindObject(doc, "Hello VisyaDocs").ObjectIndex, "");
        Assert.DoesNotContain("Hello", doc.GetPageText(0));
    }

    [Fact]
    public void NonLatinTextUsesFallbackFont()
    {
        string? font = new[] { @"C:\Windows\Fonts\arial.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" }
            .FirstOrDefault(File.Exists);
        if (font is null) return;
        PdfDocument.FallbackFontPath = font;
        using var doc = CreateDocWithText();
        doc.AddText(0, 72, 300, "Привет мир", new TextStyle(14));
        using var reopened = PdfDocument.Load(doc.SaveToBytes());
        Assert.Contains("Привет мир", reopened.GetPageText(0));
    }

    [Fact]
    public void MultiLineTextCreatesOneRunPerLine()
    {
        using var doc = CreateDocWithText();
        doc.AddText(0, 72, 300, "first line\nsecond line", new TextStyle(12, new PdfColor(200, 0, 0)));
        string text = doc.GetPageText(0);
        Assert.Contains("first line", text);
        Assert.Contains("second line", text);
    }

    [Fact]
    public void NotesCanBeAddedEditedAndRemoved()
    {
        using var doc = CreateDocWithText();
        doc.AddNote(0, 300, 300, "Check this", "Tom");
        using var reopened = PdfDocument.Load(doc.SaveToBytes());
        var note = Assert.Single(reopened.GetAnnotations(0));
        Assert.Equal(AnnotationKind.Note, note.Kind);
        Assert.Equal("Check this", note.Contents);
        Assert.Equal("Tom", note.Author);

        reopened.UpdateAnnotationText(0, note.Index, "Checked");
        Assert.Equal("Checked", reopened.GetAnnotations(0)[0].Contents);
        reopened.RemoveAnnotation(0, note.Index);
        Assert.Empty(reopened.GetAnnotations(0));
    }

    [Fact]
    public void NotesAndHighlightsAreRendered()
    {
        using var doc = CreateDocWithText();
        var hit = doc.Search("Hello")[0];
        doc.AddHighlight(0, hit.Rects, "important");
        doc.AddNote(0, 300, 300, "note", "");
        var g = doc.GetGeometry(0);
        byte[] withAnnots = doc.RenderPage(0, 600, 600);
        byte[] without = doc.RenderPage(0, 600, 600, annotations: false);

        Assert.True(Differs(withAnnots, without, g.ToView(hit.Rects[0]), 600 / g.ViewWidth), "highlight not rendered");
        Assert.True(Differs(withAnnots, without, g.ToView(new PdfRect(300, 280, 320, 300)), 600 / g.ViewWidth), "note not rendered");

        var kinds = doc.GetAnnotations(0).Select(a => a.Kind).ToArray();
        Assert.Equal([AnnotationKind.Highlight, AnnotationKind.Note], kinds);
        Assert.Equal("important", doc.GetAnnotations(0)[0].Contents);
    }

    [Fact]
    public void UndoAndRedoRestoreContent()
    {
        using var doc = CreateDocWithText();
        Assert.False(doc.CanUndo);
        doc.AddText(0, 72, 200, "Temporary", new TextStyle());
        Assert.True(doc.IsDirty);
        Assert.Contains("Temporary", doc.GetPageText(0));

        doc.Undo();
        Assert.DoesNotContain("Temporary", doc.GetPageText(0));
        Assert.True(doc.CanRedo);

        doc.Redo();
        Assert.Contains("Temporary", doc.GetPageText(0));
    }

    [Fact]
    public void InvisibleTextLayerIsSearchableButNotVisible()
    {
        using var doc = PdfDocument.CreateEmpty();
        doc.AppendImagePage(White(PagePx, PagePx), PagePx, PagePx);
        byte[] before = doc.RenderPage(0, 300, 300);

        doc.AddInvisibleTextLayer(0, [new OcrWord("Scanned", new PdfRect(100, 400, 220, 430)), new OcrWord("words", new PdfRect(230, 400, 300, 430))]);

        using var reopened = PdfDocument.Load(doc.SaveToBytes());
        Assert.Contains("Scanned", reopened.GetPageText(0));
        var hit = Assert.Single(reopened.Search("words"));
        Assert.Equal(230, hit.Rects[0].Left, 0);
        Assert.Equal(before, reopened.RenderPage(0, 300, 300));
    }

    [Fact]
    public void JpegPagesAreEmbeddedWithoutReencoding()
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "assets", "sample.jpg"));
        using var doc = PdfDocument.CreateEmpty();
        doc.AppendJpegPage(jpeg);
        byte[] saved = doc.SaveToBytes();
        Assert.True(saved.AsSpan().IndexOf(jpeg.AsSpan(0, 64)) >= 0, "JPEG stream should be stored verbatim");

        using var reopened = PdfDocument.Load(saved);
        var g = reopened.GetGeometry(0);
        Assert.Equal(841.89, g.ViewWidth, 1);          // landscape image: long A4 side
        Assert.Equal(841.89 * 80 / 120, g.ViewHeight, 1);
    }

    [Fact]
    public void DocumentsCanBeMerged()
    {
        using var a = CreateDocWithText("First");
        using var b = CreateDocWithText("Second");
        a.AppendDocument(b);
        Assert.Equal(2, a.PageCount);
        Assert.Contains("Second", a.GetPageText(1));
    }

    [Fact]
    public void SaveWritesFileAndClearsDirtyFlag()
    {
        string path = Path.Combine(Path.GetTempPath(), $"visya-{Guid.NewGuid():N}.pdf");
        try
        {
            using var doc = CreateDocWithText();
            doc.AddText(0, 72, 100, "Saved", new TextStyle());
            doc.Save(path);
            Assert.False(doc.IsDirty);
            using var reopened = PdfDocument.Open(path);
            Assert.Contains("Saved", reopened.GetPageText(0));
            Assert.Equal(path, reopened.FilePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InvalidDataThrowsPdfException()
    {
        Assert.Throws<PdfException>(() => PdfDocument.Load("not a pdf"u8.ToArray()));
    }

    private static TextObjectInfo FindObject(PdfDocument doc, string text)
    {
        var rect = doc.Search(text)[0].Rects[0];
        var obj = doc.FindTextObjectAt(0, (rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        Assert.NotNull(obj);
        Assert.Equal(text, obj.Text);
        return obj;
    }

    private static bool Differs(byte[] a, byte[] b, ViewRect view, double scale)
    {
        var r = view.Scale(scale);
        for (int y = (int)r.Y; y < (int)(r.Y + r.Height); y++)
        {
            for (int x = (int)r.X; x < (int)(r.X + r.Width); x++)
            {
                int i = (y * 600 + x) * 4;
                if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) return true;
            }
        }
        return false;
    }
}
