namespace MangaView.Core;

public enum ZoomMode
{
    Custom,
    FitWindow,
    FitWidth,
    FitHeight,
    OriginalSize,
}

/// <summary>
/// 单页看图状态：缩放、旋转与翻转。只影响显示，不修改源文件。
/// 缩放范围按需求限制在 5%–6400%，适应模式会根据视口重新计算。
/// </summary>
public sealed class ImageTransformState
{
    public const double MinScale = 0.05;
    public const double MaxScale = 64.0;

    public ZoomMode Mode { get; private set; } = ZoomMode.FitWindow;
    public double Scale { get; private set; } = 1.0;
    public int RotationDegrees { get; private set; }
    public bool FlipHorizontal { get; private set; }
    public bool FlipVertical { get; private set; }

    public double GetEffectiveScale(double pixelWidth, double pixelHeight,
        double viewportWidth, double viewportHeight)
    {
        double width = Math.Max(1, pixelWidth);
        double height = Math.Max(1, pixelHeight);
        bool quarterTurn = RotationDegrees is 90 or 270;
        double orientedWidth = quarterTurn ? height : width;
        double orientedHeight = quarterTurn ? width : height;

        double scale = Mode switch
        {
            ZoomMode.OriginalSize => 1.0,
            ZoomMode.Custom => Scale,
            ZoomMode.FitWidth when viewportWidth > 0 => viewportWidth / orientedWidth,
            ZoomMode.FitHeight when viewportHeight > 0 => viewportHeight / orientedHeight,
            ZoomMode.FitWindow when viewportWidth > 0 && viewportHeight > 0 =>
                Math.Min(viewportWidth / orientedWidth, viewportHeight / orientedHeight),
            _ => 1.0,
        };

        double effective = Math.Clamp(scale, MinScale, MaxScale);
        Scale = effective;
        return effective;
    }

    public (double Width, double Height) GetDisplaySize(double pixelWidth, double pixelHeight,
        double viewportWidth, double viewportHeight)
    {
        double scale = GetEffectiveScale(pixelWidth, pixelHeight, viewportWidth, viewportHeight);
        bool quarterTurn = RotationDegrees is 90 or 270;
        double width = Math.Max(1, pixelWidth) * scale;
        double height = Math.Max(1, pixelHeight) * scale;
        return quarterTurn ? (height, width) : (width, height);
    }

    public void SetZoomMode(ZoomMode mode)
    {
        Mode = mode;
    }

    public void SetCustomScale(double scale)
    {
        Scale = Math.Clamp(scale, MinScale, MaxScale);
        Mode = ZoomMode.Custom;
    }

    public void ZoomBy(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(factor));

        double current = Mode == ZoomMode.OriginalSize ? 1.0 : Scale;
        SetCustomScale(current * factor);
    }

    public void Restore(ZoomMode mode, double scale)
    {
        Scale = Math.Clamp(scale, MinScale, MaxScale);
        Mode = mode;
    }

    public void RotateRight() => RotationDegrees = (RotationDegrees + 90) % 360;

    public void RotateLeft() => RotationDegrees = (RotationDegrees + 270) % 360;

    public void ToggleHorizontalFlip() => FlipHorizontal = !FlipHorizontal;

    public void ToggleVerticalFlip() => FlipVertical = !FlipVertical;

    public void Reset()
    {
        Mode = ZoomMode.FitWindow;
        Scale = 1.0;
        RotationDegrees = 0;
        FlipHorizontal = false;
        FlipVertical = false;
    }
}
