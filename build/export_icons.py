"""Regenerates the app icon files from the SVG sources in assets/icon.

Usage: python build/export_icons.py   (needs: pip install cairosvg pillow)

Writes into src/VisyaDocs.App/Assets:
  VisyaDocs.ico       EXE, window and taskbar icon (16 to 256 px)
  PdfFile.ico         icon for .pdf files associated with the app
  *.png               MSIX logo set (tiles, store logo, splash screen, target sizes)
"""
import io
import shutil
import struct
from pathlib import Path

import cairosvg
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "assets" / "icon"
OUT = ROOT / "src" / "VisyaDocs.App" / "Assets"


def render(svg: str, size: int) -> Image.Image:
    # Small sizes use the simplified drawing so the mark stays crisp.
    name = "visyadocs-small.svg" if svg == "visyadocs.svg" and size <= 24 else svg
    png = cairosvg.svg2png(url=str(SRC / name), output_width=size, output_height=size)
    return Image.open(io.BytesIO(png)).convert("RGBA")


def write_ico(path: Path, svg: str, sizes=(16, 20, 24, 32, 40, 48, 64, 256)) -> None:
    images = []
    for s in sizes:
        buf = io.BytesIO()
        render(svg, s).save(buf, "PNG", optimize=True)
        images.append((s, buf.getvalue()))
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, data = b"", b""
    for s, png in images:
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(png), offset + len(data))
        data += png
    path.write_bytes(header + entries + data)


def write_png(name: str, width: int, height: int, logo: int, svg: str = "visyadocs.svg") -> None:
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    mark = render(svg, logo)
    canvas.alpha_composite(mark, ((width - logo) // 2, (height - logo) // 2))
    canvas.save(OUT / name, "PNG", optimize=True)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    write_ico(OUT / "VisyaDocs.ico", "visyadocs.svg")
    write_ico(OUT / "PdfFile.ico", "visyadocs-pdf.svg", sizes=(16, 24, 32, 48, 64, 256))

    # MSIX visual assets (scale-200 plus unplated target sizes for the taskbar and Start).
    write_png("Square44x44Logo.scale-200.png", 88, 88, 80)
    for s in (16, 24, 32, 48, 256):
        write_png(f"Square44x44Logo.targetsize-{s}_altform-unplated.png", s, s, s)
    write_png("Square150x150Logo.scale-200.png", 300, 300, 200)
    write_png("Wide310x150Logo.scale-200.png", 620, 300, 220)
    write_png("LockScreenLogo.scale-200.png", 48, 48, 48)
    write_png("StoreLogo.png", 50, 50, 50)
    write_png("SplashScreen.scale-200.png", 1240, 600, 360)
    write_png("PdfFileLogo.png", 64, 64, 64, "visyadocs-pdf.svg")
    write_png("AppLogo.png", 96, 96, 96)
    # Vector copies for in-app use (title bar, home page): sharp at every display scale.
    shutil.copy(SRC / "visyadocs.svg", OUT / "AppLogo.svg")
    shutil.copy(SRC / "visyadocs-small.svg", OUT / "AppLogoSmall.svg")


if __name__ == "__main__":
    main()
