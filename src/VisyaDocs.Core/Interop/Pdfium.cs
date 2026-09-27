using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VisyaDocs.Core.Interop;

// P/Invoke declarations for the subset of the PDFium C API that VisyaDocs uses.
// Signatures follow the headers of the pinned release (see PdfiumRelease in Directory.Build.props).
// FPDF_BOOL is int, "unsigned long" is CULong (32 bit on Windows, 64 bit on Linux).
internal static unsafe partial class Pdfium
{
    private const string Lib = "pdfium";

    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int FPDF_PRINTING = 0x800;
    public const int FPDFBitmap_BGRA = 4;
    public const int FPDF_ERR_PASSWORD = 4;
    public const int FPDF_NO_INCREMENTAL = 1 << 1;
    public const int FPDF_PAGEOBJ_TEXT = 1;
    public const int FPDF_PAGEOBJ_IMAGE = 3;
    public const int FPDF_TEXTRENDERMODE_FILL = 0;
    public const int FPDF_TEXTRENDERMODE_INVISIBLE = 3;
    public const int FPDF_FONT_TRUETYPE = 2;
    public const int FPDF_ANNOT_TEXT = 1;
    public const int FPDF_ANNOT_HIGHLIGHT = 9;
    public const int FPDF_ANNOT_FLAG_PRINT = 1 << 2;
    public const int FPDFANNOT_COLORTYPE_Color = 0;
    public const uint FPDF_MATCHCASE = 0x1;
    public const uint FPDF_MATCHWHOLEWORD = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_MATRIX
    {
        public float a, b, c, d, e, f;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_RECTF
    {
        public float left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_QUADPOINTSF
    {
        public float x1, y1, x2, y2, x3, y3, x4, y4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEACCESS
    {
        public CULong m_FileLen;
        public delegate* unmanaged[Cdecl]<void*, CULong, byte*, CULong, int> m_GetBlock;
        public void* m_Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEWRITE
    {
        public int version;
        public delegate* unmanaged[Cdecl]<FPDF_FILEWRITE*, void*, CULong, int> WriteBlock;
    }

    // Library
    [LibraryImport(Lib)] public static partial void FPDF_InitLibrary();
    [LibraryImport(Lib)] public static partial CULong FPDF_GetLastError();

    // Documents
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDF_LoadMemDocument64(void* data, nuint size, string? password);
    [LibraryImport(Lib)] public static partial nint FPDF_CreateNewDocument();
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(nint document);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(nint document);
    [LibraryImport(Lib)] public static partial int FPDF_SaveAsCopy(nint document, FPDF_FILEWRITE* fileWrite, CULong flags);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int FPDF_ImportPages(nint dest, nint src, string? pageRange, int index);

    // Pages
    [LibraryImport(Lib)] public static partial nint FPDF_LoadPage(nint document, int index);
    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageWidthF(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageHeightF(nint page);
    [LibraryImport(Lib)] public static partial nint FPDFPage_New(nint document, int index, double width, double height);
    [LibraryImport(Lib)] public static partial int FPDFPage_GenerateContent(nint page);
    [LibraryImport(Lib)]
    public static partial int FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate,
        double pageX, double pageY, int* deviceX, int* deviceY);

    // Rendering
    [LibraryImport(Lib)] public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, CULong color);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(nint bitmap);
    [LibraryImport(Lib)]
    public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // Text extraction
    [LibraryImport(Lib)] public static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Lib)] public static partial void FPDFText_ClosePage(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_CountChars(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_GetText(nint textPage, int start, int count, char* result);
    [LibraryImport(Lib)] public static partial int FPDFText_CountRects(nint textPage, int start, int count);
    [LibraryImport(Lib)] public static partial int FPDFText_GetRect(nint textPage, int index, double* left, double* top, double* right, double* bottom);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharBox(nint textPage, int index, double* left, double* right, double* bottom, double* top);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharIndexAtPos(nint textPage, double x, double y, double xTol, double yTol);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FPDFText_FindStart(nint textPage, string findWhat, CULong flags, int startIndex);
    [LibraryImport(Lib)] public static partial int FPDFText_FindNext(nint handle);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchResultIndex(nint handle);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchCount(nint handle);
    [LibraryImport(Lib)] public static partial void FPDFText_FindClose(nint handle);

    // Page objects
    [LibraryImport(Lib)] public static partial int FPDFPage_CountObjects(nint page);
    [LibraryImport(Lib)] public static partial nint FPDFPage_GetObject(nint page, int index);
    [LibraryImport(Lib)] public static partial int FPDFPage_InsertObject(nint page, nint obj);
    [LibraryImport(Lib)] public static partial int FPDFPage_RemoveObject(nint page, nint obj);
    [LibraryImport(Lib)] public static partial void FPDFPageObj_Destroy(nint obj);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetType(nint obj);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetBounds(nint obj, float* left, float* bottom, float* right, float* top);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetMatrix(nint obj, FS_MATRIX* matrix);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetMatrix(nint obj, FS_MATRIX* matrix);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_GetFillColor(nint obj, uint* r, uint* g, uint* b, uint* a);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetFillColor(nint obj, uint r, uint g, uint b, uint a);

    // Text objects and fonts
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint FPDFText_LoadStandardFont(nint document, string font);
    [LibraryImport(Lib)] public static partial nint FPDFText_LoadFont(nint document, byte* data, uint size, int fontType, int cid);
    [LibraryImport(Lib)] public static partial void FPDFFont_Close(nint font);
    [LibraryImport(Lib)] public static partial nint FPDFPageObj_CreateTextObj(nint document, nint font, float fontSize);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int FPDFText_SetText(nint textObj, string text);
    [LibraryImport(Lib)] public static partial CULong FPDFTextObj_GetText(nint textObj, nint textPage, char* buffer, CULong length);
    [LibraryImport(Lib)] public static partial int FPDFTextObj_GetFontSize(nint textObj, float* size);
    [LibraryImport(Lib)] public static partial int FPDFTextObj_SetTextRenderMode(nint textObj, int mode);

    // Images
    [LibraryImport(Lib)] public static partial nint FPDFPageObj_NewImageObj(nint document);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_SetBitmap(nint* pages, int count, nint imageObj, nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_LoadJpegFileInline(nint* pages, int count, nint imageObj, FPDF_FILEACCESS* access);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_GetImagePixelSize(nint imageObj, uint* width, uint* height);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_SetMatrix(nint imageObj, double a, double b, double c, double d, double e, double f);

    // Annotations
    [LibraryImport(Lib)] public static partial int FPDFPage_GetAnnotCount(nint page);
    [LibraryImport(Lib)] public static partial nint FPDFPage_GetAnnot(nint page, int index);
    [LibraryImport(Lib)] public static partial nint FPDFPage_CreateAnnot(nint page, int subtype);
    [LibraryImport(Lib)] public static partial int FPDFPage_RemoveAnnot(nint page, int index);
    [LibraryImport(Lib)] public static partial void FPDFPage_CloseAnnot(nint annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetSubtype(nint annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetRect(nint annot, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetRect(nint annot, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetColor(nint annot, int type, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetFlags(nint annot, int flags);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_AppendAttachmentPoints(nint annot, FS_QUADPOINTSF* quad);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int FPDFAnnot_SetStringValue(nint annot, string key, char* value);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial CULong FPDFAnnot_GetStringValue(nint annot, string key, char* buffer, CULong length);

    /// <summary>Sets a UTF-16 string value on an annotation dictionary key.</summary>
    public static bool AnnotSetString(nint annot, string key, string value)
    {
        fixed (char* p = value + "\0")
        {
            return FPDFAnnot_SetStringValue(annot, key, p) != 0;
        }
    }

    /// <summary>Reads a UTF-16 string value from an annotation dictionary key.</summary>
    public static string AnnotGetString(nint annot, string key)
    {
        ulong bytes = FPDFAnnot_GetStringValue(annot, key, null, default).Value;
        if (bytes <= 2) return string.Empty;
        var buffer = new char[bytes / 2];
        fixed (char* p = buffer)
        {
            FPDFAnnot_GetStringValue(annot, key, p, new CULong((nuint)bytes));
        }
        return new string(buffer, 0, buffer.Length - 1);
    }

    /// <summary>Reads the Unicode text of a text page object.</summary>
    public static string TextObjGetText(nint textObj, nint textPage)
    {
        ulong bytes = FPDFTextObj_GetText(textObj, textPage, null, default).Value;
        if (bytes <= 2) return string.Empty;
        var buffer = new char[bytes / 2];
        fixed (char* p = buffer)
        {
            FPDFTextObj_GetText(textObj, textPage, p, new CULong((nuint)bytes));
        }
        return new string(buffer, 0, buffer.Length - 1);
    }

    /// <summary>Extracts a range of characters from a text page.</summary>
    public static string TextGetText(nint textPage, int start, int count)
    {
        if (count <= 0) return string.Empty;
        var buffer = new char[count + 1];
        int written;
        fixed (char* p = buffer)
        {
            written = FPDFText_GetText(textPage, start, count, p);
        }
        return written <= 1 ? string.Empty : new string(buffer, 0, written - 1);
    }

    /// <summary>Writes a document to a stream through FPDF_SaveAsCopy.</summary>
    public static bool SaveToStream(nint document, Stream stream)
    {
        var handle = GCHandle.Alloc(stream);
        try
        {
            var writer = new StreamWriter
            {
                Base = new FPDF_FILEWRITE { version = 1, WriteBlock = &WriteBlock },
                Stream = GCHandle.ToIntPtr(handle),
            };
            return FPDF_SaveAsCopy(document, &writer.Base, new CULong(FPDF_NO_INCREMENTAL)) != 0;
        }
        finally
        {
            handle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamWriter
    {
        public FPDF_FILEWRITE Base;
        public nint Stream;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteBlock(FPDF_FILEWRITE* self, void* data, CULong size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(((StreamWriter*)self)->Stream).Target!;
            stream.Write(new ReadOnlySpan<byte>(data, checked((int)size.Value)));
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Loads JPEG bytes into an image object without re-encoding them.</summary>
    public static bool LoadJpeg(nint imageObj, byte[] jpeg)
    {
        fixed (byte* data = jpeg)
        {
            var reader = new MemoryReader { Data = data, Length = jpeg.Length };
            var access = new FPDF_FILEACCESS
            {
                m_FileLen = new CULong((nuint)jpeg.Length),
                m_GetBlock = &GetBlock,
                m_Param = &reader,
            };
            return FPDFImageObj_LoadJpegFileInline(null, 0, imageObj, &access) != 0;
        }
    }

    private struct MemoryReader
    {
        public byte* Data;
        public long Length;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetBlock(void* param, CULong position, byte* buffer, CULong size)
    {
        var reader = (MemoryReader*)param;
        ulong pos = position.Value, len = size.Value;
        if (pos + len > (ulong)reader->Length) return 0;
        Buffer.MemoryCopy(reader->Data + pos, buffer, (long)len, (long)len);
        return 1;
    }
}
