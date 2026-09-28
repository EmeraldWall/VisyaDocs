namespace VisyaDocs.Core;

/// <summary>A rectangle in PDF page space (points, origin bottom left, Y up).</summary>
public readonly record struct PdfRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Bottom && y <= Top;

    public PdfRect Inflate(double d) => new(Left - d, Bottom - d, Right + d, Top + d);

    public static PdfRect Union(IEnumerable<PdfRect> rects)
    {
        double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
        foreach (var x in rects)
        {
            l = Math.Min(l, x.Left); b = Math.Min(b, x.Bottom);
            r = Math.Max(r, x.Right); t = Math.Max(t, x.Top);
        }
        return l > r ? default : new PdfRect(l, b, r, t);
    }
}

/// <summary>A rectangle in view space (points at 100% zoom, origin top left, Y down).</summary>
public readonly record struct ViewRect(double X, double Y, double Width, double Height)
{
    public ViewRect Scale(double s) => new(X * s, Y * s, Width * s, Height * s);
}

/// <summary>An RGB color with 0 to 255 channels.</summary>
public readonly record struct PdfColor(byte R, byte G, byte B)
{
    public static readonly PdfColor Black = new(0, 0, 0);
    public static readonly PdfColor Highlight = new(255, 226, 64);
    public static readonly PdfColor Note = new(255, 200, 0);
}

public sealed record TextStyle(double FontSize = 12, PdfColor? Color = null, string StandardFont = "Helvetica")
{
    public PdfColor TextColor => Color ?? PdfColor.Black;
}

/// <summary>A text object on a page that can be edited in place.</summary>
public sealed record TextObjectInfo(int PageIndex, int ObjectIndex, string Text, PdfRect Bounds, double FontSize);

public enum AnnotationKind
{
    Note,
    Highlight,
    Other,
}

public sealed record AnnotationInfo(
    int PageIndex, int Index, AnnotationKind Kind, PdfRect Bounds, string Contents, string Author);

public sealed record SearchHit(int PageIndex, int CharIndex, int Length, IReadOnlyList<PdfRect> Rects);

public sealed record TextSelection(int PageIndex, int Start, int Count, string Text, IReadOnlyList<PdfRect> Rects);

/// <summary>A recognized word positioned in page space, used to build a searchable text layer.</summary>
public sealed record OcrWord(string Text, PdfRect Bounds);

/// <summary>What a (possibly protected) document allows.</summary>
public readonly record struct PdfPermissions(bool CanPrint, bool CanCopy, bool CanModify)
{
    public bool All => CanPrint && CanCopy && CanModify;
}

public class PdfException(string message) : Exception(message);

public sealed class PdfPasswordException() : PdfException("This PDF is protected. Enter the password to open it.");
