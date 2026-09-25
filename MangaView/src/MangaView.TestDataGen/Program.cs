using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// 用法: MangaView.TestDataGen <输出目录> [页数=500] [宽=800] [高=2200]
// 每 50 页生成一张约 5 倍高度的长图，用于验证虚拟滚动与内存控制。
string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "testdata/webtoon-500");
int count = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 500;
int w = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 800;
int h = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 2200;

Directory.CreateDirectory(outDir);
var sw = System.Diagnostics.Stopwatch.StartNew();
for (int i = 1; i <= count; i++)
{
    bool tall = i % 50 == 0;
    int height = tall ? h * 5 : h;
    RenderAndSave(Path.Combine(outDir, $"page_{i}.jpg"), i, count, w, height, tall);
    if (i % 25 == 0 || i == count)
        Console.WriteLine($"生成 {i}/{count}");
}
sw.Stop();
Console.WriteLine($"完成: {count} 页 → {outDir}");
Console.WriteLine($"耗时: {sw.Elapsed.TotalSeconds:F1}s");

static void RenderAndSave(string path, int index, int total, int width, int height, bool tall)
{
    var dv = new DrawingVisual();
    using (DrawingContext dc = dv.RenderOpen())
    {
        // 按页变化的渐变背景，滚动时可直观看到页与页的切换
        var c1 = Color.FromRgb((byte)(30 + index % 60), (byte)(60 + index % 80), (byte)(120 + index % 100));
        var c2 = Color.FromRgb((byte)(200 - index % 60), (byte)(160 + index % 50), (byte)(240 - index % 80));
        dc.DrawRectangle(new LinearGradientBrush(c1, c2, 90), null, new Rect(0, 0, width, height));

        // 每 100px 一条参考线，用于观察滚动速度与流畅度
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1);
        pen.Freeze();
        for (int y = 0; y <= height; y += 100)
            dc.DrawLine(pen, new Point(0, y), new Point(width, y));

        var tf = new Typeface("Consolas");
        var ui = CultureInfo.InvariantCulture;
        var t1 = new FormattedText($"PAGE {index}", ui, FlowDirection.LeftToRight, tf, 64,
            Brushes.White, 1.0);
        dc.DrawText(t1, new Point(24, 24));

        var t2 = new FormattedText($"{width}x{height}{(tall ? " (long page)" : "")} - MangaView M0",
            ui, FlowDirection.LeftToRight, tf, 26, Brushes.White, 1.0);
        dc.DrawText(t2, new Point(24, 100));

        var t3 = new FormattedText($"{index} / {total}", ui, FlowDirection.LeftToRight, tf, 40,
            Brushes.White, 1.0);
        dc.DrawText(t3, new Point(24, height - 80));
    }

    var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
    rtb.Render(dv);
    var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
    encoder.Frames.Add(BitmapFrame.Create(rtb));
    using FileStream fs = File.Create(path);
    encoder.Save(fs);
}
