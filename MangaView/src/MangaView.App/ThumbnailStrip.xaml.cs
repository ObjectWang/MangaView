using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MangaView.Core;

namespace MangaView.App;

public partial class ThumbnailStrip : UserControl
{
    private readonly ObservableCollection<ThumbnailItem> _items = new();
    private readonly LruCache<object> _cache = new(64L * 1024 * 1024);
    private readonly DecodeScheduler _scheduler = new(new WpfDecodeWorker(), maxConcurrent: 1);
    private readonly HashSet<int> _loading = new();
    private int _generation;
    private bool _suppressSelection;

    public event Action<int>? PageRequested;

    public ThumbnailStrip()
    {
        InitializeComponent();
        List.ItemsSource = _items;
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        _cache.ItemEvicted += (_, value, _) =>
        {
            if (value is IDisposable disposable) disposable.Dispose();
        };
    }

    public void SetPages(IReadOnlyList<ImagePage> pages)
    {
        _generation++;
        _scheduler.CancelQueued();
        _cache.Clear();
        _loading.Clear();
        _items.Clear();
        for (int i = 0; i < pages.Count; i++)
            _items.Add(new ThumbnailItem(i, pages[i].Path));
        LoadNearbyThumbnails(0);
    }

    public void SetCurrentPage(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _suppressSelection = true;
        List.SelectedIndex = index;
        List.ScrollIntoView(_items[index]);
        _suppressSelection = false;
        RequestThumbnail(_items[index]);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || List.SelectedIndex < 0) return;
        PageRequested?.Invoke(List.SelectedIndex);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => LoadRealizedThumbnails();

    private void LoadNearbyThumbnails(int center)
    {
        for (int i = Math.Max(0, center - 8); i < Math.Min(_items.Count, center + 12); i++)
            RequestThumbnail(_items[i]);
    }

    private void LoadRealizedThumbnails()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(i) is null) continue;
            RequestThumbnail(_items[i]);
        }
    }

    private void RequestThumbnail(ThumbnailItem item)
    {
        if (item.Thumbnail is not null || !_loading.Add(item.Index)) return;
        string key = "thumb:" + item.Path;
        if (_cache.TryGet(key, out var cached) && TryGetFrame(cached, out var cachedFrame))
        {
            item.SetThumbnail(cachedFrame);
            _loading.Remove(item.Index);
            return;
        }

        item.Status = "…";
        int generation = _generation;
        var request = new DecodeRequest(key, item.Index, item.Path, 120, DecodePriority.Thumbnail);
        _scheduler.RequestAsync(request).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != _generation || item.Index >= _items.Count) return;
                _loading.Remove(item.Index);
                if (t.IsFaulted || t.IsCanceled || t.Result is null)
                {
                    item.Status = "×";
                    return;
                }

                object decoded = t.Result;
                _cache.Put(key, decoded, EstimateBytes(decoded));
                if (TryGetFrame(decoded, out var frame)) item.SetThumbnail(frame);
                else item.Status = "×";
            }));
        }, TaskScheduler.Default);
    }

    private static bool TryGetFrame(object decoded, out BitmapSource frame)
    {
        if (decoded is AnimatedImageSource animated)
        {
            frame = animated.CurrentFrame;
            return true;
        }
        frame = (BitmapSource)decoded;
        return true;
    }

    private static long EstimateBytes(object decoded) => decoded switch
    {
        AnimatedImageSource animated => animated.EstimatedBytes,
        BitmapSource bitmap => (long)Math.Max(1, bitmap.PixelWidth) * Math.Max(1, bitmap.PixelHeight) * 4,
        _ => 1,
    };
}

public sealed class ThumbnailItem : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private string _status = "加载中";

    public ThumbnailItem(int index, string path)
    {
        Index = index;
        Path = path;
        Label = $"{index + 1}";
    }

    public int Index { get; }
    public string Path { get; }
    public string Label { get; }
    public BitmapSource? Thumbnail => _thumbnail;
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetThumbnail(BitmapSource thumbnail)
    {
        _thumbnail = thumbnail;
        Status = "";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
    }
}
