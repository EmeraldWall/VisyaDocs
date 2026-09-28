namespace VisyaDocs.Core.Tests;

public class FormsAndPropertiesTests
{
    private static PdfDocument OpenForm() =>
        PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "assets", "form.pdf"));

    [Fact]
    public void ReadsFormFields()
    {
        using var doc = OpenForm();
        Assert.True(doc.HasForm);
        var fields = doc.GetFormFields(0);
        var name = Assert.Single(fields, f => f.Name == "name");
        Assert.Equal(FormFieldKind.Text, name.Kind);
        Assert.Equal("", name.Value);
        Assert.True(name.Bounds.Width > 200);

        var subscribe = Assert.Single(fields, f => f.Name == "subscribe");
        Assert.Equal(FormFieldKind.CheckBox, subscribe.Kind);
        Assert.False(subscribe.IsChecked);

        var country = Assert.Single(fields, f => f.Name == "country");
        Assert.Equal(FormFieldKind.ComboBox, country.Kind);
        Assert.Equal(["India", "Germany", "Japan"], country.Options);
    }

    [Fact]
    public void FilledValuesSurviveSaveAndRender()
    {
        using var doc = OpenForm();
        var fields = doc.GetFormFields(0);
        var name = fields.Single(f => f.Name == "name");
        var geometry = doc.GetGeometry(0);
        byte[] before = doc.RenderPage(0, 600, 849);

        doc.SetFieldText(0, name.AnnotIndex, "Ada Lovelace");
        doc.ToggleCheck(0, fields.Single(f => f.Name == "subscribe").AnnotIndex);
        doc.SelectOption(0, fields.Single(f => f.Name == "country").AnnotIndex, 2);
        Assert.True(doc.CanUndo);

        byte[] after = doc.RenderPage(0, 600, 849);
        var box = geometry.ToView(name.Bounds).Scale(600 / geometry.ViewWidth);
        Assert.True(Differs(before, after, box, 600), "typed text should be drawn in the field");

        using var reopened = PdfDocument.Load(doc.SaveToBytes());
        var saved = reopened.GetFormFields(0);
        Assert.Equal("Ada Lovelace", saved.Single(f => f.Name == "name").Value);
        Assert.True(saved.Single(f => f.Name == "subscribe").IsChecked);
        Assert.Equal("Japan", saved.Single(f => f.Name == "country").Value);
    }

    [Fact]
    public void UndoRestoresEmptyForm()
    {
        using var doc = OpenForm();
        var name = doc.GetFormFields(0).Single(f => f.Name == "name");
        doc.SetFieldText(0, name.AnnotIndex, "Temporary");
        doc.Undo();
        Assert.True(doc.HasForm);
        Assert.Equal("", doc.GetFormFields(0).Single(f => f.Name == "name").Value);
    }

    [Fact]
    public void ReadsDocumentProperties()
    {
        using var doc = OpenForm();
        var p = doc.GetProperties();
        Assert.Equal("VisyaDocs form fixture", p.Title);
        Assert.Equal("VisyaDocs tests", p.Author);
        Assert.Equal("AcroForm", p.Subject);
        Assert.StartsWith("1.", p.PdfVersion);
        Assert.Equal(1, p.PageCount);
        Assert.Equal(595.3, p.PageWidth, 0);
        Assert.True(p.HasForm);
        Assert.False(p.Encrypted);
        Assert.True(p.CanPrint && p.CanCopy && p.CanModify);
        Assert.NotNull(p.Created);
        Assert.NotNull(p.FileSize);
    }

    [Theory]
    [InlineData("D:20240131154500+05'30'", 2024, 1, 31, 15, 45, 330)]
    [InlineData("D:20231201", 2023, 12, 1, 0, 0, 0)]
    [InlineData("D:19991231235959-08'00", 1999, 12, 31, 23, 59, -480)]
    public void ParsesPdfDates(string value, int y, int mo, int d, int h, int mi, int offsetMinutes)
    {
        var date = PdfDocument.ParsePdfDate(value);
        Assert.NotNull(date);
        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, date!.Value.Second, TimeSpan.FromMinutes(offsetMinutes)), date);
    }

    [Fact]
    public void ImageStampKeepsTransparency()
    {
        using var doc = PdfDocumentTests.CreateDocWithText("Signed here", 24);
        var hit = doc.Search("Signed")[0].Rects[0];
        // A stamp that is transparent except for a red bar along its bottom edge.
        const int w = 40, h = 20;
        var pixels = new byte[w * h * 4];
        for (int y = h - 3; y < h; y++)
            for (int x = 0; x < w; x++)
                (pixels[(y * w + x) * 4 + 2], pixels[(y * w + x) * 4 + 3]) = (220, 255);
        var stamp = new PdfRect(hit.Left, hit.Bottom - 10, hit.Left + 120, hit.Top + 10);
        doc.AddImageStamp(0, pixels, w, h, stamp);

        using var reopened = PdfDocument.Load(doc.SaveToBytes());
        Assert.Contains("Signed here", reopened.GetPageText(0));
        var g = reopened.GetGeometry(0);
        byte[] render = reopened.RenderPage(0, 600, 600);
        double s = 600 / g.ViewWidth;
        var v = g.ToView(stamp).Scale(s);
        // Bottom edge is red, middle still shows black text through the transparent area.
        int bottom = ((int)(v.Y + v.Height - 2) * 600 + (int)(v.X + v.Width / 2)) * 4;
        Assert.True(render[bottom + 2] > 150 && render[bottom] < 100, "red bar");
        bool dark = false;
        var t = g.ToView(hit).Scale(s);
        for (int y = (int)t.Y; y < t.Y + t.Height && !dark; y++)
            for (int x = (int)t.X; x < t.X + t.Width && !dark; x++)
                dark = render[(y * 600 + x) * 4] < 80;
        Assert.True(dark, "text must remain visible through transparent pixels");
    }

    private static bool Differs(byte[] a, byte[] b, ViewRect r, int stride)
    {
        for (int y = (int)r.Y; y < (int)(r.Y + r.Height); y++)
            for (int x = (int)r.X; x < (int)(r.X + r.Width); x++)
            {
                int i = (y * stride + x) * 4;
                if (a[i] != b[i]) return true;
            }
        return false;
    }
}
