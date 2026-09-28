"""Regenerates the app icon files from the logo master in assets/icon.

Usage: python build/export_icons.py   (needs: pip install cairosvg pillow)

Sources:
  assets/icon/visyadocs.png   the logo (large, transparent background); every size is scaled from it
  assets/icon/pdf-badge.svg   the "PDF" tag added for the .pdf file icon

Writes into src/VisyaDocs.App/Assets:
  VisyaDocs.ico       EXE, window and taskbar icon (16 to 256 px)
  PdfFile.ico         icon for .pdf files associated with the app
  AppLogo.png         in-app logo (home screen), AppLogoSmall.png (title bar)
  *.png               MSIX logo set (tiles, store logo, splash screen, target sizes)
"""
import io
import struct
from pathlib import Path

import cairosvg
from PIL import Image, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "assets" / "icon"
OUT = ROOT / "src" / "VisyaDocs.App" / "Assets"


def master() -> Image.Image:
    """The logo cropped to its content and centered on a square transparent canvas."""
    logo = Image.open(SRC / "visyadocs.png").convert("RGBA")
    logo = logo.crop(logo.getchannel("A").getbbox())
    side = max(logo.size)
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.alpha_composite(logo, ((side - logo.width) // 2, (side - logo.height) // 2))
    return square


MASTER = master()


def with_badge(image: Image.Image) -> Image.Image:
    """The logo with a "PDF" tag in the bottom right corner (file type icon)."""
    size = image.width
    width = round(size * 0.62)
    png = cairosvg.svg2png(url=str(SRC / "pdf-badge.svg"), output_width=width)
    badge = Image.open(io.BytesIO(png)).convert("RGBA")
    out = image.copy()
    out.alpha_composite(badge, (size - badge.width, size - badge.height))
    return out


PDF_MASTER = with_badge(MASTER)


def render(size: int, pdf: bool = False) -> Image.Image:
    image = (PDF_MASTER if pdf else MASTER).resize((size, size), Image.LANCZOS)
    # Small sizes lose contrast when scaled down this far; a light sharpen keeps the page and lines crisp.
    if size <= 32:
        image = image.filter(ImageFilter.UnsharpMask(radius=0.6, percent=60, threshold=0))
    return image


def write_ico(path: Path, pdf: bool = False, sizes=(16, 20, 24, 32, 40, 48, 64, 256)) -> None:
    images = []
    for s in sizes:
        buf = io.BytesIO()
        render(s, pdf).save(buf, "PNG", optimize=True)
        images.append((s, buf.getvalue()))
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, data = b"", b""
    for s, png in images:
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(png), offset + len(data))
        data += png
    path.write_bytes(header + entries + data)


def write_png(name: str, width: int, height: int, logo: int, pdf: bool = False) -> None:
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    canvas.alpha_composite(render(logo, pdf), ((width - logo) // 2, (height - logo) // 2))
    canvas.save(OUT / name, "PNG", optimize=True)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    write_ico(OUT / "VisyaDocs.ico")
    write_ico(OUT / "PdfFile.ico", pdf=True, sizes=(16, 24, 32, 48, 64, 256))

    # MSIX visual assets (scale-200 plus unplated target sizes for the taskbar and Start).
    write_png("Square44x44Logo.scale-200.png", 88, 88, 80)
    for s in (16, 24, 32, 48, 256):
        write_png(f"Square44x44Logo.targetsize-{s}_altform-unplated.png", s, s, s)
    write_png("Square150x150Logo.scale-200.png", 300, 300, 200)
    write_png("Wide310x150Logo.scale-200.png", 620, 300, 220)
    write_png("LockScreenLogo.scale-200.png", 48, 48, 48)
    write_png("StoreLogo.png", 50, 50, 50)
    write_png("SplashScreen.scale-200.png", 1240, 600, 360)
    write_png("PdfFileLogo.png", 64, 64, 64, pdf=True)
    # In-app logos, large enough to stay sharp up to 400% display scale.
    write_png("AppLogo.png", 256, 256, 256)        # home screen, shown at 64 DIPs
    write_png("AppLogoSmall.png", 64, 64, 64)      # title bar, shown at 16 DIPs


if __name__ == "__main__":
    main()
