namespace VisyaDocs.Core.Tests;

public class RobustnessTests
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "assets", name);

    /// <summary>Wide red stamp placed on a page rotated 90 degrees must look wide on screen (upright).</summary>
    [Fact]
    public void StampsAndTextAreUprightOnRotatedPages()
    {
        using var doc = PdfDocumentTests.CreateDocWithText("Rotated");
        doc.RotatePage(0, 1);
        Assert.Equal(1, doc.GetRotation(0));
        var g = doc.GetGeometry(0);

        // 40 x 10 pixel solid red image placed into a 160 x 40 point box on screen.
        const int w = 40, h = 10;
        var red = new byte[w * h * 4];
        for (int i = 0; i < red.Length; i += 4) (red[i + 2], red[i + 3]) = (230, 255);
        var box = new ViewRect(100, 100, 160, 40);
        doc.AddImageStamp(0, red, w, h, box);
        doc.AddText(0, g.ToPage(100, 300).X, g.ToPage(100, 300).Y, "Upright text", new TextStyle(20));

        using var reopened = PdfDocument.Load(doc.SaveToBytes());
        var g2 = reopened.GetGeometry(0);
        int pw = (int)g2.ViewWidth, ph = (int)g2.ViewHeight;
        byte[] px = reopened.RenderPage(0, pw, ph);
        bool Red(int x, int y) => px[(y * pw + x) * 4 + 2] > 180 && px[(y * pw + x) * 4 + 1] < 80;
        Assert.True(Red(250, 110), "right end of the stamp is red");
        Assert.True(Red(110, 135), "bottom left of the stamp is red");
        Assert.False(Red(110, 170), "below the stamp is not red");

        // Text is upright: its selection rectangle is wider than tall on screen.
        var hit = Assert.Single(reopened.Search("Upright text"));
        var view = g2.ToView(PdfRect.Union(hit.Rects));
        Assert.True(view.Width > view.Height * 3, $"text should run horizontally on screen, got {view}");
        Assert.InRange(view.X, 95, 110);
    }

    [Fact]
    public void UndoHistoryIsCappedByMemory()
    {
        long old = PdfDocument.MaxHistoryBytes;
        try
        {
            using var doc = PdfDocumentTests.CreateDocWithText();
            long size = doc.SaveToBytes().Length;
            PdfDocument.MaxHistoryBytes = size * 3;   // room for about three steps
            for (int i = 0; i < 8; i++) doc.AddText(0, 72, 100 + i * 20, $"Line {i}", new TextStyle());
            Assert.InRange(doc.UndoDepth, 1, 3);
            doc.Undo();
            Assert.Contains("Line 6", doc.GetPageText(0));
            Assert.DoesNotContain("Line 7", doc.GetPageText(0));
        }
        finally
        {
            PdfDocument.MaxHistoryBytes = old;
        }
    }

    [Fact]
    public void PagesCanBeDeletedAndExtracted()
    {
        using var doc = PdfDocument.Open(Asset("links.pdf"));
        Assert.Equal(3, doc.PageCount);
        using (var extracted = doc.ExtractPages([2, 0]))
        {
            Assert.Equal(2, extracted.PageCount);
            Assert.Contains("Page 3", extracted.GetPageText(0));
            Assert.Contains("Page 1", extracted.GetPageText(1));
        }
        doc.DeletePage(1);
        Assert.Equal(2, doc.PageCount);
        Assert.Contains("Page 3", doc.GetPageText(1));
        doc.Undo();
        Assert.Equal(3, doc.PageCount);

        using var single = PdfDocumentTests.CreateDocWithText();
        Assert.Throws<PdfException>(() => single.DeletePage(0));
    }

    [Fact]
    public void ReadsLinksAndOutline()
    {
        using var doc = PdfDocument.Open(Asset("links.pdf"));
        var links = doc.GetLinks(0);
        var jump = Assert.Single(links, l => l.TargetPage is not null);
        Assert.Equal(2, jump.TargetPage);
        var web = Assert.Single(links, l => l.Uri is not null);
        Assert.Equal("https://example.org/", web.Uri);
        Assert.True(web.Bounds.Contains(100, 663));

        var outline = doc.GetOutline();
        Assert.Equal(["Chapter 1", "Chapter 2", "Chapter 3"], outline.Select(o => o.Title));
        Assert.Equal([0, 1, 2], outline.Select(o => o.TargetPage ?? -1));
        Assert.Equal("Section 2.1", Assert.Single(outline[1].Children).Title);
    }

    [Fact]
    public void PasswordProtectedFileKeepsProtectionAndCanBeUnlockedIntoACopy()
    {
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(Asset("locked.pdf")));
        using var doc = PdfDocument.Open(Asset("locked.pdf"), "open123");
        Assert.True(doc.OpenedWithPassword);
        Assert.Contains("Secret page", doc.GetPageText(0));

        // A normal save stays protected.
        doc.AddText(0, 72, 700, "Edited", new TextStyle());
        byte[] saved = doc.SaveToBytes();
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Load(saved));
        using (var again = PdfDocument.Load(saved, "open123")) Assert.Contains("Edited", again.GetPageText(0));

        // Remove password: only a copy, readable without a password.
        Assert.True(doc.IsEncrypted);
        string path = Path.Combine(Path.GetTempPath(), $"visya-unlocked-{Guid.NewGuid():N}.pdf");
        try
        {
            doc.SaveUnprotectedCopy(path);
            using var unlocked = PdfDocument.Open(path);
            Assert.False(unlocked.GetProperties().Encrypted);
            Assert.Contains("Secret page", unlocked.GetPageText(0));
            Assert.Contains("Edited", unlocked.GetPageText(0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RestrictionsAreReportedAndCanBeRemovedIntoACopy()
    {
        using var doc = PdfDocument.Open(Asset("restricted.pdf"));
        Assert.False(doc.OpenedWithPassword);
        var permissions = doc.Permissions;
        Assert.False(permissions.CanPrint);
        Assert.False(permissions.CanCopy);
        Assert.True(doc.IsEncrypted);
        string text = doc.GetPageText(0);

        string path = Path.Combine(Path.GetTempPath(), $"visya-unrestricted-{Guid.NewGuid():N}.pdf");
        try
        {
            doc.SaveUnprotectedCopy(path);
            using var copy = PdfDocument.Open(path);
            Assert.False(copy.IsEncrypted);
            Assert.True(copy.Permissions.All);
            Assert.Equal(text, copy.GetPageText(0));
            // The open document keeps its restrictions.
            Assert.False(doc.Permissions.CanPrint);
            using var inMemory = doc.CreateUnprotectedCopy();
            Assert.True(inMemory.Permissions.CanPrint);
            Assert.Equal(text, inMemory.GetPageText(0));
        }
        finally
        {
            File.Delete(path);
        }

        using var plain = PdfDocumentTests.CreateDocWithText();
        Assert.True(plain.Permissions.All);
        Assert.False(plain.IsEncrypted);
        Assert.Throws<PdfException>(() => plain.SaveUnprotectedCopy(Path.Combine(Path.GetTempPath(), "never.pdf")));
    }
}
