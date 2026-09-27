namespace MangaView.Core;

public enum ReadingMode
{
    /// <summary>单页基础浏览模式。</summary>
    SinglePage,
    /// <summary>网络漫画纵向滚动模式。</summary>
    Webtoon,
    /// <summary>智能双页模式。</summary>
    DoublePage,
}

public enum ReadingDirection
{
    /// <summary>左到右：左页先读，适合欧美漫画、普通画册。</summary>
    LeftToRight,
    /// <summary>右到左：右页先读，适合日式漫画。</summary>
    RightToLeft,
}
