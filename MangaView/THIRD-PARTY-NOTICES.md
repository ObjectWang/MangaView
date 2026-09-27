# Third-Party Notices

MangaView uses the following third-party components.

## Magick.NET

- Package: `Magick.NET-Q8-AnyCPU` 14.17.1
- Project: <https://github.com/dlemstra/Magick.NET>
- License: Apache License 2.0

Magick.NET is used as the fallback decoder for PSD/PSB, RAW previews, AVIF, WebP, JXL, QOI, EXR and other ImageMagick-supported formats.

## LibHeifSharp

- Package: `LibHeifSharp` 3.2.0
- Project: <https://github.com/0xC0000054/libheif-sharp>
- License: LGPL-3.0-or-later

LibHeifSharp is used as the managed binding for HEIC / HEIF decoding.

## libheif native runtime

- Package: `LibHeif.Native.Runtime` 1.20.2
- Project: <https://github.com/borka-s/LibHeif.Native.Runtime>
- Upstream library: <https://github.com/strukturag/libheif>
- License: LGPL-3.0-or-later

The native `libheif.dll` is distributed as a separate dynamically loaded library. Review LGPL relinking/replacement requirements and HEIC/HEVC patent licensing before a commercial release.
