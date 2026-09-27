using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MangaView.Core;

namespace MangaView.App;

public partial class MainWindow
{
    private CancellationTokenSource? _metadataCts;
    private int _metadataGeneration;

    private void OnToggleRememberProgressClick(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { RememberProgress = RememberProgressMenuItem.IsChecked };
        SaveM4Settings();
    }

    private void QueueMetadataRefresh()
    {
        if (!_m4Loaded || !_infoPanelVisible || _pages.Count == 0 || _currentPageIndex < 0)
            return;

        _metadataCts?.Cancel();
        _metadataCts = new CancellationTokenSource();
        int generation = ++_metadataGeneration;
        string path = _pages[_currentPageIndex].Path;
        InfoText.Text = BuildMonitorSummary(path) + "\n\n正在读取 EXIF / XMP…";

        _ = ImageMetadataReader.ReadAsync(path, _metadataCts.Token).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != _metadataGeneration) return;
                InfoText.Text = BuildMonitorSummary(path) + "\n\n" +
                    (t.IsFaulted || t.IsCanceled ? $"元数据读取失败：{t.Exception?.GetBaseException().Message}" : t.Result);
            }));
        }, TaskScheduler.Default);
    }

    private string BuildMonitorSummary(string path)
    {
        var monitor = MonitorService.GetMonitorFromWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var status = MonitorService.GetHdrStatus();
        string monitorText = monitor is null
            ? "显示器: 未知"
            : $"显示器: {monitor.DeviceName}  ({monitor.DpiX} DPI)";
        string profile = monitor?.ColorProfilePath is { Length: > 0 } profilePath
            ? $"ICC: {profilePath}"
            : "ICC: 未检测到";
        string hdr = status.Supported
            ? $"HDR: {(status.Enabled ? "已启用" : "未启用")} / {status.BitsPerColorChannel} bit"
            : "HDR: 不支持";
        return $"当前页: {Path.GetFileName(path)}\n{monitorText}\n{profile}\n{hdr}";
    }
}
