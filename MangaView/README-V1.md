# MangaView V1.0 Release Preparation

- Version: 1.0.0
- Platform: Windows 10 / 11 x64
- Runtime: .NET 10 self-contained publish
- UI: WPF

## V1.0 Feature Summary

- Single-page, smart double-page and Webtoon reading modes.
- Folder, recursive folder, CBZ and ZIP virtual-folder browsing.
- Natural sorting, page navigation, thumbnail strip and reading progress.
- Named bookmarks, recent files, slideshow and light / dark / system themes.
- Zoom, pan, rotate, flip, animation playback and fullscreen.
- EXIF / XMP / common AI generation parameter display.
- Multi-monitor window placement, monitor DPI / ICC profile reporting and Windows HDR / advanced-color status detection.
- JPG, PNG, GIF, WebP, BMP, TIFF, AVIF, HEIC/HEIF, PSD/PSB, common RAW containers, JXL, QOI, SVG, ICO and EXR decode routes.

## Publish

```powershell
powershell -ExecutionPolicy Bypass -File `
  D:\WorkSpace\Codex\MangaView\MangaView\scripts\publish-win-x64.ps1
```

Default output:

```text
MangaView\artifacts\publish\win-x64\MangaView.exe
```

The self-contained output includes the .NET runtime, WPF desktop runtime, Magick.NET native libraries and `libheif.dll`; the target machine does not need a separately installed .NET runtime.

## Local Data

Default location:

```text
%LOCALAPPDATA%\MangaView
```

It contains `progress.json`, `settings.json`, `bookmarks.json`, `recent.json` and the CBZ extraction cache. `MANGAVIEW_DATA_DIR` can redirect all of it for portable or isolated operation.

## Release Checks

```powershell
D:\WorkSpace\Codex\MangaView\MangaView\tests\MangaView.Core.Tests\bin\Release\net10.0\MangaView.Core.Tests.exe
D:\WorkSpace\Codex\MangaView\MangaView\tests\MangaView.FormatTests\bin\Release\net10.0-windows\MangaView.FormatTests.exe
```

## Known Release Limitations

- HDR / advanced-color state is detected and reported, but WPF output remains on its sRGB compositor fallback. A future Direct3D/DXGI renderer is required for true HDR swap-chain output.
- RAW decoding coverage follows Magick.NET / ImageMagick camera support and may vary by camera model.
- `.rar` / `.cbr` / `.7z` are not part of V1.0.
- Batch conversion, printing and Windows Explorer thumbnail registration remain follow-up features.
