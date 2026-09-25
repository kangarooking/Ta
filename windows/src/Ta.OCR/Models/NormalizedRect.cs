namespace Ta.OCR.Models;

/// <summary>
/// 归一化矩形 —— Mac 版 `CGRect` 型 `boundingBox` 的 Windows 等价物。
///
/// **坐标系约定必须与 Mac 完全一致**，否则布局分析器的每个阈值都会错位：
/// · <see cref="X"/> / <see cref="Y"/> 是**左下**角坐标，值域 0..1
/// · X 轴向右增长，**Y 轴向上增长**（Vision 的 `VNRecognizedTextObservation.boundingBox` 约定）
/// · <see cref="MinX"/> = X，<see cref="MinY"/> = Y，与 Mac 的 `boundingBox.minX` / `.minY` 同名同义
///
/// 参考文档 §14 风险 #33：WinRT `OcrWord.BoundingRect` 是**图像像素坐标、原点左上、Y 向下**，
/// 必须经 <see cref="FromImagePixels"/> 归一化后才能参与 Mac 那套几何判断。
/// </summary>
public readonly record struct NormalizedRect
{
    public NormalizedRect(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>全零矩形。</summary>
    public static NormalizedRect Zero => new(0, 0, 0, 0);

    /// <summary>左边界（0..1）。对应 Mac `boundingBox.minX`。</summary>
    public double MinX => X;

    /// <summary>下边界（0..1）。对应 Mac `boundingBox.minY`。</summary>
    public double MinY => Y;

    /// <summary>右边界（0..1）。</summary>
    public double MaxX => X + Width;

    /// <summary>上边界（0..1）。</summary>
    public double MaxY => Y + Height;

    /// <summary>垂直中点（0..1）。对应 Mac `boundingBox.midY` —— 阅读顺序与分行容差都依赖它。</summary>
    public double MidY => Y + (Height / 2);

    /// <summary>左边界（0..1）。</summary>
    public double X { get; }

    /// <summary>下边界（0..1）。</summary>
    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    /// <summary>
    /// 把 **WinRT 图像像素矩形**（原点左上、Y 向下）换算成与 Mac 一致的归一化矩形。
    ///
    /// 这是本移植里最关键的一个纯函数 —— 参考文档 §14 #33 明确把「WinRT bbox 是图像像素坐标
    /// 而非归一化」列为必须显式处理的缺口。换算分两步：
    /// <code>
    ///   normalizedX = pixelX / imageWidth                       // X 只需缩放，方向一致
    ///   normalizedY = 1 - (pixelY + pixelHeight) / imageHeight   // 既要缩放，又要翻 Y 轴
    /// </code>
    /// <para>
    /// **不做夹取**：Mac 的 Vision 归一化框本身就可能轻微越界（例如 1.0000001），
    /// 分析器用的是相对差值与绝对阈值，夹取反而会引入 Mac 上没有的差异。
    /// </para>
    /// </summary>
    /// <param name="pixelX">图像像素矩形的左边界。</param>
    /// <param name="pixelY">图像像素矩形的**上**边界。</param>
    /// <param name="pixelWidth">像素宽度。</param>
    /// <param name="pixelHeight">像素高度。</param>
    /// <param name="imageWidth">图像总宽（像素）。</param>
    /// <param name="imageHeight">图像总高（像素）。</param>
    public static NormalizedRect FromImagePixels(
        double pixelX,
        double pixelY,
        double pixelWidth,
        double pixelHeight,
        int imageWidth,
        int imageHeight)
    {
        // 图像尺寸非法时返回零矩形：后续所有几何判断都会退化成「不构成表格/同一块」，
        // 与 Mac 拿到空 observations 时的行为同构（不会抛异常打断识别）。
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return Zero;
        }

        var width = imageWidth;
        var height = imageHeight;

        return new NormalizedRect(
            pixelX / width,
            1 - ((pixelY + pixelHeight) / height),
            pixelWidth / width,
            pixelHeight / height);
    }

    /// <summary>
    /// 与另一个矩形求并集。用于把 WinRT 的**词级**矩形合成 Mac 的**行级** bbox。
    /// </summary>
    public NormalizedRect Union(NormalizedRect other)
    {
        var minX = Math.Min(MinX, other.MinX);
        var minY = Math.Min(MinY, other.MinY);
        var maxX = Math.Max(MaxX, other.MaxX);
        var maxY = Math.Max(MaxY, other.MaxY);
        return new NormalizedRect(minX, minY, maxX - minX, maxY - minY);
    }
}
