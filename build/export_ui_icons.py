"""Builds the toolbar icons from Microsoft's Fluent UI System Icons (MIT license).

Official "color" icons (with Microsoft's gradients) are used as they are. For actions that have no
color variant, the official "filled" glyph is painted with one of the gradient ramps Microsoft uses
in the color set, so every icon shares the same look.

Two sets are written, tuned for contrast (WCAG 2.1 asks for 3:1 for icons against their background):
  Assets/Icons/<name>.svg        dark and black themes: colors darker than the floor are lifted
  Assets/Icons/Light/<name>.svg  light theme: bright colors are compressed into a darker range
Colors keep their hue; only their luminance changes. Themes/Icons.xaml maps "Icon.<name>" to the
right file for the current theme (used by menu icons); the AppIcon control picks the file itself.

Usage:
  python build/export_ui_icons.py [path/to/@fluentui/svg-icons/icons]
The path is only needed to refresh assets/ui-icons/fluent (vendored copies of the used SVGs).
Needs only the Python standard library.
"""
import re
import shutil
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
VENDOR = ROOT / "assets" / "ui-icons" / "fluent"
OUT = ROOT / "src" / "VisyaDocs.App" / "Assets" / "Icons"
XAML = ROOT / "src" / "VisyaDocs.App" / "Themes" / "Icons.xaml"

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


# Contrast tuning ---------------------------------------------------------------------------

def _linear(v: int) -> float:
    c = v / 255
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def _encode(c: float) -> int:
    c = 12.92 * c if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055
    return max(0, min(255, round(c * 255)))


def retone(color: str, curve) -> str:
    """Moves a color to the luminance the curve gives, keeping its hue and saturation."""
    h = color.lstrip("#")
    if len(h) == 3:
        h = "".join(ch * 2 for ch in h)
    rgb = [_linear(int(h[i:i + 2], 16)) for i in (0, 2, 4)]
    lum = 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2]
    target = curve(lum)
    if target < lum:
        rgb = [c * target / lum for c in rgb]  # darker: scale linear light, same chromaticity
    elif target > lum:
        t = (target - lum) / (1 - lum)
        rgb = [c + t * (1 - c) for c in rgb]  # lighter: mix toward white
    return "#%02x%02x%02x" % tuple(_encode(c) for c in rgb)


def light_curve(lum: float, knee: float = 0.16, top: float = 0.30) -> float:
    # On the light grey panes (#EDEDED), bright colors are compressed into [knee, top].
    return lum if lum <= knee else knee + (lum - knee) * (top - knee) / (1 - knee)


def dark_curve(lum: float, floor: float = 0.20) -> float:
    # On dark and black panes, colors darker than the floor are lifted towards it.
    return lum if lum >= floor else floor - (floor - lum) * 0.35


def tuned(svg: str, curve) -> str:
    return re.sub(r"#[0-9a-fA-F]{6}\b|#[0-9a-fA-F]{3}\b", lambda m: retone(m.group(0), curve), svg)


def write_xaml() -> None:
    def entries(folder: str) -> str:
        return "\n".join(
            f'            <SvgImageSource x:Key="Icon.{name}" UriSource="ms-appx:///Assets/Icons/{folder}{name}.svg" />'
            for name in ICONS)
    XAML.write_text(f"""<!-- Generated by build/export_ui_icons.py. Menu icons: {{ThemeResource Icon.<name>}}. -->
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <ResourceDictionary.ThemeDictionaries>
        <ResourceDictionary x:Key="Light">
{entries("Light/")}
        </ResourceDictionary>
        <ResourceDictionary x:Key="Default">
{entries("")}
        </ResourceDictionary>
    </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
""")


def main() -> None:
    if len(sys.argv) > 1:
        vendor(Path(sys.argv[1]))
    light = OUT / "Light"
    light.mkdir(parents=True, exist_ok=True)
    for old in [*OUT.glob("*.png"), *OUT.glob("*.svg"), *light.glob("*.svg")]:
        old.unlink()
    for name, (icon, style) in ICONS.items():
        text = svg_for(icon, style)
        (OUT / f"{name}.svg").write_text(tuned(text, dark_curve))
        (light / f"{name}.svg").write_text(tuned(text, light_curve))
    write_xaml()
    print(f"{len(ICONS)} icons (dark and light sets) -> {OUT}")


if __name__ == "__main__":
    main()
