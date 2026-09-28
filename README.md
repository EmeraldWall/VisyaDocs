# VisyaDocs

A modern, lightweight PDF reader, editor and converter for Windows, built with WinUI 3. Scanned PDFs can be turned into searchable, copyable text with the OCR engine that is already part of Windows.

![VisyaDocs icon](src/VisyaDocs.App/Assets/Square44x44Logo.targetsize-256_altform-unplated.png)

## Features

**Reading first**
- Thin title bar (the height of the window buttons) with a File menu and compact document tabs that shrink and scroll instead of running under the window buttons
- Reading and editing tools live in a floating, rounded tool bar that folds away to a small handle and can be dragged to either side. Pages are laid out around it, so it never covers them or the side panes
- Page and zoom indicator that fades out while you read
- Layouts: continuous scroll, two pages side by side, two pages with the cover alone, single page (flip with PageUp/PageDown, arrow keys or the wheel)
- Pinch to zoom on touchpads and touch screens, Ctrl + wheel, Ctrl+plus/minus, fit width, fit page
- Full screen (F11, Esc to leave)
- Thumbnails that follow the page you are reading, and an Outline tab with the PDF's bookmarks (table of contents)
- Links work: internal links jump to their page, web links ask before opening in your browser
- Search (Ctrl+F) with highlighted matches, Match case and Whole word options, text selection and copy
- Reopening a file returns to the page and zoom where you left off
- Keyboard shortcuts list (F1 or File > Keyboard shortcuts)
- Document properties (Ctrl+D): title, author, dates, PDF version, page size, security and permissions

**Pages**
- Right click a thumbnail to rotate a page, delete it, or extract pages into a new PDF (opens in a new tab). All undoable

**Password protected PDFs**
- Opening asks for the password. Saving keeps the protection
- File > "Save a copy without password" writes an unprotected copy, offered only for files you opened with their password. The original is not changed
- Restrictions set by the author (no printing, copying or editing) are respected: those tools are disabled with a short explanation
- Adding a password is not possible: PDFium, the PDF engine, can read encryption but cannot write it

**Fill and sign**
- **Fill PDF forms**: text fields, check boxes, radio buttons and drop-downs are filled in place and saved into the PDF
- **E-sign**: draw a signature (mouse, pen or finger), type it in a handwriting font, or use an image. Move and resize it on the page, optionally add the date. Up to three signatures are remembered. Signature fields in forms offer "Sign here"
- (This is a visual signature, like "Fill & Sign". Certificate based digital signatures are not included.)

**Edit**
- **Edit text**: click existing text and change it in place. The original embedded font is kept when it can show the new text, otherwise the run is rebuilt with a matching standard font at the same position, size and color
- **Add text**: click anywhere, type (multi line), choose size and color
- **Comments** (sticky notes) and **highlights**, with a comments pane
- Undo / redo for every edit, safe save (writes a temporary file first)

**Print**
- Standard Windows print dialog (printer, page range, current page, copies). Pages print as sharp vector output, landscape pages turn to fill the sheet, comments, highlights and form values are included

**Convert**
- **OCR**: "Make searchable" adds an invisible text layer to scanned pages. "Extract text" shows all text, using OCR for scanned pages
- Export to Word (.docx), plain text (.txt), PNG or JPEG images (with page ranges and DPI). Scanned pages are recognized automatically during export
- Images to PDF (JPG, PNG, TIFF, HEIC, ...). JPEGs are embedded without re-encoding
- Merge PDFs, append pages or images to an open document

**Look and feel**
- Themes: System (follows Windows), Light, Dark, Black
  - Light is a soft grey instead of stark white, to reduce eye strain
  - Dark is the Windows style dark grey; Black is pure black (great on OLED screens)
  - Pages can be dimmed slightly in Dark and Black for night reading
- Settings is a page inside the app (File > Settings), every change applies at once
- Icons are vector (SVG) and render sharp at any display scale
- Color icons in Microsoft's Fluent style (see Third party notices)
- Custom app icon and .pdf file icon (sources in `assets/icon`)
- One window: opening another PDF from Explorer adds a tab to the running window
- Settings > "Open PDFs with VisyaDocs" registers the app for .pdf files (current user, no admin) and opens Windows Default apps so you can pick it
- Tool buttons have accessible names for screen readers

## Why it is light

| Part | Choice |
|---|---|
| UI | WinUI 3 (Windows App SDK 1.8, only the WinUI and Foundation packages, no AI/ML runtimes) |
| PDF engine | [PDFium](https://pdfium.googlesource.com/pdfium/) via a thin P/Invoke layer, one native DLL |
| OCR | `Windows.Media.Ocr`, built into Windows, uses the OCR language packs you already have |
| Images | Windows Imaging Component, built into Windows |
| Word export | A small OOXML writer, no Office SDK |
| Runtime | .NET 10, Native AOT, trimmed, self contained: no .NET install needed |

Measured on CI (x64, Native AOT, no debug symbols): the app folder is about 67 MB (29 MB zipped). About 45 MB of that is the bundled WinUI runtime, which is what lets the app run from a folder with nothing to install; VisyaDocs itself is about 8 MB and PDFium about 7 MB. The CI job prints the size in its summary.

## Requirements

- Windows 10 1809 (build 17763) or newer, or Windows 11. x64 or ARM64.
- For OCR: at least one Windows language with the "Optical character recognition" feature (Settings > Time & language > Language & region > language options). English is present on most systems.

## Build

Needs the .NET 10 SDK. Visual Studio 2022/2026 with the "Windows application development" workload is the easiest way to run and debug.

```powershell
# Run tests (the PDFium binary for your machine is downloaded automatically on first build)
dotnet test tests/VisyaDocs.Core.Tests
dotnet test tests/VisyaDocs.Platform.Tests

# Build and run the app
dotnet build src/VisyaDocs.App -p:Platform=x64
.\src\VisyaDocs.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\VisyaDocs.exe

# Publish a self contained Native AOT build to artifacts\VisyaDocs-win-x64
dotnet publish src/VisyaDocs.App -c Release -p:Platform=x64 -p:PublishProfile=win-x64
```

Use `ARM64` / `win-ARM64` for ARM devices. An MSIX package (with the .pdf file association and icon) can be produced with `-p:WindowsPackageType=MSIX`.

The core library (`VisyaDocs.Core`) has no Windows dependencies, so its tests also run on Linux and macOS.

### Regenerating icons

Toolbar icons: `python build/export_ui_icons.py` (needs `pip install cairosvg`).

App icon:

Edit the SVGs in `assets/icon`, then:

```bash
pip install cairosvg pillow
python build/export_icons.py
```

## Project layout

```
src/VisyaDocs.Core      PDFium interop, document model, editing, export (PNG, DOCX, TXT)
src/VisyaDocs.Platform  Windows OCR and image codecs
src/VisyaDocs.App       WinUI 3 app: shell, viewer, tools, themes, icons
tests/                  xUnit tests for Core (cross platform) and Platform (Windows)
build/                  PDFium download targets, icon export script
```

## Known limitations

- Text editing works on text runs as the PDF stores them. Some PDFs store one word or even one character per run, so an edit may cover a smaller piece than a whole line.
- When the embedded font cannot show the new characters, the replacement uses Helvetica (or Arial for non Latin text), so the look can differ slightly from the original.
- Text written into CJK scripts needs a font that covers them; Arial is used as the fallback.
- Undo history is limited to 30 steps and about 256 MB, so very large files keep fewer steps.
- Password protection cannot be added (see above).

## Third party notices

Toolbar icons are built from Microsoft's [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) (MIT license, see `assets/ui-icons/fluent/LICENSE`). Official color icons are used as they are; icons without a color variant are recolored with the same gradient ramps by `build/export_ui_icons.py`.

## License of dependencies

PDFium binaries come from [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) (Apache 2.0 / BSD). Windows App SDK and CommunityToolkit.Mvvm are MIT licensed.
