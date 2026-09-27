namespace MangaView.Core;

/// <summary>一个双页跨页：Left/Right 为显示位置的页索引，-1 表示该侧无页。</summary>
public sealed record DoublePageSpread(int SpreadIndex, int LeftPageIndex, int RightPageIndex)
{
    public bool IsSingle => LeftPageIndex < 0 || RightPageIndex < 0;

    /// <summary>按阅读方向返回本跨页包含的页（先读页在前）。</summary>
    public IEnumerable<int> ReadingOrder(ReadingDirection direction)
    {
        if (direction == ReadingDirection.LeftToRight)
        {
            if (LeftPageIndex >= 0) yield return LeftPageIndex;
            if (RightPageIndex >= 0) yield return RightPageIndex;
        }
        else
        {
            if (RightPageIndex >= 0) yield return RightPageIndex;
            if (LeftPageIndex >= 0) yield return LeftPageIndex;
        }
    }

    /// <summary>按阅读方向返回本跨页的“当前页”（用于进度与页码显示）。</summary>
    public int CurrentPage(ReadingDirection direction) => direction == ReadingDirection.LeftToRight
        ? (LeftPageIndex >= 0 ? LeftPageIndex : RightPageIndex)
        : (RightPageIndex >= 0 ? RightPageIndex : LeftPageIndex);
}

/// <summary>
/// 智能双页布局：
/// - 可选独立封面页（第一页单独显示，第二页开始两两配对）；
/// - 阅读方向决定左右页位置；
/// - 提供页码 -&gt; 跨页索引的查找，供进度恢复与跳转使用。
/// </summary>
public sealed class DoublePageLayout
{
    public IReadOnlyList<ImagePage> Pages { get; }
    public bool CoverPage { get; }
    public ReadingDirection Direction { get; }
    public IReadOnlyList<DoublePageSpread> Spreads { get; }

    public DoublePageLayout(IReadOnlyList<ImagePage> pages, bool coverPage, ReadingDirection direction)
    {
        ArgumentNullException.ThrowIfNull(pages);
        Pages = pages;
        CoverPage = coverPage;
        Direction = direction;
        var spreads = new List<DoublePageSpread>(pages.Count / 2 + 2);
        int start = 0;

        if (pages.Count > 0 && coverPage)
        {
            spreads.Add(CreateSpread(spreads.Count, pages[0].Index, -1, direction));
            start = 1;
        }

        for (int i = start; i < pages.Count; i += 2)
        {
            int first = pages[i].Index;
            int second = i + 1 < pages.Count ? pages[i + 1].Index : -1;
            spreads.Add(CreateSpread(spreads.Count, first, second, direction));
        }

        Spreads = spreads;
    }

    private static DoublePageSpread CreateSpread(int spreadIndex, int first, int second, ReadingDirection direction)
    {
        if (second < 0)
        {
            // 单页：LTR 放左侧；RTL 放右侧（保持“先读页”的位置习惯）
            return direction == ReadingDirection.LeftToRight
                ? new DoublePageSpread(spreadIndex, first, -1)
                : new DoublePageSpread(spreadIndex, -1, first);
        }
        return direction == ReadingDirection.LeftToRight
            ? new DoublePageSpread(spreadIndex, first, second)
            : new DoublePageSpread(spreadIndex, second, first);
    }

    public DoublePageSpread SpreadAt(int spreadIndex) =>
        Spreads[Math.Clamp(spreadIndex, 0, Math.Max(0, Spreads.Count - 1))];

    public int SpreadIndexForPage(int pageIndex)
    {
        for (int i = 0; i < Spreads.Count; i++)
        {
            var s = Spreads[i];
            if (s.LeftPageIndex == pageIndex || s.RightPageIndex == pageIndex)
                return i;
        }
        return 0;
    }

    public int PageCount => Pages.Count;
    public int SpreadCount => Spreads.Count;
}
