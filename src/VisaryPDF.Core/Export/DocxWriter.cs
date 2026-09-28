using System.IO.Compression;
using System.Text;
using System.Xml;

namespace VisaryPDF.Core.Export;

/// <summary>
/// Writes a minimal Word document (paragraphs and page breaks only) without any Office SDK.
/// </summary>
public static class DocxWriter
{
    private const string ContentTypes =
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""";

    private const string Relationships =
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""";

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Writes pages of paragraphs; a page break separates pages.</summary>
    public static void Write(Stream output, IReadOnlyList<IReadOnlyList<string>> pages)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(zip, "[Content_Types].xml", ContentTypes);
        WriteEntry(zip, "_rels/.rels", Relationships);

        var entry = zip.CreateEntry("word/document.xml", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        xml.WriteStartDocument(true);
        xml.WriteStartElement("w", "document", W);
        xml.WriteStartElement("w", "body", W);
        for (int p = 0; p < pages.Count; p++)
        {
            foreach (string paragraph in pages[p])
            {
                xml.WriteStartElement("w", "p", W);
                xml.WriteStartElement("w", "r", W);
                xml.WriteStartElement("w", "t", W);
                xml.WriteAttributeString("xml", "space", null, "preserve");
                xml.WriteString(Clean(paragraph));
                xml.WriteEndElement();
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            if (p < pages.Count - 1)
            {
                xml.WriteStartElement("w", "p", W);
                xml.WriteStartElement("w", "r", W);
                xml.WriteStartElement("w", "br", W);
                xml.WriteAttributeString("w", "type", W, "page");
                xml.WriteEndElement();
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
        }
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    // XML 1.0 forbids most control characters, which PDF text extraction can produce.
    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '\t' || c >= ' ' && c != '￾' && c != '￿') sb.Append(c);
        }
        return sb.ToString();
    }
}
