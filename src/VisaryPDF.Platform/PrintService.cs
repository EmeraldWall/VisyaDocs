using System.ComponentModel;
using System.Runtime.InteropServices;
using VisaryPDF.Core;

namespace VisaryPDF.Platform;

/// <summary>A print job chosen in the print dialog. The service owns and releases the device context.</summary>
public sealed record PrintJob(nint Hdc, string PrinterName, IReadOnlyList<int> Pages, int Copies);

/// <summary>
/// Printing through the standard Windows print dialog. PDFium draws every page directly onto the
/// printer's device context, so text and vector graphics print sharp at the printer's resolution
/// and memory use stays low even for long documents.
/// </summary>
public static unsafe partial class PrintService
{
    /// <summary>Shows the print dialog. Returns null when the user cancels.</summary>
    /// <param name="currentPage">0 based page shown in the viewer, offered as "Current page".</param>
    public static PrintJob? ShowPrintDialog(nint ownerHwnd, int pageCount, int currentPage)
    {
        const int MaxRanges = 16;
        var ranges = stackalloc PRINTPAGERANGE[MaxRanges];
        ranges[0] = new PRINTPAGERANGE { nFromPage = 1, nToPage = (uint)pageCount };
        var dlg = new PRINTDLGEXW
        {
            lStructSize = (uint)sizeof(PRINTDLGEXW),
            hwndOwner = ownerHwnd,
            Flags = PD_RETURNDC | PD_NOSELECTION | PD_USEDEVMODECOPIESANDCOLLATE,
            nMaxPageRanges = MaxRanges,
            lpPageRanges = ranges,
            nMinPage = 1,
            nMaxPage = (uint)pageCount,
            nCopies = 1,
            nStartPage = START_PAGE_GENERAL,
        };

        int hr = PrintDlgExW(&dlg);
        try
        {
            if (hr < 0) throw new Win32Exception(hr, "The print dialog could not be opened.");
            if (dlg.dwResultAction != PD_RESULT_PRINT || dlg.hDC == 0)
            {
                if (dlg.hDC != 0) DeleteDC(dlg.hDC);
                return null;
            }

            IReadOnlyList<int> pages;
            if ((dlg.Flags & PD_CURRENTPAGE) != 0)
            {
                pages = [Math.Clamp(currentPage, 0, pageCount - 1)];
            }
            else if ((dlg.Flags & PD_PAGENUMS) != 0)
            {
                var list = new List<int>();
                for (int i = 0; i < dlg.nPageRanges; i++)
                {
                    int from = (int)Math.Max(1, ranges[i].nFromPage), to = (int)Math.Min((uint)pageCount, ranges[i].nToPage);
                    for (int p = from; p <= to; p++) list.Add(p - 1);
                }
                pages = list;
            }
            else
            {
                pages = Enumerable.Range(0, pageCount).ToArray();
            }
            // With PD_USEDEVMODECOPIESANDCOLLATE the driver makes the copies when it can; nCopies > 1
            // means it cannot, so the pages are sent that many times.
            return new PrintJob(dlg.hDC, ReadPrinterName(dlg.hDevNames), pages, (int)Math.Max(1, dlg.nCopies));
        }
        finally
        {
            if (dlg.hDevMode != 0) GlobalFree(dlg.hDevMode);
            if (dlg.hDevNames != 0) GlobalFree(dlg.hDevNames);
        }
    }

    /// <summary>Prints the job on a background thread and releases its device context.</summary>
    public static Task PrintAsync(PdfDocument doc, PrintJob job, string documentName,
        IProgress<double>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                PrintToDeviceContext(doc, job.Hdc, job.Pages, documentName, job.Copies, null, progress, ct);
            }
            finally
            {
                DeleteDC(job.Hdc);
            }
        }, CancellationToken.None);

    /// <summary>
    /// Sends pages to a printer by name without showing a dialog. <paramref name="outputFile"/> redirects
    /// the output to a file (for example with "Microsoft Print to PDF"). Returns false if the printer is not installed.
    /// </summary>
    public static bool PrintToPrinter(PdfDocument doc, string printerName, IReadOnlyList<int> pages, string documentName,
        string? outputFile = null)
    {
        nint hdc = CreateDCW("WINSPOOL", printerName, null, 0);
        if (hdc == 0) return false;
        try
        {
            PrintToDeviceContext(doc, hdc, pages, documentName, 1, outputFile, null, CancellationToken.None);
            return true;
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    private static void PrintToDeviceContext(PdfDocument doc, nint hdc, IReadOnlyList<int> pages, string documentName,
        int copies, string? outputFile, IProgress<double>? progress, CancellationToken ct)
    {
        int dpiX = GetDeviceCaps(hdc, LOGPIXELSX), dpiY = GetDeviceCaps(hdc, LOGPIXELSY);
        int printableW = GetDeviceCaps(hdc, HORZRES), printableH = GetDeviceCaps(hdc, VERTRES);
        int paperW = GetDeviceCaps(hdc, PHYSICALWIDTH), paperH = GetDeviceCaps(hdc, PHYSICALHEIGHT);
        int offsetX = GetDeviceCaps(hdc, PHYSICALOFFSETX), offsetY = GetDeviceCaps(hdc, PHYSICALOFFSETY);
        if (dpiX <= 0 || dpiY <= 0 || printableW <= 0 || printableH <= 0)
            throw new InvalidOperationException("The printer did not report its page size.");
        if (paperW <= 0 || paperH <= 0) (paperW, paperH, offsetX, offsetY) = (printableW, printableH, 0, 0);

        fixed (char* name = documentName)
        fixed (char* output = outputFile)
        {
            var info = new DOCINFOW { cbSize = sizeof(DOCINFOW), lpszDocName = name, lpszOutput = output };
            if (StartDocW(hdc, &info) <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "The printer did not accept the document.");
        }

        bool finished = false;
        try
        {
            int total = pages.Count * copies, done = 0;
            for (int copy = 0; copy < copies; copy++)
            {
                foreach (int pageIndex in pages)
                {
                    ct.ThrowIfCancellationRequested();
                    var g = doc.GetGeometry(pageIndex);
                    // Page size on paper at actual size, in device pixels.
                    double w = g.ViewWidth / 72 * dpiX, h = g.ViewHeight / 72 * dpiY;
                    int rotate = 0;
                    if ((w > h) != (printableW > printableH) && Math.Abs(w - h) > 1)
                    {
                        // Landscape page on portrait paper (or the reverse): turn it to fill the sheet.
                        rotate = 1;
                        (w, h) = (g.ViewHeight / 72 * dpiX, g.ViewWidth / 72 * dpiY);
                    }
                    // Shrink to the printable area, never enlarge.
                    double scale = Math.Min(1, Math.Min(printableW / w, printableH / h));
                    int outW = Math.Max(1, (int)(w * scale)), outH = Math.Max(1, (int)(h * scale));
                    // Center on the physical sheet, then keep it inside the printable area.
                    int x = Math.Clamp((paperW - outW) / 2 - offsetX, 0, Math.Max(0, printableW - outW));
                    int y = Math.Clamp((paperH - outH) / 2 - offsetY, 0, Math.Max(0, printableH - outH));

                    if (StartPage(hdc) <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "The printer stopped accepting pages.");
                    doc.RenderPageToDeviceContext(pageIndex, hdc, x, y, outW, outH, rotate);
                    if (EndPage(hdc) <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "The printer stopped accepting pages.");
                    progress?.Report(++done / (double)total);
                }
            }
            EndDoc(hdc);
            finished = true;
        }
        finally
        {
            if (!finished) AbortDoc(hdc);
        }
    }

    private static string ReadPrinterName(nint hDevNames)
    {
        if (hDevNames == 0) return "the printer";
        var names = (DEVNAMES*)GlobalLock(hDevNames);
        try
        {
            return names == null ? "the printer" : new string((char*)names + names->wDeviceOffset);
        }
        finally
        {
            GlobalUnlock(hDevNames);
        }
    }

    // Win32 ------------------------------------------------------------------------------------

    private const uint PD_NOSELECTION = 0x4, PD_PAGENUMS = 0x2, PD_RETURNDC = 0x100;
    private const uint PD_USEDEVMODECOPIESANDCOLLATE = 0x40000, PD_CURRENTPAGE = 0x400000;
    private const uint START_PAGE_GENERAL = 0xFFFFFFFF, PD_RESULT_PRINT = 1;
    private const int HORZRES = 8, VERTRES = 10, LOGPIXELSX = 88, LOGPIXELSY = 90;
    private const int PHYSICALWIDTH = 110, PHYSICALHEIGHT = 111, PHYSICALOFFSETX = 112, PHYSICALOFFSETY = 113;

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTPAGERANGE
    {
        public uint nFromPage, nToPage;
    }

    // Natural (8 byte) packing on x64 and ARM64, matching commdlg.h.
    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTDLGEXW
    {
        public uint lStructSize;
        public nint hwndOwner, hDevMode, hDevNames, hDC;
        public uint Flags, Flags2, ExclusionFlags, nPageRanges, nMaxPageRanges;
        public PRINTPAGERANGE* lpPageRanges;
        public uint nMinPage, nMaxPage, nCopies;
        public nint hInstance, lpPrintTemplateName, lpCallback;
        public uint nPropertyPages;
        public nint lphPropertyPages;
        public uint nStartPage, dwResultAction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVNAMES
    {
        public ushort wDriverOffset, wDeviceOffset, wOutputOffset, wDefault;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOCINFOW
    {
        public int cbSize;
        public char* lpszDocName, lpszOutput, lpszDatatype;
        public uint fwType;
    }

    [LibraryImport("comdlg32.dll")] private static partial int PrintDlgExW(PRINTDLGEXW* dlg);
    [LibraryImport("kernel32.dll")] private static partial void* GlobalLock(nint hMem);
    [LibraryImport("kernel32.dll")] private static partial int GlobalUnlock(nint hMem);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalFree(nint hMem);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateDCW(string driver, string device, string? output, nint devMode);
    [LibraryImport("gdi32.dll")] private static partial int DeleteDC(nint hdc);
    [LibraryImport("gdi32.dll")] private static partial int GetDeviceCaps(nint hdc, int index);
    [LibraryImport("gdi32.dll", SetLastError = true)] private static partial int StartDocW(nint hdc, DOCINFOW* info);
    [LibraryImport("gdi32.dll", SetLastError = true)] private static partial int StartPage(nint hdc);
    [LibraryImport("gdi32.dll", SetLastError = true)] private static partial int EndPage(nint hdc);
    [LibraryImport("gdi32.dll")] private static partial int EndDoc(nint hdc);
    [LibraryImport("gdi32.dll")] private static partial int AbortDoc(nint hdc);
}
