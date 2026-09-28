namespace VisaryPDF.Core;

/// <summary>
/// Maps between PDF page space and view space for one page. View space is what the
/// viewer lays out: points at 100% zoom, origin top left, page rotation applied.
/// </summary>
public sealed class PageGeometry
{
    // view = [A C] * page + [E]
    //        [B D]          [F]
    private readonly double _a, _b, _c, _d, _e, _f;

    internal PageGeometry(double width, double height, double a, double b, double c, double d, double e, double f)
    {
        ViewWidth = width;
        ViewHeight = height;
        (_a, _b, _c, _d, _e, _f) = (a, b, c, d, e, f);
    }

    public double ViewWidth { get; }
    public double ViewHeight { get; }

    public (double X, double Y) ToView(double x, double y) => (_a * x + _c * y + _e, _b * x + _d * y + _f);

    public (double X, double Y) ToPage(double vx, double vy)
    {
        double det = _a * _d - _b * _c;
        double x = vx - _e, y = vy - _f;
        return ((_d * x - _c * y) / det, (-_b * x + _a * y) / det);
    }

    /// <summary>
    /// Matrix (a, b, c, d, e, f) that places an object whose local unit square should appear as
    /// the given view rectangle, upright on screen whatever the page rotation. Local x runs right
    /// and local y runs up, as PDF objects expect.
    /// </summary>
    public (double A, double B, double C, double D, double E, double F) UprightMatrix(ViewRect view)
    {
        var o = ToPage(view.X, view.Y + view.Height);          // bottom left on screen
        var x = ToPage(view.X + view.Width, view.Y + view.Height);
        var y = ToPage(view.X, view.Y);
        return (x.X - o.X, x.Y - o.Y, y.X - o.X, y.Y - o.Y, o.X, o.Y);
    }

    public ViewRect ToView(PdfRect r)
    {
        var (x1, y1) = ToView(r.Left, r.Bottom);
        var (x2, y2) = ToView(r.Right, r.Top);
        return new ViewRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    }

    public PdfRect ToPage(ViewRect r)
    {
        var (x1, y1) = ToPage(r.X, r.Y);
        var (x2, y2) = ToPage(r.X + r.Width, r.Y + r.Height);
        return new PdfRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }
}
