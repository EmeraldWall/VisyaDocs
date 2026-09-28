using VisyaDocs.Core.Interop;

namespace VisyaDocs.Core;

// Building documents: image pages and merging.
public sealed unsafe partial class PdfDocument
{
    private const double A4Short = 595.28, A4Long = 841.89;

    /// <summary>Appends a page holding a JPEG image. The JPEG data is embedded as is, without re-encoding.</summary>
    public void AppendJpegPage(byte[] jpeg) => Mutate(() =>
    {
        nint image = Pdfium.FPDFPageObj_NewImageObj(Handle);
        if (!Pdfium.LoadJpeg(image, jpeg))
        {
            Pdfium.FPDFPageObj_Destroy(image);
            throw new PdfException("The JPEG image could not be read.");
        }
        uint w, h;
        Pdfium.FPDFImageObj_GetImagePixelSize(image, &w, &h);
        PlaceImagePage(image, w, h);
        return 0;
    });

    /// <summary>Appends a page holding an image given as top-down BGRA pixels.</summary>
    public void AppendImagePage(byte[] bgra, int width, int height) => Mutate(() =>
    {
        fixed (byte* p = bgra)
        {
            nint bitmap = Pdfium.FPDFBitmap_CreateEx(width, height, Pdfium.FPDFBitmap_BGRA, p, width * 4);
            if (bitmap == 0) throw new PdfException("The image is too large.");
            try
            {
                nint image = Pdfium.FPDFPageObj_NewImageObj(Handle);
                if (Pdfium.FPDFImageObj_SetBitmap(null, 0, image, bitmap) == 0)
                {
                    Pdfium.FPDFPageObj_Destroy(image);
                    throw new PdfException("The image could not be added.");
                }
                PlaceImagePage(image, (uint)width, (uint)height);
            }
            finally
            {
                Pdfium.FPDFBitmap_Destroy(bitmap);
            }
        }
        return 0;
    });

    // Page is A4 width (landscape images get the long side) with height following the image aspect.
    private void PlaceImagePage(nint image, uint pixelWidth, uint pixelHeight)
    {
        if (pixelWidth == 0 || pixelHeight == 0) throw new PdfException("The image is empty.");
        double pageWidth = pixelWidth > pixelHeight ? A4Long : A4Short;
        double pageHeight = pageWidth * pixelHeight / pixelWidth;
        int index = Pdfium.FPDF_GetPageCount(Handle);
        nint page = Pdfium.FPDFPage_New(Handle, index, pageWidth, pageHeight);
        try
        {
            Pdfium.FPDFImageObj_SetMatrix(image, pageWidth, 0, 0, pageHeight, 0, 0);
            Pdfium.FPDFPage_InsertObject(page, image);
            if (Pdfium.FPDFPage_GenerateContent(page) == 0) throw new PdfException("The page could not be created.");
        }
        finally
        {
            Pdfium.FPDF_ClosePage(page);
        }
    }

    /// <summary>Appends every page of another document.</summary>
    public void AppendDocument(PdfDocument other) => Mutate(() =>
    {
        if (Pdfium.FPDF_ImportPages(Handle, other.Handle, null, Pdfium.FPDF_GetPageCount(Handle)) == 0)
            throw new PdfException("The pages could not be imported.");
        return 0;
    });

    /// <summary>Rotation of a page in quarter turns clockwise (0 to 3).</summary>
    public int GetRotation(int pageIndex) => WithPage(pageIndex, page => Pdfium.FPDFPage_GetRotation(page));

    /// <summary>Turns a page by quarter turns (positive = clockwise). Saved in the PDF and undoable.</summary>
    public void RotatePage(int pageIndex, int quarterTurns) => Mutate(() => WithPage(pageIndex, page =>
    {
        int rotation = ((Pdfium.FPDFPage_GetRotation(page) + quarterTurns) % 4 + 4) % 4;
        Pdfium.FPDFPage_SetRotation(page, rotation);
        return 0;
    }));

    /// <summary>Removes a page. The last remaining page cannot be deleted.</summary>
    public void DeletePage(int pageIndex) => Mutate(() =>
    {
        int count = Pdfium.FPDF_GetPageCount(Handle);
        if ((uint)pageIndex >= (uint)count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (count == 1) throw new PdfException("A document needs at least one page.");
        Pdfium.FPDFPage_Delete(Handle, pageIndex);
        return 0;
    });

    /// <summary>Copies the given pages (0 based) into a new, unsaved document.</summary>
    public PdfDocument ExtractPages(IReadOnlyList<int> pages)
    {
        if (pages.Count == 0) throw new ArgumentException("No pages to extract.", nameof(pages));
        var result = CreateEmpty();
        string range = string.Join(",", pages.Select(p => (p + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        lock (Sync)
        {
            if (Pdfium.FPDF_ImportPages(result.Handle, Handle, range, 0) == 0)
            {
                result.Dispose();
                throw new PdfException("The pages could not be extracted.");
            }
        }
        return result;
    }
}
