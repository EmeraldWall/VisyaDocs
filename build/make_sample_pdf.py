"""Writes a text-heavy sample PDF used by the CI memory check.

Usage: python build/make_sample_pdf.py out.pdf [pages]
Needs: pip install reportlab
"""
import sys

from reportlab.lib.pagesizes import A4
from reportlab.pdfgen import canvas

WORDS = ("reading viewer page zoom memory layout render bitmap scroll document text search "
         "comment highlight sign form print convert export thumbnail outline").split()


def main() -> None:
    out = sys.argv[1]
    pages = int(sys.argv[2]) if len(sys.argv) > 2 else 80
    c = canvas.Canvas(out, pagesize=A4)
    width, height = A4
    for n in range(pages):
        c.setFont("Helvetica-Bold", 20)
        c.drawString(56, height - 72, f"Sample page {n + 1}")
        c.setFont("Helvetica", 10.5)
        y = height - 104
        line = 0
        while y > 72:
            text = " ".join(WORDS[(line * 7 + i + n) % len(WORDS)] for i in range(14))
            c.drawString(56, y, text)
            y -= 14
            line += 1
        c.setFillColorRGB(0.2 + (n % 5) * 0.15, 0.4, 0.8)
        c.circle(width - 90, height - 80, 22, fill=1, stroke=0)
        c.setFillColorRGB(0, 0, 0)
        c.showPage()
    c.save()


if __name__ == "__main__":
    main()
