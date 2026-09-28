using System.Runtime.Versioning;
using VisaryPDF.Core.Interop;

namespace VisaryPDF.Core;

// Printing: PDFium draws straight onto a printer device context, so text and shapes stay vector sharp.
public sealed partial class PdfDocument
{
    /// <summary>
    /// Renders a page onto a GDI device context (for example a printer DC) at the given device
    /// rectangle. <paramref name="rotate"/> is in quarter turns clockwise. Comments and highlights are included.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public void RenderPageToDeviceContext(int pageIndex, nint hdc, int x, int y, int width, int height, int rotate = 0) =>
        WithPage(pageIndex, page =>
        {
            if (Pdfium.FPDF_RenderPage(hdc, page, x, y, width, height, rotate, Pdfium.FPDF_ANNOT | Pdfium.FPDF_PRINTING) == 0)
                throw new PdfException($"Page {pageIndex + 1} could not be printed.");
            return 0;
        });
}
