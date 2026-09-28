using VisaryPDF.Core.Interop;

namespace VisaryPDF.Core;

// Editing: existing text, new text, comments (sticky notes), highlights and the OCR text layer.
public sealed unsafe partial class PdfDocument
{
    /// <summary>
    /// TrueType font used for text the standard PDF fonts cannot encode (anything outside
    /// Latin-1). The app points this at a Windows font such as Arial.
    /// </summary>
    public static string? FallbackFontPath { get; set; }

    private readonly Dictionary<string, nint> _fonts = [];
    private static byte[]? s_fallbackFont;
    private static string? s_fallbackFontLoadedFrom;

    /// <summary>Finds the text object under a page space point.</summary>
    public TextObjectInfo? FindTextObjectAt(int pageIndex, double x, double y) =>
        WithTextPage(pageIndex, (page, text) =>
        {
            TextObjectInfo? best = null;
            int count = Pdfium.FPDFPage_CountObjects(page);
            for (int i = 0; i < count; i++)
            {
                nint obj = Pdfium.FPDFPage_GetObject(page, i);
                if (Pdfium.FPDFPageObj_GetType(obj) != Pdfium.FPDF_PAGEOBJ_TEXT) continue;
                var bounds = GetBounds(obj);
                if (!bounds.Inflate(1).Contains(x, y)) continue;
                // Prefer the smallest hit so nested or overlapping runs resolve to the one under the cursor.
                if (best is not null && bounds.Width * bounds.Height >= best.Bounds.Width * best.Bounds.Height) continue;
                float size;
                Pdfium.FPDFTextObj_GetFontSize(obj, &size);
                best = new TextObjectInfo(pageIndex, i, Pdfium.TextObjGetText(obj, text), bounds, size * Scale(obj));
            }
            return best;
        });

    /// <summary>
    /// Replaces the text of an existing text object. The original embedded font is kept when it
    /// can represent the new text; otherwise the run is rebuilt with a standard or fallback font
    /// at the same position, size and color.
    /// </summary>
    public void ReplaceText(int pageIndex, int objectIndex, string newText) => Mutate(() => WithTextPage(pageIndex, (page, text) =>
    {
        nint obj = Pdfium.FPDFPage_GetObject(page, objectIndex);
        if (obj == 0 || Pdfium.FPDFPageObj_GetType(obj) != Pdfium.FPDF_PAGEOBJ_TEXT)
            throw new PdfException("The selected text can no longer be found on the page.");

        if (newText.Length == 0)
        {
            Pdfium.FPDFPage_RemoveObject(page, obj);
            Pdfium.FPDFPageObj_Destroy(obj);
        }
        else
        {
            string oldText = Pdfium.TextObjGetText(obj, text);
            bool reused = false;
            if (newText.All(c => oldText.Contains(c)) && Pdfium.FPDFText_SetText(obj, newText) != 0)
                reused = Pdfium.TextObjGetText(obj, text) == newText;
            if (!reused) RebuildTextObject(page, obj, newText);
        }
        if (Pdfium.FPDFPage_GenerateContent(page) == 0) throw new PdfException("The page could not be updated.");
        return 0;
    }));

    private void RebuildTextObject(nint page, nint oldObj, string newText)
    {
        float size;
        Pdfium.FPDFTextObj_GetFontSize(oldObj, &size);
        Pdfium.FS_MATRIX m;
        Pdfium.FPDFPageObj_GetMatrix(oldObj, &m);
        uint r, g, b, a;
        if (Pdfium.FPDFPageObj_GetFillColor(oldObj, &r, &g, &b, &a) == 0) (r, g, b, a) = (0, 0, 0, 255);

        nint obj = CreateTextObject(newText, size <= 0 ? 12 : size, "Helvetica");
        Pdfium.FPDFPageObj_SetMatrix(obj, &m);
        Pdfium.FPDFPageObj_SetFillColor(obj, r, g, b, a);
        Pdfium.FPDFPage_InsertObject(page, obj);
        Pdfium.FPDFPage_RemoveObject(page, oldObj);
        Pdfium.FPDFPageObj_Destroy(oldObj);
    }

    /// <summary>
    /// Adds new text with its first baseline starting at a page space point. Supports multiple lines.
    /// The text is upright as seen on screen, also on rotated pages.
    /// </summary>
    public void AddText(int pageIndex, double x, double baselineY, string value, TextStyle style)
    {
        var geometry = GetGeometry(pageIndex);
        var (vx, vy) = geometry.ToView(x, baselineY);
        Mutate(() => WithPage(pageIndex, page =>
        {
            var lines = value.Replace("\r\n", "\n").Split('\n');
            double lineHeight = style.FontSize * 1.2;
            var c = style.TextColor;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                nint obj = CreateTextObject(lines[i], style.FontSize, style.StandardFont);
                Pdfium.FPDFPageObj_SetFillColor(obj, c.R, c.G, c.B, 255);
                // Unit box whose bottom left is the line's baseline start on screen.
                var m = ToMatrix(geometry.UprightMatrix(new ViewRect(vx, vy + i * lineHeight - 1, 1, 1)));
                Pdfium.FPDFPageObj_SetMatrix(obj, &m);
                Pdfium.FPDFPage_InsertObject(page, obj);
            }
            if (Pdfium.FPDFPage_GenerateContent(page) == 0) throw new PdfException("The page could not be updated.");
            return 0;
        }));
    }

    private static Pdfium.FS_MATRIX ToMatrix((double A, double B, double C, double D, double E, double F) m) => new()
    {
        a = (float)m.A, b = (float)m.B, c = (float)m.C, d = (float)m.D, e = (float)m.E, f = (float)m.F,
    };

    /// <summary>
    /// Writes recognized words as invisible text so a scanned page becomes searchable and selectable.
    /// Each word is stretched to exactly cover its box.
    /// </summary>
    public void AddInvisibleTextLayer(int pageIndex, IReadOnlyList<OcrWord> words) => Mutate(() => WithPage(pageIndex, page =>
    {
        foreach (var word in words)
        {
            if (string.IsNullOrWhiteSpace(word.Text) || word.Bounds.Width <= 0 || word.Bounds.Height <= 0) continue;
            nint obj = CreateTextObject(word.Text, (float)word.Bounds.Height, "Helvetica");
            Pdfium.FPDFTextObj_SetTextRenderMode(obj, Pdfium.FPDF_TEXTRENDERMODE_INVISIBLE);
            var natural = GetBounds(obj);
            if (natural.Width <= 0 || natural.Height <= 0)
            {
                Pdfium.FPDFPageObj_Destroy(obj);
                continue;
            }
            double sx = word.Bounds.Width / natural.Width, sy = word.Bounds.Height / natural.Height;
            var m = new Pdfium.FS_MATRIX
            {
                a = (float)sx,
                d = (float)sy,
                e = (float)(word.Bounds.Left - natural.Left * sx),
                f = (float)(word.Bounds.Bottom - natural.Bottom * sy),
            };
            Pdfium.FPDFPageObj_SetMatrix(obj, &m);
            Pdfium.FPDFPage_InsertObject(page, obj);
        }
        if (Pdfium.FPDFPage_GenerateContent(page) == 0) throw new PdfException("The page could not be updated.");
        return 0;
    }));

    /// <summary>
    /// Places an image (for example a signature) on a page so it fills the given view rectangle
    /// (points, top left origin, as shown on screen), upright whatever the page rotation. Pixels are
    /// top-down BGRA with straight (not premultiplied) alpha; transparent pixels let the page show through.
    /// </summary>
    public void AddImageStamp(int pageIndex, byte[] bgra, int width, int height, ViewRect bounds)
    {
        var matrix = GetGeometry(pageIndex).UprightMatrix(bounds);
        Mutate(() => WithPage(pageIndex, page =>
        {
            if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) throw new ArgumentException("Invalid image size.");
            fixed (byte* p = bgra)
            {
                nint bitmap = Pdfium.FPDFBitmap_CreateEx(width, height, Pdfium.FPDFBitmap_BGRA, p, width * 4);
                if (bitmap == 0) throw new PdfException("The image is too large.");
                try
                {
                    nint image = Pdfium.FPDFPageObj_NewImageObj(Handle);
                    if (Pdfium.FPDFImageObj_SetBitmap(&page, 1, image, bitmap) == 0)
                    {
                        Pdfium.FPDFPageObj_Destroy(image);
                        throw new PdfException("The image could not be added.");
                    }
                    Pdfium.FPDFImageObj_SetMatrix(image, matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);
                    Pdfium.FPDFPage_InsertObject(page, image);
                }
                finally
                {
                    Pdfium.FPDFBitmap_Destroy(bitmap);
                }
            }
            if (Pdfium.FPDFPage_GenerateContent(page) == 0) throw new PdfException("The page could not be updated.");
            return 0;
        }));
    }

    // Annotations -------------------------------------------------------------------------

    public IReadOnlyList<AnnotationInfo> GetAnnotations(int pageIndex) => WithPage(pageIndex, page =>
    {
        var list = new List<AnnotationInfo>();
        int count = Pdfium.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            nint annot = Pdfium.FPDFPage_GetAnnot(page, i);
            if (annot == 0) continue;
            try
            {
                var kind = Pdfium.FPDFAnnot_GetSubtype(annot) switch
                {
                    Pdfium.FPDF_ANNOT_TEXT => AnnotationKind.Note,
                    Pdfium.FPDF_ANNOT_HIGHLIGHT => AnnotationKind.Highlight,
                    _ => AnnotationKind.Other,
                };
                if (kind == AnnotationKind.Other) continue;
                Pdfium.FS_RECTF r;
                Pdfium.FPDFAnnot_GetRect(annot, &r);
                list.Add(new AnnotationInfo(pageIndex, i, kind, new PdfRect(r.left, r.bottom, r.right, r.top),
                    Pdfium.AnnotGetString(annot, "Contents"), Pdfium.AnnotGetString(annot, "T")));
            }
            finally
            {
                Pdfium.FPDFPage_CloseAnnot(annot);
            }
        }
        return list;
    });

    /// <summary>Adds a sticky note comment whose icon's top left corner sits at a page space point.</summary>
    public int AddNote(int pageIndex, double x, double y, string contents, string author) => Mutate(() => WithPage(pageIndex, page =>
    {
        nint annot = Pdfium.FPDFPage_CreateAnnot(page, Pdfium.FPDF_ANNOT_TEXT);
        if (annot == 0) throw new PdfException("The comment could not be added.");
        try
        {
            var rect = new Pdfium.FS_RECTF { left = (float)x, top = (float)y, right = (float)x + 20, bottom = (float)y - 20 };
            Pdfium.FPDFAnnot_SetRect(annot, &rect);
            var c = PdfColor.Note;
            Pdfium.FPDFAnnot_SetColor(annot, Pdfium.FPDFANNOT_COLORTYPE_Color, c.R, c.G, c.B, 255);
            Pdfium.FPDFAnnot_SetFlags(annot, Pdfium.FPDF_ANNOT_FLAG_PRINT);
            SetCommentStrings(annot, contents, author);
            return Pdfium.FPDFPage_GetAnnotCount(page) - 1;
        }
        finally
        {
            Pdfium.FPDFPage_CloseAnnot(annot);
        }
    }));

    /// <summary>Adds a highlight over text rectangles (for example a selection), with an optional comment.</summary>
    public int AddHighlight(int pageIndex, IReadOnlyList<PdfRect> rects, string? contents = null, string? author = null,
        PdfColor? color = null) => Mutate(() => WithPage(pageIndex, page =>
    {
        if (rects.Count == 0) throw new ArgumentException("Nothing to highlight.", nameof(rects));
        nint annot = Pdfium.FPDFPage_CreateAnnot(page, Pdfium.FPDF_ANNOT_HIGHLIGHT);
        if (annot == 0) throw new PdfException("The highlight could not be added.");
        try
        {
            var c = color ?? PdfColor.Highlight;
            Pdfium.FPDFAnnot_SetColor(annot, Pdfium.FPDFANNOT_COLORTYPE_Color, c.R, c.G, c.B, 255);
            Pdfium.FPDFAnnot_SetFlags(annot, Pdfium.FPDF_ANNOT_FLAG_PRINT);
            foreach (var r in rects)
            {
                var q = new Pdfium.FS_QUADPOINTSF
                {
                    x1 = (float)r.Left, y1 = (float)r.Top, x2 = (float)r.Right, y2 = (float)r.Top,
                    x3 = (float)r.Left, y3 = (float)r.Bottom, x4 = (float)r.Right, y4 = (float)r.Bottom,
                };
                Pdfium.FPDFAnnot_AppendAttachmentPoints(annot, &q);
            }
            var u = PdfRect.Union(rects);
            var rect = new Pdfium.FS_RECTF { left = (float)u.Left, top = (float)u.Top, right = (float)u.Right, bottom = (float)u.Bottom };
            Pdfium.FPDFAnnot_SetRect(annot, &rect);
            SetCommentStrings(annot, contents ?? string.Empty, author ?? string.Empty);
            return Pdfium.FPDFPage_GetAnnotCount(page) - 1;
        }
        finally
        {
            Pdfium.FPDFPage_CloseAnnot(annot);
        }
    }));

    public void UpdateAnnotationText(int pageIndex, int annotIndex, string contents) => Mutate(() => WithPage(pageIndex, page =>
    {
        nint annot = Pdfium.FPDFPage_GetAnnot(page, annotIndex);
        if (annot == 0) throw new PdfException("The comment can no longer be found.");
        try
        {
            Pdfium.AnnotSetString(annot, "Contents", contents);
            Pdfium.AnnotSetString(annot, "M", PdfDate(DateTimeOffset.Now));
        }
        finally
        {
            Pdfium.FPDFPage_CloseAnnot(annot);
        }
        return 0;
    }));

    public void RemoveAnnotation(int pageIndex, int annotIndex) => Mutate(() => WithPage(pageIndex, page =>
    {
        if (Pdfium.FPDFPage_RemoveAnnot(page, annotIndex) == 0) throw new PdfException("The comment could not be removed.");
        return 0;
    }));

    private static void SetCommentStrings(nint annot, string contents, string author)
    {
        Pdfium.AnnotSetString(annot, "Contents", contents);
        if (author.Length > 0) Pdfium.AnnotSetString(annot, "T", author);
        Pdfium.AnnotSetString(annot, "M", PdfDate(DateTimeOffset.Now));
    }

    private static string PdfDate(DateTimeOffset t)
    {
        var o = t.Offset;
        return $"D:{t:yyyyMMddHHmmss}{(o < TimeSpan.Zero ? '-' : '+')}{Math.Abs(o.Hours):00}'{Math.Abs(o.Minutes):00}'";
    }

    // Helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Creates a text object in a font that can encode the text: a standard PDF font for
    /// Latin-1 text, otherwise the fallback TrueType font embedded as a CID font.
    /// </summary>
    private nint CreateTextObject(string value, double fontSize, string standardFont)
    {
        nint font = IsLatin1(value) ? 0 : GetFont("fallback");
        if (font == 0) font = GetFont(standardFont);
        if (font == 0) throw new PdfException("No font is available for the text.");
        nint obj = Pdfium.FPDFPageObj_CreateTextObj(Handle, font, (float)fontSize);
        if (obj == 0) throw new PdfException("The text could not be created.");
        if (Pdfium.FPDFText_SetText(obj, value) == 0)
        {
            Pdfium.FPDFPageObj_Destroy(obj);
            throw new PdfException("The text could not be created.");
        }
        return obj;
    }

    // Fonts are loaded once per document so repeated text (such as an OCR layer) embeds a font only once.
    private nint GetFont(string name)
    {
        if (_fonts.TryGetValue(name, out nint font)) return font;
        if (name == "fallback")
        {
            if (LoadFallbackFont() is not { } data) return 0;
            fixed (byte* p = data) font = Pdfium.FPDFText_LoadFont(Handle, p, (uint)data.Length, Pdfium.FPDF_FONT_TRUETYPE, 1);
        }
        else
        {
            font = Pdfium.FPDFText_LoadStandardFont(Handle, name);
        }
        if (font != 0) _fonts[name] = font;
        return font;
    }

    private static bool IsLatin1(string s) => s.All(c => c is >= ' ' and < '\u007f' or >= ' ' and <= 'ÿ');

    private static byte[]? LoadFallbackFont()
    {
        string? path = FallbackFontPath;
        if (path is null || !File.Exists(path)) return null;
        if (s_fallbackFont is null || s_fallbackFontLoadedFrom != path)
        {
            s_fallbackFont = File.ReadAllBytes(path);
            s_fallbackFontLoadedFrom = path;
        }
        return s_fallbackFont;
    }

    private static PdfRect GetBounds(nint obj)
    {
        float l, b, r, t;
        return Pdfium.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) != 0 ? new PdfRect(l, b, r, t) : default;
    }

    // Effective vertical scale of an object's matrix, so the reported font size matches what is seen.
    private static double Scale(nint obj)
    {
        Pdfium.FS_MATRIX m;
        if (Pdfium.FPDFPageObj_GetMatrix(obj, &m) == 0) return 1;
        double s = Math.Sqrt(m.c * m.c + m.d * m.d);
        return s > 0 ? s : 1;
    }
}
