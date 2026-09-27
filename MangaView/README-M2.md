# MangaView — M2 漫画核心

- 阶段：M2（智能双页、Webtoon 进度恢复、CBZ / ZIP、动图）
- 技术栈：WPF / .NET 10
- 前置阶段：M1 基础浏览

## M2 完成内容

| 需求 | 状态 |
|---|---|
| 智能双页模式接入主界面 | 完成 |
| 第一页可作为独立封面 | 完成 |
| 从第二页开始正确配对 | 完成 |
| 左到右 / 右到左阅读方向 | 完成 |
| 双页图片独立适配高度并垂直居中 | 完成 |
| 双页方向、封面与间距按书记忆 | 完成 |
| Webtoon 页码、页内锚点恢复 | 完成 |
| 打开 `.cbz` / `.zip` 虚拟文件夹 | 完成 |
| CBZ 条目数、单文件大小、总大小限制 | 完成 |
| CBZ 路径扁平化与路径穿越防护 | 完成 |
| 损坏 / 加密压缩包明确报错 | 完成 |
| 文件夹与归档变更自动刷新 | 完成 |
| Animated GIF 播放（单页 / 双页 / Webtoon） | 完成 |

## 双页设置

视图菜单中的“双页设置”支持：

- 阅读方向：左到右 / 右到左。
- 第一页作为独立封面。
- 双页间距：0 / 12 / 24 像素。

设置按数据源独立保存。文件夹使用完整路径作为书标识，CBZ / ZIP 使用归档完整路径作为书标识。

## 阅读进度

默认保存位置：

```text
%LOCALAPPDATA%\MangaView\progress.json
```

测试或便携运行时可通过环境变量隔离 / 自定义：

```powershell
$env:MANGAVIEW_DATA_DIR = "D:\MangaViewData"
```

每本书保存：

- 当前阅读模式。
- 当前页码。
- Webtoon 页码 + 页内偏移锚点。
- 阅读方向。
- 独立封面设置。
- 双页间距。
- 单页缩放模式与比例。
- 最后更新时间。

进度文件损坏时会回退为空记录，不会阻止应用启动。页面切换、模式切换和滚动会延迟合并保存，关闭窗口时强制保存。

## CBZ / ZIP

- `.cbz` 与 `.zip` 作为虚拟文件夹处理，不解压回源目录。
- 解压缓存位于 `%LOCALAPPDATA%\MangaView\cache\cbz`，可通过 `MANGAVIEW_DATA_DIR` 改到其他位置。
- 缓存键包含归档路径、大小与最后写入时间；归档更新后自动重新解压。
- 所有条目名会转为安全文件名，并使用序号前缀避免重名。
- 默认限制：10000 个条目、单文件 512 MB、总解压大小 2 GB。

## 自动刷新

- 文件夹监视当前目录；递归模式下包含子目录。
- 归档监视其所在目录并匹配归档文件名。
- 文件新增、删除、重命名或保存后，650 ms 防抖重载播放列表。
- 重载前保存当前进度，重载后保持当前文件和阅读位置。

## 动图

Animated GIF 使用 WPF `GifBitmapDecoder` 在后台解码为已冻结帧，再由独立计时器按帧延迟循环。

三种浏览模式共用动图帧源：

- 单页模式重绘当前帧。
- Webtoon 仅对视口附近的动图订阅帧更新。
- 双页模式对当前跨页中的动图订阅帧更新。

## 构建与测试

```powershell
$env:DOTNET_CLI_HOME = "D:\WorkSpace\Codex\MangaView\.dotnet-home"
$env:DOTNET_ROOT = "D:\software\dotnet"
D:\software\dotnet\dotnet.exe build D:\WorkSpace\Codex\MangaView\MangaView\MangaView.slnx -c Release

D:\WorkSpace\Codex\MangaView\MangaView\tests\MangaView.Core.Tests\bin\Release\net10.0\MangaView.Core.Tests.exe
```

当前结果：

- 构建 0 警告 / 0 错误。
- 核心测试 16/16 通过。
- Webtoon 跳转第 300 页并重启后恢复到第 300 页。
- 双页右到左恢复后显示左页 3、右页 2，页码保持第 2 页。
- 文件夹新增图片后自动从 6 页刷新为 7 页。
- CBZ 更新后自动从 7 页刷新为 8 页。
- 单页、Webtoon、双页 GIF 采样均检测到两种帧颜色并持续播放。
- 800×30000 长图在 Webtoon 模式正常打开，工作集约 205 MB，UI 保持响应。

## 暂不包含

- `.rar` / `.cbr` / `.7z` 属于 V1.x。
- 自动跨页识别与自动阅读顺序判断属于 V2.x。

> Animated WebP / AVIF、HEIC、PSD、RAW 等格式增强已在 M3 完成，见 `README-M3.md`。
