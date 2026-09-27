using System.Runtime.InteropServices;
using VisyaDocs.Core.Interop;

namespace VisyaDocs.Core;

// Text extraction, hit testing, selection and search.
public sealed unsafe partial class PdfDocument
{
    public string GetPageText(int pageIndex) => WithTextPage(pageIndex, (_, text) =>
        text == 0 ? string.Empty : Pdfium.TextGetText(text, 0, Pdfium.FPDFText_CountChars(text)));

    /// <summary>
    /// True when the page carries almost no extractable text but an image covers most of it,
    /// which is what a scanned page looks like.
    /// </summary>
    public bool IsLikelyScanned(int pageIndex) => WithTextPage(pageIndex, (page, text) =>
    {
        int chars = text == 0 ? 0 : Pdfium.FPDFText_CountChars(text);
        if (chars > 0 && Pdfium.TextGetText(text, 0, chars).Count(c => !char.IsWhiteSpace(c)) >= 10) return false;
        double pageArea = Pdfium.FPDF_GetPageWidthF(page) * (double)Pdfium.FPDF_GetPageHeightF(page);
        int count = Pdfium.FPDFPage_CountObjects(page);
        for (int i = 0; i < count; i++)
        {
            nint obj = Pdfium.FPDFPage_GetObject(page, i);
            if (Pdfium.FPDFPageObj_GetType(obj) != Pdfium.FPDF_PAGEOBJ_IMAGE) continue;
            var b = GetBounds(obj);
            if (b.Width * b.Height >= pageArea * 0.5) return true;
        }
        return false;
    });

    /// <summary>Character index at a page space point, or -1.</summary>
    public int GetCharIndexAt(int pageIndex, double x, double y, double tolerance = 4) =>
        WithTextPage(pageIndex, (_, text) =>
            text == 0 ? -1 : Pdfium.FPDFText_GetCharIndexAtPos(text, x, y, tolerance, tolerance));

    /// <summary>Text and highlight rectangles between two character indexes (inclusive, any order).</summary>
    public TextSelection GetSelection(int pageIndex, int fromChar, int toChar)
    {
        int start = Math.Min(fromChar, toChar), end = Math.Max(fromChar, toChar);
        return WithTextPage(pageIndex, (_, text) =>
        {
            if (text == 0 || start < 0) return new TextSelection(pageIndex, 0, 0, string.Empty, []);
            int count = end - start + 1;
            return new TextSelection(pageIndex, start, count,
                Pdfium.TextGetText(text, start, count), GetRects(text, start, count));
        });
    }

    /// <summary>Selects the word around a character index.</summary>
    public TextSelection GetWordAt(int pageIndex, int charIndex) => WithTextPage(pageIndex, (_, text) =>
    {
        if (text == 0 || charIndex < 0) return new TextSelection(pageIndex, 0, 0, string.Empty, []);
        int total = Pdfium.FPDFText_CountChars(text);
        string all = Pdfium.TextGetText(text, 0, total);
        if (charIndex >= all.Length) return new TextSelection(pageIndex, 0, 0, string.Empty, []);
        int s = charIndex, e = charIndex;
        while (s > 0 && char.IsLetterOrDigit(all[s - 1])) s--;
        while (e < all.Length - 1 && char.IsLetterOrDigit(all[e + 1])) e++;
        int count = e - s + 1;
        return new TextSelection(pageIndex, s, count, all.Substring(s, count), GetRects(text, s, count));
    });

    public IReadOnlyList<SearchHit> Search(string query, bool matchCase = false, bool wholeWord = false,
        CancellationToken cancellationToken = default)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrEmpty(query)) return hits;
        uint flags = (matchCase ? Pdfium.FPDF_MATCHCASE : 0) | (wholeWord ? Pdfium.FPDF_MATCHWHOLEWORD : 0);
        int pages = PageCount;
        for (int p = 0; p < pages; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WithTextPage(p, (_, text) =>
            {
                if (text == 0) return 0;
                nint find = Pdfium.FPDFText_FindStart(text, query, new CULong(flags), 0);
                if (find == 0) return 0;
                try
                {
                    while (Pdfium.FPDFText_FindNext(find) != 0)
                    {
                        int start = Pdfium.FPDFText_GetSchResultIndex(find);
                        int count = Pdfium.FPDFText_GetSchCount(find);
                        hits.Add(new SearchHit(p, start, count, GetRects(text, start, count)));
                    }
                }
                finally
                {
                    Pdfium.FPDFText_FindClose(find);
                }
                return 0;
            });
        }
        return hits;
    }

    private static List<PdfRect> GetRects(nint text, int start, int count)
    {
        var rects = new List<PdfRect>();
        int n = Pdfium.FPDFText_CountRects(text, start, count);
        for (int i = 0; i < n; i++)
        {
            double l, t, r, b;
            if (Pdfium.FPDFText_GetRect(text, i, &l, &t, &r, &b) != 0)
                rects.Add(new PdfRect(l, b, r, t));
        }
        return rects;
    }
}
