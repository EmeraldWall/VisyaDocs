using System.Globalization;
using System.Runtime.InteropServices;
using VisaryPDF.Core.Interop;

namespace VisaryPDF.Core;

public sealed record PdfProperties(
    string Title,
    string Author,
    string Subject,
    string Keywords,
    string Creator,
    string Producer,
    DateTimeOffset? Created,
    DateTimeOffset? Modified,
    string PdfVersion,
    int PageCount,
    double PageWidth,
    double PageHeight,
    bool Encrypted,
    bool CanPrint,
    bool CanCopy,
    bool CanModify,
    bool HasForm,
    string? FilePath,
    long? FileSize);

// Document information (metadata, version, security).
public sealed unsafe partial class PdfDocument
{
    public PdfProperties GetProperties()
    {
        lock (Sync)
        {
            nint doc = Handle;
            string Meta(string tag) => Pdfium.ReadUtf16((p, n) => Pdfium.FPDF_GetMetaText(doc, tag, (void*)p, new CULong((nuint)n)).Value).Trim();

            int version = 0;
            Pdfium.FPDF_GetFileVersion(doc, &version);
            ulong permissions = Pdfium.FPDF_GetDocPermissions(doc).Value;
            bool encrypted = Pdfium.FPDF_GetSecurityHandlerRevision(doc) >= 0;
            int pages = Pdfium.FPDF_GetPageCount(doc);
            var size = pages > 0 ? GetGeometry(0) : null;
            long? fileSize = FilePath is not null && File.Exists(FilePath) ? new FileInfo(FilePath).Length : null;

            // Permission bits from the PDF specification (table 22): 3 print, 4 modify, 5 copy.
            return new PdfProperties(
                Meta("Title"), Meta("Author"), Meta("Subject"), Meta("Keywords"), Meta("Creator"), Meta("Producer"),
                ParsePdfDate(Meta("CreationDate")), ParsePdfDate(Meta("ModDate")),
                version > 0 ? $"{version / 10}.{version % 10}" : "unknown",
                pages, size?.ViewWidth ?? 0, size?.ViewHeight ?? 0,
                encrypted,
                (permissions & (1 << 2)) != 0,
                (permissions & (1 << 4)) != 0,
                (permissions & (1 << 3)) != 0,
                _form != 0,
                FilePath, fileSize);
        }
    }

    /// <summary>Parses a PDF date string such as "D:20240131154500+05'30'".</summary>
    public static DateTimeOffset? ParsePdfDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string s = value.StartsWith("D:", StringComparison.Ordinal) ? value[2..] : value;
        string digits = new(s.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length < 4) return null;
        digits = digits.PadRight(14, '0');
        // Missing month and day default to 01.
        if (digits[4..6] == "00") digits = digits[..4] + "01" + digits[6..];
        if (digits[6..8] == "00") digits = digits[..6] + "01" + digits[8..];
        if (!DateTime.TryParseExact(digits[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;

        var offset = TimeSpan.Zero;
        string rest = s[Math.Min(s.Length, s.TakeWhile(char.IsDigit).Count())..];
        if (rest.Length > 0 && (rest[0] == '+' || rest[0] == '-'))
        {
            string[] parts = rest[1..].Split('\'', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && int.TryParse(parts[0], out int h))
            {
                int m = parts.Length > 1 && int.TryParse(parts[1], out int mm) ? mm : 0;
                offset = new TimeSpan(h, m, 0);
                if (rest[0] == '-') offset = -offset;
            }
        }
        try
        {
            return new DateTimeOffset(local, offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
