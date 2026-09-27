using System.Text;

namespace VisyaDocs.Core.Export;

public enum ExportFormat
{
    Text,
    Docx,
    Png,
    Jpeg,
}

/// <summary>Encodes a top-down BGRA image to JPEG. Supplied by the platform layer (Windows Imaging).</summary>
public delegate void JpegEncoder(Stream output, byte[] bgra, int width, int height);

/// <summary>Converts PDF documents to other formats.</summary>
public static class Converter
{
    /// <summary>Pages in a 1 based, comma separated range such as "1-3, 5". Empty means all pages.</summary>
    public static IReadOnlyList<int> ParsePageRange(string? range, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(range)) return Enumerable.Range(0, pageCount).ToArray();
        var pages = new List<int>();
        foreach (string part in range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length is < 1 or > 2 || !int.TryParse(ends[0], out int from) ||
                !int.TryParse(ends.Length == 2 ? ends[1] : ends[0], out int to) || from < 1 || to < from || to > pageCount)
            {
                throw new FormatException($"\"{part}\" is not a valid page range for a document with {pageCount} pages.");
            }
            for (int p = from; p <= to; p++) pages.Add(p - 1);
        }
        return pages;
    }

    public static void ExportText(PdfDocument doc, string path, IReadOnlyList<int> pages,
        IProgress<double>? progress = null, CancellationToken ct = default) =>
        WriteText(path, ExtractTexts(doc, pages, progress, ct));

    public static void ExportDocx(PdfDocument doc, string path, IReadOnlyList<int> pages,
        IProgress<double>? progress = null, CancellationToken ct = default) =>
        WriteDocx(path, ExtractTexts(doc, pages, progress, ct));

    /// <summary>Writes page texts as UTF-8 with a form feed between pages.</summary>
    public static void WriteText(string path, IReadOnlyList<string> pageTexts) =>
        File.WriteAllText(path, string.Join("\r\n\f\r\n", pageTexts), new UTF8Encoding(true));

    /// <summary>Writes page texts as a Word document, rebuilding paragraphs and breaking pages.</summary>
    public static void WriteDocx(string path, IReadOnlyList<string> pageTexts)
    {
        using var file = File.Create(path);
        DocxWriter.Write(file, pageTexts.Select(ToParagraphs).ToArray());
    }

    private static List<string> ExtractTexts(PdfDocument doc, IReadOnlyList<int> pages, IProgress<double>? progress, CancellationToken ct)
    {
        var texts = new List<string>(pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            texts.Add(doc.GetPageText(pages[i]));
            progress?.Report((i + 1.0) / pages.Count);
        }
        return texts;
    }

    /// <summary>Renders pages to images named "name-1.png" etc. and returns the written files.</summary>
    public static IReadOnlyList<string> ExportImages(PdfDocument doc, string folder, string baseName, IReadOnlyList<int> pages,
        ExportFormat format, int dpi = 150, JpegEncoder? jpeg = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (format == ExportFormat.Jpeg && jpeg is null) throw new ArgumentNullException(nameof(jpeg));
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        string ext = format == ExportFormat.Jpeg ? ".jpg" : ".png";
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var g = doc.GetGeometry(pages[i]);
            int w = Math.Max(1, (int)Math.Round(g.ViewWidth * dpi / 72));
            int h = Math.Max(1, (int)Math.Round(g.ViewHeight * dpi / 72));
            byte[] pixels = doc.RenderPage(pages[i], w, h);
            string file = Path.Combine(folder, $"{baseName}-{pages[i] + 1}{ext}");
            using (var stream = File.Create(file))
            {
                if (format == ExportFormat.Jpeg) jpeg!(stream, pixels, w, h);
                else PngEncoder.Write(stream, pixels, w, h);
            }
            written.Add(file);
            progress?.Report((i + 1.0) / pages.Count);
        }
        return written;
    }

    /// <summary>
    /// Rebuilds paragraphs from extracted lines: a line joins the previous one unless the previous
    /// line was blank or ended a sentence well short of the typical line length.
    /// </summary>
    public static IReadOnlyList<string> ToParagraphs(string pageText)
    {
        var lines = pageText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd()).ToArray();
        int typical = lines.Length == 0 ? 0 : lines.Select(l => l.Length).OrderByDescending(n => n)
            .ElementAt(Math.Min(lines.Length - 1, lines.Length / 4));
        var paragraphs = new List<string>();
        var current = new StringBuilder();
        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            if (current.Length > 0)
            {
                if (current[^1] == '-' && current.Length > 1 && char.IsLetter(current[^2])) current.Length--;
                else current.Append(' ');
            }
            current.Append(line.TrimStart());
            bool endsSentence = line.EndsWith('.') || line.EndsWith(':') || line.EndsWith('!') || line.EndsWith('?');
            if (endsSentence && line.Length < typical * 0.8) Flush();
        }
        Flush();
        return paragraphs;

        void Flush()
        {
            if (current.Length > 0) paragraphs.Add(current.ToString());
            current.Clear();
        }
    }
}
