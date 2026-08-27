namespace Ta.Windows.Core.Capture;

public enum CaptureKind
{
    Region,
    FullDesktop,
    RepeatedRegion,
    SmartText,
    Window,
}

public readonly record struct CaptureArea
{
    public CaptureArea(int x, int y, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "截图宽度必须大于零。");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "截图高度必须大于零。");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public int Right => checked(X + Width);

    public int Bottom => checked(Y + Height);

    public bool Contains(CaptureArea other) =>
        other.X >= X &&
        other.Y >= Y &&
        other.Right <= Right &&
        other.Bottom <= Bottom;

    public CaptureArea? Intersect(CaptureArea other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);

        return right > left && bottom > top
            ? new CaptureArea(left, top, right - left, bottom - top)
            : null;
    }
}
