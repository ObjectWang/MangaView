namespace MangaView.Core;

/// <summary>已探明尺寸的单页数据。宽度/高度来自文件头解析，不触发完整解码。</summary>
public sealed record ImagePage(int Index, string Path, string FileName, int Width, int Height)
{
    public double Aspect => Width > 0 ? (double)Height / Width : 0;
}
