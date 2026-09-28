using System.Runtime.InteropServices;
using System.Text;
using VisyaDocs.Core.Interop;

namespace VisyaDocs.Core;

/// <summary>A link on a page: a jump inside the document or a web address.</summary>
public sealed record PdfLink(PdfRect Bounds, int? TargetPage, double? TargetY, string? Uri);

/// <summary>An entry of the document outline (bookmarks / table of contents).</summary>
public sealed record OutlineItem(string Title, int? TargetPage, double? TargetY, IReadOnlyList<OutlineItem> Children);

// Links and the outline.
public sealed unsafe partial class PdfDocument
{
    private const int MaxOutlineItems = 5000;

    public IReadOnlyList<PdfLink> GetLinks(int pageIndex) => WithPage(pageIndex, page =>
    {
        var links = new List<PdfLink>();
        int position = 0;
        nint link;
        while (Pdfium.FPDFLink_Enumerate(page, &position, &link) != 0)
        {
            Pdfium.FS_RECTF r;
            if (Pdfium.FPDFLink_GetAnnotRect(link, &r) == 0) continue;
            var bounds = new PdfRect(Math.Min(r.left, r.right), Math.Min(r.bottom, r.top), Math.Max(r.left, r.right), Math.Max(r.bottom, r.top));

            nint dest = Pdfium.FPDFLink_GetDest(Handle, link);
            string? uri = null;
            if (dest == 0)
            {
                nint action = Pdfium.FPDFLink_GetAction(link);
                (dest, uri) = ResolveAction(action);
            }
            var (target, y) = ResolveDest(dest);
            if (target is null && uri is null) continue;
            links.Add(new PdfLink(bounds, target, y, uri));
        }
        return links;
    });

    public IReadOnlyList<OutlineItem> GetOutline()
    {
        lock (Sync)
        {
            int budget = MaxOutlineItems;
            return ReadOutline(0, 0, ref budget);
        }
    }

    private List<OutlineItem> ReadOutline(nint parent, int depth, ref int budget)
    {
        var items = new List<OutlineItem>();
        if (depth > 32) return items;
        var seen = new HashSet<nint>();
        for (nint b = Pdfium.FPDFBookmark_GetFirstChild(Handle, parent);
             b != 0 && budget > 0 && seen.Add(b);
             b = Pdfium.FPDFBookmark_GetNextSibling(Handle, b))
        {
            budget--;
            nint bookmark = b;
            string title = Pdfium.ReadUtf16((p, n) => Pdfium.FPDFBookmark_GetTitle(bookmark, (void*)p, new CULong((nuint)n)).Value).Trim();
            nint dest = Pdfium.FPDFBookmark_GetDest(Handle, b);
            if (dest == 0) (dest, _) = ResolveAction(Pdfium.FPDFBookmark_GetAction(b));
            var (target, y) = ResolveDest(dest);
            items.Add(new OutlineItem(title.Length > 0 ? title : "(untitled)", target, y, ReadOutline(b, depth + 1, ref budget)));
        }
        return items;
    }

    private (nint Dest, string? Uri) ResolveAction(nint action)
    {
        if (action == 0) return (0, null);
        uint type = (uint)Pdfium.FPDFAction_GetType(action).Value;
        if (type == Pdfium.PDFACTION_GOTO) return (Pdfium.FPDFAction_GetDest(Handle, action), null);
        if (type != Pdfium.PDFACTION_URI) return (0, null);
        ulong length = Pdfium.FPDFAction_GetURIPath(Handle, action, null, default).Value;
        if (length <= 1 || length > 8192) return (0, null);
        var buffer = new byte[length];
        fixed (byte* p = buffer) Pdfium.FPDFAction_GetURIPath(Handle, action, p, new CULong((nuint)length));
        return (0, Encoding.ASCII.GetString(buffer, 0, buffer.Length - 1));
    }

    private (int? Page, double? Y) ResolveDest(nint dest)
    {
        if (dest == 0) return (null, null);
        int page = Pdfium.FPDFDest_GetDestPageIndex(Handle, dest);
        if (page < 0) return (null, null);
        int hasX, hasY, hasZoom;
        float x, y, zoom;
        bool known = Pdfium.FPDFDest_GetLocationInPage(dest, &hasX, &hasY, &hasZoom, &x, &y, &zoom) != 0 && hasY != 0;
        return (page, known ? y : null);
    }
}
