namespace MangaView.Core;

/// <summary>
/// 自然排序比较器：page1 &lt; page2 &lt; page10，数字段按数值比较，
/// 字母段大小写不敏感（同字母时按原始序数稳定排序）。
/// </summary>
public sealed class NaturalSortComparer : IComparer<string>
{
    public static readonly NaturalSortComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            char cx = x[ix], cy = y[iy];
            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                int sx = ix, sy = iy;
                while (sx < x.Length && char.IsDigit(x[sx])) sx++;
                while (sy < y.Length && char.IsDigit(y[sy])) sy++;

                // 去除前导零后按长度 + 序数比较数值
                int zx = ix; while (zx < sx && x[zx] == '0') zx++;
                int zy = iy; while (zy < sy && y[zy] == '0') zy++;
                int lenX = sx - zx, lenY = sy - zy;
                if (lenX != lenY) return lenX < lenY ? -1 : 1;
                int cmp = string.CompareOrdinal(x, zx, y, zy, lenX);
                if (cmp != 0) return cmp;
                // 数值相同（如 01 与 1）时，前导零更少的排前面，保证结果稳定
                if (sx - ix != sy - iy) return (sx - ix) < (sy - iy) ? -1 : 1;
                ix = sx; iy = sy;
            }
            else
            {
                int ci = char.ToLowerInvariant(cx).CompareTo(char.ToLowerInvariant(cy));
                if (ci != 0) return ci;
                int co = cx.CompareTo(cy);
                if (co != 0) return co;
                ix++; iy++;
            }
        }
        if (ix < x.Length) return 1;
        if (iy < y.Length) return -1;
        return 0;
    }
}
