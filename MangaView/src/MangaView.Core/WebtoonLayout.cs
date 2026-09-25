namespace MangaView.Core;

/// <summary>滚动锚点：页码 + 页内偏移（而非绝对像素），窗口宽度变化时用于恢复视觉位置。</summary>
public readonly record struct ScrollAnchor(int PageIndex, double InPageOffset);

/// <summary>
/// Webtoon 纵向流的虚拟滚动布局：
/// 根据每页头信息（宽高）与视口宽度计算“按宽度适配”的每页显示高度与总高度，
/// 提供偏移 <-> 页码的快速查找（二分），供渲染层只绘制可见页。
/// </summary>
public sealed class WebtoonLayout
{
    private readonly double[] _tops;
    private readonly double[] _bottoms;

    public IReadOnlyList<ImagePage> Pages { get; }
    public double ViewportWidth { get; }
    public double Gap { get; }
    public double TotalHeight { get; }

    public WebtoonLayout(IReadOnlyList<ImagePage> pages, double viewportWidth, double gap)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(viewportWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(gap, 0);
        Pages = pages;
        ViewportWidth = viewportWidth;
        Gap = gap;
        _tops = new double[pages.Count];
        _bottoms = new double[pages.Count];
        double y = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            _tops[i] = y;
            double h = p.Width > 0 ? viewportWidth * p.Height / p.Width : 0;
            y += h;
            _bottoms[i] = y;
            y += gap;
        }
        TotalHeight = pages.Count == 0 ? 0 : Math.Max(0, y - gap);
    }

    public double PageTop(int index) => _tops[index];
    public double PageBottom(int index) => _bottoms[index];
    public double PageHeight(int index) => _bottoms[index] - _tops[index];

    /// <summary>返回 offset 所在页；offset 落在页间隙时返回前一页；offset 小于 0 时返回 -1。</summary>
    public int FindPageAt(double offset)
    {
        int lo = 0, hi = _tops.Length - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_tops[mid] <= offset) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    public ScrollAnchor GetAnchor(double offset)
    {
        int p = FindPageAt(offset);
        return p < 0 ? new ScrollAnchor(0, 0) : new ScrollAnchor(p, offset - _tops[p]);
    }

    public double OffsetFromAnchor(ScrollAnchor anchor)
    {
        if (Pages.Count == 0) return 0;
        int i = Math.Clamp(anchor.PageIndex, 0, Pages.Count - 1);
        return _tops[i] + Math.Max(0, anchor.InPageOffset);
    }

    /// <summary>枚举与 [top, bottom) 相交的所有页索引（升序）。</summary>
    public IEnumerable<int> PagesIntersecting(double top, double bottom)
    {
        if (_tops.Length == 0 || bottom <= top) yield break;
        int i = LowerBound(_bottoms, top);
        for (; i < _tops.Length && _tops[i] < bottom; i++)
            yield return i;
    }

    private static int LowerBound(double[] arr, double value)
    {
        int lo = 0, hi = arr.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (arr[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
