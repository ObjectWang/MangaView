# MangaView — M3 格式增强

- 阶段：M3（AVIF、WebP 动图、HEIC、PSD、RAW）
- 技术栈：WPF / .NET 10 / WIC / Magick.NET / libheif
- 前置阶段：M2 漫画核心

## 解码架构

M3 使用分层解码架构：

1. **WPF WIC 快速路径**：JPG、PNG、GIF、BMP、TIFF、WebP、AVIF 等优先由系统 WIC 解码。
2. **Magick.NET 后备路径**：WIC 缺少对应系统扩展时，处理 PSD/PSB、RAW 预览、AVIF、WebP、JXL、QOI、EXR 等格式。
3. **libheif 专用路径**：HEIC / HEIF 优先使用随应用分发的 `libheif.dll` 解码主图，避免依赖用户在 Microsoft Store 安装 HEIF 扩展。
4. **动画路径**：GIF、WebP、AVIF、PNG 多帧容器先尝试 WIC，再回退 Magick.NET `MagickImageCollection`，统一输出 `AnimatedImageSource`。

## 已支持格式

### 基础与常用格式

`JPG`、`JPEG`、`PNG`、`GIF`、`WebP`、`BMP`、`TIFF`、`TIF`、`AVIF`

### M3 增强格式

`HEIC`、`HEIF`、`PSD`、`PSB`、`JXL`、`QOI`、`SVG`、`ICO`、`EXR`

### 常见 RAW 容器 / 预览

`DNG`、`CR2`、`CR3`、`NEF`、`NRW`、`ARW`、`SRF`、`SR2`、`ORF`、`RAF`、`RW2`、`PEF`、`SRW`、`RAW`

RAW 以 Magick.NET / ImageMagick 的相机解码与预览能力为准；不同厂商、压缩方式和新型号的支持程度可能不同。无法完整解码时会返回明确错误，不会导致主程序退出。

## 动画支持

- Animated GIF：WIC + Magick.NET 回退。
- Animated WebP：WIC 可用时直接解帧；否则使用 Magick.NET 动画集合。
- Animated AVIF：同样按 WIC → Magick.NET 顺序尝试。
- APNG：按 WIC 多帧解码。

三种阅读模式共享动画帧源，只有当前可见页持续触发重绘；缓存清理时会释放帧计时器。

## 依赖与许可

| 组件 | 用途 | 许可 |
|---|---|---|
| Magick.NET-Q8-AnyCPU 14.17.1 | PSD/RAW/AVIF/WebP/EXR 等后备解码 | Apache-2.0 |
| LibHeifSharp 3.2.0 | libheif .NET 绑定 | LGPL-3.0-or-later |
| LibHeif.Native.Runtime 1.20.2 | Windows x64 `libheif.dll` | LGPL-3.0-or-later |

详细说明见 `THIRD-PARTY-NOTICES.md`。商用发布前应再次审查 HEIC/HEVC 相关专利与 LGPL 分发方式。

## 构建与测试

```powershell
$env:DOTNET_CLI_HOME = "D:\WorkSpace\Codex\MangaView\.dotnet-home"
$env:DOTNET_ROOT = "D:\software\dotnet"
D:\software\dotnet\dotnet.exe build D:\WorkSpace\Codex\MangaView\MangaView\MangaView.slnx -c Release

D:\WorkSpace\Codex\MangaView\MangaView\tests\MangaView.FormatTests\bin\Release\net10.0-windows\MangaView.FormatTests.exe
```

外部样本测试：

```powershell
$env:MANGAVIEW_HEIC_SAMPLE = "D:\samples\example.heic"
$env:MANGAVIEW_RAW_SAMPLE = "D:\samples\example.cr2"
D:\WorkSpace\Codex\MangaView\MangaView\tests\MangaView.FormatTests\bin\Release\net10.0-windows\MangaView.FormatTests.exe
```

## 验收结果

- PSD：真实生成并解码通过。
- AVIF：真实生成并解码通过。
- HEIC：libheif 官方样本解码通过。
- WebP：真实生成并解码通过。
- Animated WebP：生成双帧动画，解码帧数不少于 2。
- RAW：公开 Canon CR2 样本按最大宽度 1200 解码通过。
- HEIC + CR2 + AVIF 混合文件夹可在主程序加载为 3 页，UI 保持响应。

## 当前边界

- `.rar` / `.cbr` / `.7z` 仍属于后续压缩包支持范围。
- RAW 解码兼容性取决于 Magick.NET 内置的相机解码能力。
- HEIC / HEVC 的专利授权需要按发布地区和商业模型单独评估。
