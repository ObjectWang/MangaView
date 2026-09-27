# MangaView — M1 基础浏览

- 阶段：M1（单页模式、文件夹、缩放、快捷键）
- 技术栈：WPF / .NET 10
- 前置阶段：M0 Webtoon 虚拟滚动原型

## M1 完成内容

| 需求 | 状态 |
|---|---|
| 打开单张图片并自动加载同目录播放列表 | 完成 |
| 打开文件夹、可选递归加载子文件夹 | 完成 |
| 默认自然排序 page1 < page2 < page10 | 完成 |
| 单页模式 | 完成 |
| Webtoon 模式（保留 M0 能力） | 完成 |
| 适应窗口 / 适应宽度 / 适应高度 / 原始大小 | 完成 |
| 5%–6400% 缩放、Ctrl+滚轮鼠标锚点缩放 | 完成 |
| 拖拽平移、方向键平移、触控板/触摸滚动基础支持 | 完成 |
| 左右旋转 90°、水平/垂直翻转、重置变换 | 完成 |
| 损坏图片占位且不阻断翻页 | 完成 |
| 平滑页面切换与开关 | 完成 |
| 全屏、明暗主题、页码/进度条/跳页 | 完成 |

## 快捷键

| 功能 | 快捷键 |
|---|---|
| 打开图片 | Ctrl+O |
| 打开文件夹 | Ctrl+Shift+O |
| 上一页 / 下一页 | ← / →、PageUp / PageDown、空格 / Shift+空格 |
| 首页 / 末页 | Home / End |
| 放大 / 缩小 | Ctrl++ / Ctrl+- |
| 适应窗口 / 宽度 / 高度 | Ctrl+1 / Ctrl+2 / Ctrl+3 |
| 原始大小 100% | Ctrl+0；按住 Z 可临时预览，松开恢复 |
| 右旋 / 左旋 90° | R / Shift+R |
| 水平 / 垂直翻转 | H / V |
| 重置变换 | Ctrl+R |
| 缩放 | Ctrl+滚轮 |
| 全屏 | F11，Esc 退出 |
| 明暗主题 | Ctrl+T |

## 鼠标与触控板

- 单页未放大或滚动到边界时，滚轮切换上一页/下一页。
- 单页放大后，滚轮滚动；Ctrl+滚轮以鼠标位置为锚点缩放。
- 鼠标左键拖拽平移放大后的图片。
- ScrollViewer 已启用 `PanningMode=Both`，支持触控板/触摸平移。

## 性能设计

1. 打开文件夹时先立即建立自然排序播放列表并开始解码当前页，不等待所有图片尺寸扫描完成。
2. 图片尺寸在后台逐张探测，每 20 张刷新扫描进度；单张损坏只记录失败，不终止整个列表。
3. 单页模式只解码当前页；Webtoon 继续使用 M0 的虚拟化、预加载和 LRU 缓存。
4. 单页与 Webtoon 共用同一播放列表和当前页位置，切换模式不丢页码。

## 构建与验证

```powershell
$env:DOTNET_CLI_HOME = "D:\WorkSpace\Codex\MangaView\.dotnet-home"
$env:DOTNET_ROOT = "D:\software\dotnet"
D:\software\dotnet\dotnet.exe build D:\WorkSpace\Codex\MangaView\MangaView\MangaView.slnx -c Release
```

运行核心测试：

```powershell
$env:DOTNET_ROOT = "D:\software\dotnet"
D:\WorkSpace\Codex\MangaView\MangaView\tests\MangaView.Core.Tests\bin\Release\net10.0\MangaView.Core.Tests.exe
```

当前结果：构建 0 警告 / 0 错误，核心测试 14/14 通过；500 页测试集从启动到首屏页码可见约 676 ms，连续触发 50 次翻页后仍保持响应且页码正确。快速翻页会取消过期排队解码，单页位图缓存上限为 128 MB。

## 后续 M2

- 智能双页模式正式接入主界面。
- CBZ / ZIP 虚拟文件夹。
- 阅读进度恢复与书签。
- 文件变化实时刷新和动图播放。
