# MangaView — M4 体验打磨

- 阶段：M4（书签、幻灯片、主题、多显示器、HDR、V1.0 发布准备）
- 技术栈：WPF / .NET 10 / Windows Forms Screen API / Win32 DisplayConfig
- 前置阶段：M3 格式增强

## M4 完成内容

| 功能 | 状态 |
|---|---|
| 命名书签：新增、重命名、删除、跳转 | 完成 |
| 最近打开：文件、文件夹、归档、清空、开关 | 完成 |
| 幻灯片：1–30 秒、随机、循环、自动隐藏控件 | 完成 |
| 信息面板：EXIF、XMP、常见 AI 生成参数 | 完成 |
| 缩略图栏：异步加载、虚拟化、点击跳页 | 完成 |
| 主题：跟随系统、浅色、深色并持久化 | 完成 |
| 阅读偏好：默认模式、默认方向、默认封面 | 完成 |
| 多显示器：屏幕 DPI、ICC 配置文件、窗口位置恢复 | 完成 |
| HDR / 高级颜色：Windows 状态与色深检测 | 完成 |
| V1.0 自包含 win-x64 发布脚本 | 完成 |

## 快捷键

| 功能 | 快捷键 |
|---|---|
| 添加书签 | B |
| 管理书签 | Ctrl+B |
| 信息面板 | Tab |
| 缩略图栏 | T |
| 开始 / 暂停幻灯片 | F5 |
| 停止幻灯片 | Esc |
| 明暗主题切换 | Ctrl+T |

## 幻灯片

幻灯片菜单支持：

- 1、3、5、10、30 秒预设，以及 1–30 秒自定义间隔。
- 随机播放。
- 循环播放。
- 自动隐藏菜单、状态栏、缩略图与信息面板。
- 鼠标移动时临时恢复控件。
- 停止幻灯片后恢复用户选择的面板状态。

## 书签与最近记录

本地数据源通过完整路径或归档路径区分。每本书分别保存书签，书签包含名称、页码、阅读模式和 Webtoon 页内锚点。最近记录最多保留 20 条，可在设置中关闭保存或手动清空。

文件：

```text
%LOCALAPPDATA%\MangaView\bookmarks.json
%LOCALAPPDATA%\MangaView\recent.json
```

## 信息面板

信息面板异步读取：

- 文件名、路径、大小、修改时间、尺寸和格式。
- 常见 EXIF 标签。
- XMP XML。
- Prompt、Negative Prompt、Seed、Model、Sampler、Steps、CFG 等 AI 参数（从 XMP、参数属性或常见键值文本中解析）。
- 当前显示器、DPI、ICC 配置和 HDR / 高级颜色状态。

主图浏览不会被元数据读取阻塞；切换页面时会取消旧读取结果。

## 缩略图栏

缩略图栏使用独立的低优先级解码队列和 64 MB LRU 缓存：

- 只请求已实现的虚拟化项。
- 缩略图解码与主画面解码隔离。
- 点击缩略图直接跳页。
- 当前页变化时自动滚动到对应缩略图。

## 多显示器与 HDR

多显示器支持记录窗口位置、尺寸、最大化和显示器设备名。窗口位置超出虚拟屏幕时自动回退到默认布局。

HDR / 高级颜色使用 Windows DisplayConfig API 检测 HDR 支持、启用状态和每通道色深。当前版本使用 WPF 合成管线的 sRGB 回退；真正的 HDR swap-chain 输出需要后续 Direct3D/DXGI 渲染后端。

## V1.0 发布

发布版本为 `1.0.0`，可生成 Windows x64 自包含包：

```powershell
powershell -ExecutionPolicy Bypass -File `
  D:\WorkSpace\Codex\MangaView\MangaView\scripts\publish-win-x64.ps1
```

发布产物默认位于：

```text
MangaView\artifacts\publish\win-x64\MangaView.exe
```

## 验收结果

- 构建：0 警告 / 0 错误。
- 核心测试：19/19 通过。
- 格式测试：7/7 通过。
- 信息面板成功显示当前页、显示器 DPI、ICC 配置、HDR 状态和 EXIF 信息。
- 幻灯片从第 1 页自动前进到第 2 页，UI 保持响应。
- 书签通过界面新增并写入 `bookmarks.json`。
- 信息面板、缩略图栏和窗口位置在关闭重开后恢复。
- 自包含发布包包含 `libheif.dll`、`Magick.Native-Q8-x64.dll` 和应用程序。
- 自包含发布版成功打开 HEIC、CR2、AVIF 混合文件夹。

## 后续项

- 批量转换、打印、Windows 资源管理器缩略图注册。
- RAW + JPG 分组。
- 完整 HDR swap-chain 与更深入的 ICC 色彩转换。
- 中英文资源化文案与屏幕朗读器无障碍细化。
