"""Builds the toolbar icons from Microsoft's Fluent UI System Icons (MIT license).

Official "color" icons (with Microsoft's gradients) are used as they are. For actions that have no
color variant, the official "filled" glyph is painted with one of the gradient ramps Microsoft uses
in the color set, so every icon shares the same look.

Usage:
  python build/export_ui_icons.py [path/to/@fluentui/svg-icons/icons]
The path is only needed to refresh assets/ui-icons/fluent (vendored copies of the used SVGs).
Needs: pip install cairosvg
"""
import re
import shutil
import sys
from pathlib import Path

import cairosvg

ROOT = Path(__file__).resolve().parent.parent
VENDOR = ROOT / "assets" / "ui-icons" / "fluent"
OUT = ROOT / "src" / "VisyaDocs.App" / "Assets" / "Icons"
BASE = 20  # logical size in DIPs
# None = the unqualified file (100%), which also works if resource lookup falls back to plain files.
SCALES = {None: 1.0, 125: 1.25, 150: 1.5, 200: 2.0, 300: 3.0, 400: 4.0}

# Gradient ramps sampled from Microsoft's Fluent color icons (top-left to bottom-right).
RAMPS = {
    "blue": ("#0FAFFF", "#2052CB"),
    "sky": ("#6CE0FF", "#4894FE"),
    "teal": ("#20AC9D", "#2052CB"),
    "green": ("#52D17C", "#22918B"),
    "orange": ("#FAB500", "#FE8401"),
    "coral": ("#FFA43D", "#FB5937"),
    "magenta": ("#F97DBD", "#DD3CE2"),
    "grey": ("#B9C0C7", "#70777D"),
    "yellow": ("#FFCD0F", "#FE8401"),
}

# app icon name: (fluent icon, "color" or a ramp name)
ICONS = {
    "open": ("folder_open", "yellow"),
    "save": ("save", "blue"),
    "print": ("print", "sky"),
    "properties": ("info", "blue"),
    "file": ("document", "color"),
    "thumbnails": ("apps_list", "color"),
    "search": ("search_visual", "color"),
    "view-continuous": ("document_one_page_multiple", "sky"),
    "view-two-page": ("book_open", "color"),
    "view-single": ("document_one_page", "sky"),
    "zoom-in": ("zoom_in", "blue"),
    "zoom-out": ("zoom_out", "blue"),
    "fit-width": ("arrow_autofit_width", "teal"),
    "fit-page": ("arrow_autofit_content", "teal"),
    "fullscreen": ("full_screen_maximize", "teal"),
    "fullscreen-exit": ("full_screen_minimize", "teal"),
    "select": ("cursor", "grey"),
    "edit-text": ("text_edit_style", "color"),
    "add-text": ("text_add_t", "magenta"),
    "comment": ("comment", "color"),
    "comments": ("comment_multiple", "color"),
    "highlight": ("highlight", "yellow"),
    "sign": ("signature", "magenta"),
    "form": ("form", "color"),
    "undo": ("arrow_undo", "sky"),
    "redo": ("arrow_redo", "sky"),
    "convert": ("arrow_sync", "color"),
    "ocr": ("scan_text", "green"),
    "extract-text": ("text_bullet_list_square", "color"),
    "word": ("document_text", "color"),
    "text": ("text_description", "grey"),
    "images": ("image", "color"),
    "merge": ("merge", "green"),
    "append-images": ("image_multiple", "blue"),
    "create": ("document_add", "color"),
    "recent": ("history", "color"),
    "settings": ("settings", "color"),
    "theme": ("dark_theme", "magenta"),
    "more": ("more_horizontal", "grey"),
    "pdf": ("document_pdf", "coral"),
}


def source_name(icon: str, style: str) -> str:
    return f"{icon}_24_{'color' if style == 'color' else 'filled'}.svg"


def vendor(from_dir: Path) -> None:
    VENDOR.mkdir(parents=True, exist_ok=True)
    for icon, style in ICONS.values():
        shutil.copy(from_dir / source_name(icon, style), VENDOR / source_name(icon, style))


def svg_for(icon: str, style: str) -> str:
    svg = (VENDOR / source_name(icon, style)).read_text()
    if style == "color":
        return svg
    start, end = RAMPS[style]
    gradient = (
        f'<defs><linearGradient id="g" x1="2" y1="2" x2="22" y2="22" gradientUnits="userSpaceOnUse">'
        f'<stop stop-color="{start}"/><stop offset="1" stop-color="{end}"/></linearGradient></defs>'
    )
    svg = re.sub(r"<svg([^>]*)>", lambda m: f'<svg{m.group(1)} fill="url(#g)">{gradient}', svg, count=1)
    return svg


def main() -> None:
    if len(sys.argv) > 1:
        vendor(Path(sys.argv[1]))
    OUT.mkdir(parents=True, exist_ok=True)
    for old in [*OUT.glob("*.png"), *OUT.glob("*.svg")]:
        old.unlink()
    for name, (icon, style) in ICONS.items():
        text = svg_for(icon, style)
        # The SVG is what the app shows (vector, sharp at any scale); PNGs are a fallback.
        (OUT / f"{name}.svg").write_text(text)
        svg = text.encode()
        for scale, factor in SCALES.items():
            size = round(BASE * factor)
            file = f"{name}.png" if scale is None else f"{name}.scale-{scale}.png"
            cairosvg.svg2png(bytestring=svg, write_to=str(OUT / file),
                             output_width=size, output_height=size)
    print(f"{len(ICONS)} icons x {len(SCALES)} scales -> {OUT}")


if __name__ == "__main__":
    main()
