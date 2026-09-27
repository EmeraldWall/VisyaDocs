# VisyaDocs

A modern, lightweight PDF reader, editor and converter for Windows, built with WinUI 3. Scanned PDFs can be turned into searchable, copyable text with the OCR engine that is already part of Windows.

![VisyaDocs icon](src/VisyaDocs.App/Assets/Square44x44Logo.targetsize-256_altform-unplated.png)

## Features

**Read**
- Tabs for several documents, drag and drop, recent files, "Open with" from Explorer
- Smooth scrolling with lazy page rendering (memory stays flat on long documents)
- Zoom: fit width, fit page, presets, Ctrl + mouse wheel, Ctrl + plus/minus
- Page thumbnails, go to page, search with highlighted matches (Enter / Shift+Enter)
- Select text (drag, double click for a word, Ctrl+A for the page) and copy

**Edit**
- **Edit text**: click existing text and change it in place. The original embedded font is kept when it can show the new text, otherwise the run is rebuilt with a matching standard font at the same position, size and color
- **Add text**: click anywhere, type (multi line), choose size and color
- **Comments**: sticky note comments with author and date, editable and deletable, plus highlights (with optional comment) on selected text
- Comments pane listing every comment in the document
- Undo / redo for every edit, safe save (writes a temporary file first)

**Convert**
- **OCR**: "Make searchable" adds an invisible text layer to scanned pages, so they can be searched, selected and copied. "Extract text" shows all text, using OCR for scanned pages
- Export to Word (.docx), plain text (.txt), PNG or JPEG images (with page ranges and DPI). Scanned pages are recognized automatically during export
- Images to PDF (JPG, PNG, TIFF, HEIC, ...). JPEGs are embedded without re-encoding
- Merge PDFs, append pages or images to an open document

**Look and feel**
- Theme: System, Light or Dark (title bar icon or Settings)
  - Light is a soft grey instead of stark white, to reduce eye strain
  - Dark uses pure black with dark grey panes, and optionally dims pages for night reading
- Fluent icons for every command (the Windows system icon font, so no extra size)
- Custom app icon and .pdf file icon (sources in `assets/icon`)

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
- No printing yet.

## License of dependencies

PDFium binaries come from [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) (Apache 2.0 / BSD). Windows App SDK and CommunityToolkit.Mvvm are MIT licensed.
