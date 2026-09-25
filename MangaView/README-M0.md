# MangaView — M0 技术验证报告

- 阶段：M0（渲染框架、解码管线、虚拟滚动原型）
- 日期：2026-09-25
- 退出标准：可流畅滚动 500 页长图
- 技术栈：WPF（C# / .NET 10），WIC 解码（对应需求文档“推荐技术栈 方案 A”）

## M0 完成内容

| 项 | 状态 |
|---|---|
| 渲染框架（WPF 自定义 WebtoonCanvas + OnRender 绘制） | 完成 |
| 解码管线（优先级调度 + 同 Key 去重 + LRU 位图缓存） | 完成 |
| 虚拟滚动（仅解码视口页 + 相邻预加载，滚动锚点 = 页码 + 页内偏移） | 完成 |
| 500 页长图测试数据（每 50 页含 5 倍高度特长页） | 完成 |
| 核心逻辑测试（自然排序 / 布局 / LRU / 调度器 / 虚拟化模拟） | 8/8 通过 |
| 明暗主题 / 全屏 / 页码气泡 / 进度条 / FPS 显示 | 完成 |

## 目录结构

```
MangaView/
├─ MangaView.slnx
├─ src/
│  ├─ MangaView.Core/          # 无 UI 依赖的核心逻辑
│  │  ├─ NaturalSort.cs        # 自然排序 page1 < page2 < page10
│  │  ├─ PageModel.cs          # 页面模型（文件头探明的宽高）
│  │  ├─ WebtoonLayout.cs      # 虚拟滚动布局 + 二分查找 + 滚动锚点
│  │  ├─ LruCache.cs           # 按字节预算的 LRU 位图缓存
│  │  └─ DecodeScheduler.cs    # 优先级解码调度（视口>预加载>缩略图，同 Key 去重）
│  ├─ MangaView.App/           # WPF 应用（M0：Webtoon 模式）
│  │  ├─ MainWindow.xaml(.cs)  # 菜单/状态栏/进度条/跳页/主题/全屏
│  │  ├─ WebtoonCanvas.cs      # 虚拟滚动画布（只绘制视口相交页）
│  │  └─ WpfDecodeWorker.cs    # WIC 按宽度解码 + 文件头尺寸探测
│  └─ MangaView.TestDataGen/   # 500 页测试图生成器
└─ tests/
   └─ MangaView.Core.Tests/    # 控制台测试（无需外部 NuGet 包）
```

## 构建与运行

SDK 安装在 `D:\software\dotnet`（.NET 10.0.401）。沙箱内构建需读取用户级 NuGet 配置，
首次还原请在正常终端执行：

```powershell
$env:DOTNET_CLI_HOME = "D:\WorkSpace\Codex\.dotnet-home"   # 可选：避免沙箱权限问题
D:\software\dotnet\dotnet.exe build MangaView\MangaView.slnx -c Release
```

运行测试：

```powershell
D:\WorkSpace\Codex\MangaView\tests\MangaView.Core.Tests\bin\Release\net10.0\MangaView.Core.Tests.exe
```

生成 500 页测试数据（已生成，约 32MB，不入库）：

```powershell
D:\WorkSpace\Codex\MangaView\src\MangaView.TestDataGen\bin\Release\net10.0-windows\MangaView.TestDataGen.exe `
  D:\WorkSpace\Codex\MangaView\testdata\webtoon-500 500 800 2200
```

启动应用（框架依赖构建，需指向 .NET 10 运行时）：

```powershell
$env:DOTNET_ROOT = "D:\software\dotnet"
D:\WorkSpace\Codex\MangaView\src\MangaView.App\bin\Release\net10.0-windows\MangaView.App.exe `
  D:\WorkSpace\Codex\MangaView\testdata\webtoon-500
```

> 注：系统 PATH 中的 dotnet 为 6.0，直接双击 exe 会因找不到 .NET 10 运行时失败。
> 修复方案见“遗留问题”。

## 操作（M0 已实现）

| 功能 | 操作 |
|---|---|
| 打开文件夹 | Ctrl+O / 拖拽 / 命令行参数 |
| 滚动 | 滚轮 / 空格(Shift+空格反向) / PageUp PageDown |
| 跳页 | 底部输入页码或点击进度条 |
| 翻 10 页 | Ctrl+PageUp / Ctrl+PageDown |
| 首尾 | Home / End |
| 全屏 | F11（Esc 退出） |
| 主题 | Ctrl+T / 状态栏按钮 |
| 页间距 | 视图菜单（无缝/8px/16px） |
| FPS | 状态栏实时显示 |

## 虚拟化设计要点

1. **头信息先行**：打开文件夹时仅解析每张图的文件头（宽高），
   不解码像素；500 页扫描在后台线程完成。
2. **按需解码**：只请求与视口相交的页 + 上下各一页预加载；
   解码宽度 = min(视口宽, 原图宽)，长图从源头降低内存。
3. **优先级调度**：视口页 > 预加载页 > 缩略图；同 Key 在途/排队去重；
   排队任务可被更高优先级升级（M0 测试覆盖）。
4. **LRU 预算缓存**：512MB 位图缓存，超出预算淘汰最久未用页并重绘。
5. **滚动锚点**：位置以“页码 + 页内偏移”记录，窗口宽度变化时按
   页内比例恢复视觉位置。
6. **500 页模拟测试**：全程滚动唯一解码 500 页、末态缓存仅 8 页、
   4ms 内完成调度（模拟层，即时 Worker）。

## M0 验收结果

- 500 页 / 32MB 测试集（含每 50 页一张 11000px 特长页）：
  应用启动、窗口正常、内存稳定在 ~130MB（不随页数线性增长）、
  UI 保持响应（Responding=True）。
- 核心逻辑测试：8/8 通过（含优先级、去重、LRU 淘汰、跳页只解码
  视口附近 2 页等关键路径）。
- 帧率指示器已内置（状态栏 FPS），供实际滚动人工/后续自动化验收。

## 遗留问题（进入 M1 前处理）

1. **运行时解析**：发布时使用自包含发布（`dotnet publish -r win-x64 --self-contained`）
   或安装系统级 .NET 10 运行时，消除 DOTNET_ROOT 手动依赖。
2. 平滑滚动动画、动图播放、压缩包（CBZ）为 M1/M2 范围。
3. 单页模式/智能双页模式为 M1/M2 范围。
