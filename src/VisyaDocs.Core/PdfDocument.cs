using System.Runtime.InteropServices;
using VisyaDocs.Core.Interop;

namespace VisyaDocs.Core;

/// <summary>
/// An open PDF document. All PDFium calls go through one global lock because PDFium is
/// not thread safe, so every member can be called from any thread.
/// </summary>
public sealed unsafe partial class PdfDocument : IDisposable
{
    private const int MaxUndo = 30;

    /// <summary>
    /// Undo and redo keep a full copy of the file per step; this caps their total size so large
    /// PDFs cannot exhaust memory (the newest step is always kept).
    /// </summary>
    internal static long MaxHistoryBytes = 64L * 1024 * 1024;

    internal static readonly Lock Sync = new();
    private static bool s_initialized;

    private nint _doc;
    private void* _buffer;
    private readonly History _undo = new();
    private readonly History _redo = new();
    private readonly string? _password;

    private PdfDocument(nint doc, void* buffer, string? password)
    {
        _doc = doc;
        _buffer = buffer;
        _password = password;
        InitFormsLocked();
    }

    /// <summary>Raised after any change to the document content (edit, undo, redo).</summary>
    public event EventHandler? Changed;

    public string? FilePath { get; private set; }

    /// <summary>
    /// Changes whenever pages may have been added, removed, rotated or resized (page tools, append,
    /// undo, redo), so a viewer knows to measure its pages again.
    /// </summary>
    public int LayoutVersion { get; private set; }

    internal void BumpLayout() => LayoutVersion++;
    public bool IsDirty { get; private set; }
    public bool CanUndo { get { lock (Sync) return _undo.Count > 0; } }
    public bool CanRedo { get { lock (Sync) return _redo.Count > 0; } }

    public int PageCount
    {
        get { lock (Sync) return Pdfium.FPDF_GetPageCount(Handle); }
    }

    internal nint Handle => _doc != 0 ? _doc : throw new ObjectDisposedException(nameof(PdfDocument));

    public static PdfDocument Open(string path, string? password = null)
    {
        var doc = Load(File.ReadAllBytes(path), password);
        doc.FilePath = path;
        return doc;
    }

    public static PdfDocument Load(byte[] bytes, string? password = null)
    {
        lock (Sync)
        {
            EnsureLibrary();
            var (doc, buffer) = LoadNative(bytes, password);
            return new PdfDocument(doc, (void*)buffer, password);
        }
    }

    public static PdfDocument CreateEmpty()
    {
        lock (Sync)
        {
            EnsureLibrary();
            return new PdfDocument(Pdfium.FPDF_CreateNewDocument(), null, null) { IsDirty = true };
        }
    }

    private static void EnsureLibrary()
    {
        if (s_initialized) return;
        Pdfium.FPDF_InitLibrary();
        s_initialized = true;
    }

    // PDFium reads lazily from the buffer, so it lives in native memory until the document closes.
    private static (nint Doc, nint Buffer) LoadNative(byte[] bytes, string? password)
    {
        void* buffer = NativeMemory.Alloc((nuint)Math.Max(bytes.Length, 1));
        bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
        nint doc = Pdfium.FPDF_LoadMemDocument64(buffer, (nuint)bytes.Length, password);
        if (doc != 0) return (doc, (nint)buffer);

        NativeMemory.Free(buffer);
        ulong error = Pdfium.FPDF_GetLastError().Value;
        if (error == Pdfium.FPDF_ERR_PASSWORD) throw new PdfPasswordException();
        throw new PdfException(error switch
        {
            2 => "The file could not be found or opened.",
            3 => "The file is not a valid PDF or is damaged.",
            5 => "This PDF uses an unsupported security scheme.",
            _ => "The PDF could not be opened.",
        });
    }

    /// <summary>Page size and the mapping between page and view coordinates.</summary>
    public PageGeometry GetGeometry(int pageIndex) => WithPage(pageIndex, page =>
    {
        double w = Pdfium.FPDF_GetPageWidthF(page), h = Pdfium.FPDF_GetPageHeightF(page);
        // Sample PDFium's own page to device transform on a large virtual device for precision.
        const int K = 64;
        int sx = (int)Math.Ceiling(w * K), sy = (int)Math.Ceiling(h * K);
        (double X, double Y) Map(double x, double y)
        {
            int dx, dy;
            Pdfium.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, x, y, &dx, &dy);
            return (dx * w / sx, dy * h / sy);
        }
        var o = Map(0, 0);
        var px = Map(1000, 0);
        var py = Map(0, 1000);
        return new PageGeometry(w, h,
            (px.X - o.X) / 1000, (px.Y - o.Y) / 1000,
            (py.X - o.X) / 1000, (py.Y - o.Y) / 1000,
            o.X, o.Y);
    });

    /// <summary>Renders a page into a new top-down BGRA buffer (stride = width * 4) on white.</summary>
    public byte[] RenderPage(int pageIndex, int width, int height, bool annotations = true)
    {
        var pixels = new byte[checked(width * height * 4)];
        RenderPage(pageIndex, pixels, width, height, annotations);
        return pixels;
    }

    public void RenderPage(int pageIndex, Span<byte> bgra, int width, int height, bool annotations = true)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            throw new ArgumentException("Invalid bitmap size.");
        fixed (byte* p = bgra)
        {
            nint bits = (nint)p;
            WithPage(pageIndex, page =>
            {
                nint bitmap = Pdfium.FPDFBitmap_CreateEx(width, height, Pdfium.FPDFBitmap_BGRA, (void*)bits, width * 4);
                if (bitmap == 0) throw new PdfException("Not enough memory to render the page.");
                try
                {
                    Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, new CULong(0xFFFFFFFF));
                    Pdfium.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0,
                        annotations ? Pdfium.FPDF_ANNOT : 0);
                    // Interactive form fields draw their current values on top.
                    if (annotations && _form != 0)
                        Pdfium.FPDF_FFLDraw(_form, bitmap, page, 0, 0, width, height, 0, Pdfium.FPDF_ANNOT);
                }
                finally
                {
                    Pdfium.FPDFBitmap_Destroy(bitmap);
                }
                return 0;
            });
        }
    }

    /// <summary>Saves the document. Writes to a temporary file first so a failure never corrupts the target.</summary>
    public void Save(string? path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("No file path to save to.");
        byte[] bytes = SaveToBytes();
        string temp = path + ".visya-tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
        lock (Sync)
        {
            FilePath = path;
            IsDirty = false;
        }
    }

    public byte[] SaveToBytes()
    {
        lock (Sync) return SnapshotLocked();
    }

    /// <summary>True when the document needed a password to open.</summary>
    public bool OpenedWithPassword => !string.IsNullOrEmpty(_password);

    /// <summary>What the document allows with the password it was opened with (all for unprotected files).</summary>
    public PdfPermissions Permissions
    {
        get
        {
            lock (Sync)
            {
                ulong bits = Pdfium.FPDF_GetDocPermissions(Handle).Value;
                // Permission bits from the PDF specification (table 22): 3 print, 4 modify, 5 copy.
                return new PdfPermissions((bits & (1 << 2)) != 0, (bits & (1 << 4)) != 0, (bits & (1 << 3)) != 0);
            }
        }
    }

    /// <summary>True when the file is encrypted: it has an open password, the author's restrictions, or both.</summary>
    public bool IsEncrypted
    {
        get { lock (Sync) return Pdfium.FPDF_GetSecurityHandlerRevision(Handle) >= 0; }
    }

    /// <summary>
    /// Saves a copy without encryption: no password to open it and no restrictions on printing,
    /// copying or changes. The open document (and its file) keep their protection.
    /// </summary>
    public void SaveUnprotectedCopy(string path)
    {
        if (!IsEncrypted) throw new PdfException("This PDF is not password protected or restricted.");
        byte[] bytes = UnprotectedBytes();
        string temp = path + ".visya-tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// An in-memory copy without encryption and restrictions. Used to print a PDF whose author
    /// disallowed printing, after the user confirmed, so the print path never depends on how the
    /// engine treats the restriction.
    /// </summary>
    public PdfDocument CreateUnprotectedCopy() => Load(UnprotectedBytes());

    private byte[] UnprotectedBytes()
    {
        lock (Sync)
        {
            if (_form != 0) Pdfium.FORM_ForceToKillFocus(_form);
            using var stream = new MemoryStream();
            if (!Pdfium.SaveToStream(Handle, stream, removeSecurity: true)) throw new PdfException("The PDF could not be saved.");
            return stream.ToArray();
        }
    }

    private byte[] SnapshotLocked()
    {
        if (_form != 0) Pdfium.FORM_ForceToKillFocus(_form);
        using var stream = new MemoryStream();
        if (!Pdfium.SaveToStream(Handle, stream)) throw new PdfException("The PDF could not be saved.");
        return stream.ToArray();
    }

    public void Undo() => Step(_undo, _redo);

    public void Redo() => Step(_redo, _undo);

    private void Step(History from, History to)
    {
        lock (Sync)
        {
            if (from.Count == 0) return;
            to.Push(SnapshotLocked());
            to.Trim(MaxUndo, MaxHistoryBytes);
            ReplaceLocked(from.Pop());
            LayoutVersion++;
            IsDirty = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ReplaceLocked(byte[] bytes)
    {
        var (doc, buffer) = LoadNative(bytes, _password);
        CloseLocked();
        _doc = doc;
        _buffer = (void*)buffer;
        InitFormsLocked();
    }

    /// <summary>Runs an edit as one undoable step.</summary>
    internal T Mutate<T>(Func<T> edit)
    {
        T result;
        lock (Sync)
        {
            byte[] before = SnapshotLocked();
            try
            {
                result = edit();
            }
            catch
            {
                ReplaceLocked(before);
                throw;
            }
            _undo.Push(before);
            _undo.Trim(MaxUndo, MaxHistoryBytes);
            _redo.Clear();
            IsDirty = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>A stack of file snapshots that forgets its oldest entries when it grows too large.</summary>
    private sealed class History
    {
        private readonly List<byte[]> _items = [];
        private long _bytes;

        public int Count => _items.Count;

        public long Bytes => _bytes;

        public void Push(byte[] snapshot)
        {
            _items.Add(snapshot);
            _bytes += snapshot.LongLength;
        }

        public byte[] Pop()
        {
            var last = _items[^1];
            _items.RemoveAt(_items.Count - 1);
            _bytes -= last.LongLength;
            return last;
        }

        public void Clear()
        {
            _items.Clear();
            _bytes = 0;
        }

        public void Trim(int maxCount, long maxBytes)
        {
            while (_items.Count > 1 && (_items.Count > maxCount || _bytes > maxBytes))
            {
                _bytes -= _items[0].LongLength;
                _items.RemoveAt(0);
            }
        }
    }

    /// <summary>Number of undo steps currently kept (for tests and diagnostics).</summary>
    internal int UndoDepth
    {
        get { lock (Sync) return _undo.Count; }
    }

    /// <summary>Loads a page, runs the callback under the PDFium lock and closes the page.</summary>
    internal T WithPage<T>(int pageIndex, Func<nint, T> action)
    {
        lock (Sync)
        {
            if ((uint)pageIndex >= (uint)Pdfium.FPDF_GetPageCount(Handle))
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            nint page = Pdfium.FPDF_LoadPage(Handle, pageIndex);
            if (page == 0) throw new PdfException($"Page {pageIndex + 1} could not be loaded.");
            if (_form != 0) Pdfium.FORM_OnAfterLoadPage(page, _form);
            try
            {
                return action(page);
            }
            finally
            {
                if (_form != 0) Pdfium.FORM_OnBeforeClosePage(page, _form);
                Pdfium.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Same as <see cref="WithPage{T}"/> with a text page loaded as well.</summary>
    internal T WithTextPage<T>(int pageIndex, Func<nint, nint, T> action) => WithPage(pageIndex, page =>
    {
        nint text = Pdfium.FPDFText_LoadPage(page);
        try
        {
            return action(page, text);
        }
        finally
        {
            if (text != 0) Pdfium.FPDFText_ClosePage(text);
        }
    });

    private void CloseLocked()
    {
        ExitFormsLocked();
        foreach (nint font in _fonts.Values) Pdfium.FPDFFont_Close(font);
        _fonts.Clear();
        if (_doc != 0) Pdfium.FPDF_CloseDocument(_doc);
        if (_buffer != null) NativeMemory.Free(_buffer);
        _doc = 0;
        _buffer = null;
    }

    public void Dispose()
    {
        lock (Sync) CloseLocked();
    }
}
