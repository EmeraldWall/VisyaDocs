"""Checks the MSIX images referenced by Package.appxmanifest the way Visual Studio's manifest
designer does: every referenced image must exist (plain or with scale/targetsize qualifiers) and
each scale-qualified file must have the exact pixel size for its scale. Store limit: 200 KB each.

Usage: python build/check_assets.py   (needs: pip install pillow)
"""
import re
import sys
from pathlib import Path

from PIL import Image

APP = Path(__file__).resolve().parent.parent / "src" / "VisaryPDF.App"
# Base size (scale 100) of each manifest image slot.
BASE = {"Square44x44Logo": (44, 44), "Square150x150Logo": (150, 150), "Wide310x150Logo": (310, 150),
        "StoreLogo": (50, 50), "SplashScreen": (620, 300), "LockScreenLogo": (24, 24), "PdfFileLogo": (44, 44)}


def main() -> int:
    manifest = (APP / "Package.appxmanifest").read_text(encoding="utf-8")
    errors = []
    for ref in sorted(set(re.findall(r"Assets\\([A-Za-z0-9]+)\.png", manifest))):
        files = [p for p in (APP / "Assets").glob(f"{ref}*.png") if re.fullmatch(rf"{ref}(\..+)?\.png", p.name)]
        if not files:
            errors.append(f"{ref}.png: no image found")
            continue
        for file in files:
            size = Image.open(file).size
            if file.stat().st_size > 200_000:
                errors.append(f"{file.name}: larger than 200 KB")
            scale = re.search(r"\.scale-(\d+)\.png$", file.name)
            target = re.search(r"\.targetsize-(\d+)", file.name)
            if scale and ref in BASE:
                w, h = BASE[ref]
                f = int(scale.group(1)) / 100
                expected = (round(w * f), round(h * f))
            elif target:
                expected = (int(target.group(1)),) * 2
            elif ref in BASE:
                expected = BASE[ref]  # no qualifier means scale 100
            else:
                continue
            if size != expected:
                errors.append(f"{file.name}: {size[0]}x{size[1]}, expected {expected[0]}x{expected[1]}")
    for e in errors:
        print("ERROR", e)
    print("Manifest images OK" if not errors else f"{len(errors)} problem(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
