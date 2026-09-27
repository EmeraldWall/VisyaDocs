using System.IO.Compression;
using VisyaDocs.Core.Export;

namespace VisyaDocs.Core.Tests;

public class ConverterTests
{
    [Theory]
    [InlineData("", 5, new[] { 0, 1, 2, 3, 4 })]
    [InlineData("1-2, 4", 5, new[] { 0, 1, 3 })]
    [InlineData(" 3 ", 5, new[] { 2 })]
    public void ParsesPageRanges(string range, int count, int[] expected) =>
        Assert.Equal(expected, Converter.ParsePageRange(range, count));

    [Theory]
    [InlineData("0")]
    [InlineData("2-1")]
    [InlineData("1-9")]
    [InlineData("a")]
    public void RejectsInvalidPageRanges(string range) =>
        Assert.Throws<FormatException>(() => Converter.ParsePageRange(range, 5));

    [Fact]
    public void ParagraphsJoinWrappedLinesAndSplitOnBlankLines()
    {
        string text = "This is a long wrapped line of text that\r\ncontinues here and ends.\r\n\r\nShort title\r\nNew para-\r\ngraph here.";
        var p = Converter.ToParagraphs(text);
        Assert.Equal(
            ["This is a long wrapped line of text that continues here and ends.", "Short title New paragraph here."], p);
    }

    [Fact]
    public void ExportsTextDocxAndPng()
    {
        using var doc = PdfDocumentTests.CreateDocWithText("Export me");
        string dir = Path.Combine(Path.GetTempPath(), $"visya-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var pages = Converter.ParsePageRange(null, doc.PageCount);

            string txt = Path.Combine(dir, "out.txt");
            Converter.ExportText(doc, txt, pages);
            Assert.Contains("Export me", File.ReadAllText(txt));

            string docx = Path.Combine(dir, "out.docx");
            Converter.ExportDocx(doc, docx, pages);
            using (var zip = ZipFile.OpenRead(docx))
            {
                Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
                using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
                Assert.Contains("Export me", reader.ReadToEnd());
            }

            var progress = new List<double>();
            var files = Converter.ExportImages(doc, dir, "page", pages, ExportFormat.Png, dpi: 72,
                progress: new SyncProgress(progress.Add));
            var png = File.ReadAllBytes(Assert.Single(files));
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
            Assert.Equal(595, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
            Assert.Equal([1.0], progress);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
