using Ta.Core.Capture;

namespace Ta.Pinning;

/// <summary>
/// 单个钉图的全部可变状态与命令处理 —— **纯托管，零 Win32**。
///
/// 这是 Mac 版 <c>PinnedImageView</c>（PinnedImageWindowController.swift:281-648）里
/// 除 <c>window</c> / <c>needsDisplay</c> 之外的全部逻辑：
/// 旋转、镜像、灰度/反色、裁剪、缩略图、滚轮缩放与透明度、装饰开关、鼠标穿透、双击关闭。
///
/// 拆出来的理由：这些规则（尤其是灰度/反色互斥、旋转侧向换轴、滚轮夹取）
/// 必须能被单测逐条锁定，而一旦混进窗口过程就只能靠人眼验收。
///
/// 单位约定：<see cref="ContentSize"/> / <see cref="Origin"/> 都是**逻辑 pt**，
/// 与 Mac 的 frame 语义一致；换算成物理像素是窗口层的职责。
/// </summary>
public sealed class PinViewState
{
    public PinViewState(RgbaBitmapSource source, PinSizeD contentSize, PinPointD origin)
    {
        ImageWidth = source.Width;
        ImageHeight = source.Height;
        _contentSize = contentSize;
        _origin = origin;
    }

    private PinSizeD _contentSize;
    private PinPointD _origin;

    /// <summary>源图宽（像素）。</summary>
    public int ImageWidth { get; }

    /// <summary>源图高（像素）。</summary>
    public int ImageHeight { get; }

    /// <summary>内容区逻辑尺寸（不含阴影外扩）。</summary>
    public PinSizeD ContentSize => _contentSize;

    /// <summary>窗口左上角在全局屏坐标中的位置（逻辑 pt）。</summary>
    public PinPointD Origin => _origin;

    /// <summary>旋转圈数（每圈 90°）。Mac: rotationQuarterTurns（:292）。</summary>
    public int RotationQuarterTurns { get; private set; }

    /// <summary>水平镜像。Mac: isMirroredHorizontally（:293）。</summary>
    public bool MirroredHorizontally { get; private set; }

    /// <summary>垂直镜像。Mac: isMirroredVertically（:294）。</summary>
    public bool MirroredVertically { get; private set; }

    /// <summary>滤镜模式（灰度/反色互斥）。Mac: filteredMode（:295）。</summary>
    public PinFilterMode FilterMode { get; private set; } = PinFilterMode.None;

    /// <summary>裁剪矩形（源图像素坐标）；null 表示未裁剪。Mac: cropRect（:296）。</summary>
    public RectI? CropRect { get; private set; }

    /// <summary>是否正在拖裁剪选区。Mac: isCropping（:297）。</summary>
    public bool IsCropping { get; private set; }

    /// <summary>裁剪拖拽起点（视图坐标，pt）。</summary>
    public PinPointD? CropDragStart { get; private set; }

    /// <summary>裁剪拖拽当前点（视图坐标，pt）。</summary>
    public PinPointD? CropDragCurrent { get; private set; }

    /// <summary>进入缩略图模式前的内容尺寸。Mac: thumbnailPreviousFrame（:300）。</summary>
    public PinSizeD? ThumbnailPreviousSize { get; private set; }

    /// <summary>进入缩略图模式前的原点。Mac 存的是整个 frame，这里拆成尺寸+原点。</summary>
    public PinPointD? ThumbnailPreviousOrigin { get; private set; }

    /// <summary>是否处于缩略图模式。</summary>
    public bool IsThumbnail => ThumbnailPreviousSize is not null;

    /// <summary>窗口透明度。Mac: window.alphaValue（:453）。</summary>
    public double Alpha { get; private set; } = PinTransform.MaxAlpha;

    /// <summary>装饰态。Mac: PinnedImageDecorationState（:262-273）。</summary>
    public PinDecorationState Decoration { get; private set; } = new();

    /// <summary>是否置顶。Mac: window.level == .floating（:336-341, :538）。</summary>
    public bool IsTopmost { get; private set; } = true;

    /// <summary>鼠标穿透。Mac: panel.ignoresMouseEvents（:182, :341, :591）。</summary>
    public bool ClickThrough { get; private set; }

    /// <summary>关闭中（防止重复触发）。Mac: isClosing（:301）。</summary>
    public bool IsClosing { get; private set; }

    /// <summary>整幅源图的像素矩形。</summary>
    public RectI SourceBounds => new(0, 0, ImageWidth, ImageHeight);

    /// <summary>当前生效的图像矩形（裁剪后）。Mac: currentCGImage（:606-609）。</summary>
    public RectI EffectiveImageRect => CropRect ?? SourceBounds;

    /// <summary>
    /// 旋转。<paramref name="delta"/> 为 +1 向右、−1 向左。
    /// 返回 true 表示窗口需要宽高互换（侧向状态发生改变）。Mac: :488-500。
    /// </summary>
    public bool Rotate(int delta)
    {
        var wasSideways = PinTransform.IsSideways(RotationQuarterTurns);
        RotationQuarterTurns = PinTransform.Normalize(RotationQuarterTurns + delta);
        var isSideways = PinTransform.IsSideways(RotationQuarterTurns);

        if (wasSideways != isSideways)
        {
            SwapContentSize();
            return true;
        }

        return false;
    }

    /// <summary>向左旋转。Mac: rotateLeft（:485）。</summary>
    public bool RotateLeft() => Rotate(-1);

    /// <summary>向右旋转。Mac: rotateRight（:486）。</summary>
    public bool RotateRight() => Rotate(1);

    /// <summary>水平镜像。Mac: mirrorHorizontally（:502-505）。</summary>
    public void ToggleMirrorHorizontally() => MirroredHorizontally = !MirroredHorizontally;

    /// <summary>垂直镜像。Mac: mirrorVertically（:507-510）。</summary>
    public void ToggleMirrorVertically() => MirroredVertically = !MirroredVertically;

    /// <summary>
    /// 灰度显示。Mac: toggleGrayscale（:512-517）—— 开/关自身，并把「反色显示」的勾选清掉。
    /// </summary>
    public void ToggleGrayscale()
    {
        FilterMode = PinFilterModeMath.Toggle(FilterMode, PinFilterMode.Grayscale);
    }

    /// <summary>
    /// 反色显示。Mac: toggleInversion（:519-524）—— 同上，互斥。
    /// </summary>
    public void ToggleInversion()
    {
        FilterMode = PinFilterModeMath.Toggle(FilterMode, PinFilterMode.Inverted);
    }

    /// <summary>按菜单命令切换滤镜。灰度与反色互斥的规则集中在此一处。</summary>
    public void ToggleFilter(PinCommand command)
    {
        switch (command)
        {
            case PinCommand.ToggleGrayscale:
                ToggleGrayscale();
                break;
            case PinCommand.ToggleInversion:
                ToggleInversion();
                break;
        }
    }

    /// <summary>显示边框。Mac: toggleBorder（:526-529）。</summary>
    public void ToggleBorder() => Decoration = Decoration.Toggled(showsBorder: !Decoration.ShowsBorder, Decoration.ShowsShadow);

    /// <summary>窗口阴影。Mac: toggleShadow（:531-534）。</summary>
    public void ToggleShadow() => Decoration = Decoration.Toggled(Decoration.ShowsBorder, !Decoration.ShowsShadow);

    /// <summary>保持最前。Mac: toggleTopmost（:536-541）。</summary>
    public void ToggleTopmost() => IsTopmost = !IsTopmost;

    /// <summary>鼠标穿透。Mac: enableClickThrough（:591）。</summary>
    public void ToggleClickThrough() => ClickThrough = !ClickThrough;

    /// <summary>
    /// 恢复显示（键等价 0）：清掉旋转/镜像/滤镜/装饰，侧向状态改变时换轴。
    /// 返回 true 表示窗口需要宽高互换。Mac: resetAppearance（:543-561）。
    /// </summary>
    public bool ResetAppearance()
    {
        var wasSideways = PinTransform.IsSideways(RotationQuarterTurns);
        RotationQuarterTurns = 0;
        MirroredHorizontally = false;
        MirroredVertically = false;
        FilterMode = PinFilterMode.None;
        Decoration = new PinDecorationState();

        if (wasSideways)
        {
            SwapContentSize();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 缩略图模式。返回 true 表示尺寸发生了变化，窗口需要重排。
    /// Mac: toggleThumbnail（:573-589）。
    /// </summary>
    public bool ToggleThumbnail()
    {
        if (ThumbnailPreviousSize is { } previousSize && ThumbnailPreviousOrigin is { } previousOrigin)
        {
            _contentSize = previousSize;
            _origin = previousOrigin;
            ThumbnailPreviousSize = null;
            ThumbnailPreviousOrigin = null;
            return true;
        }

        ThumbnailPreviousSize = _contentSize;
        ThumbnailPreviousOrigin = _origin;

        var thumbnail = PinTransform.ThumbnailSize(ImageWidth, ImageHeight);
        _origin = PinTransform.CenterPreservingOrigin(_origin, _contentSize, thumbnail);
        _contentSize = thumbnail;
        return true;
    }

    /// <summary>
    /// 滚轮。返回 true 表示几何发生了变化（需要重绘/重排）。
    /// Mac: scrollWheel（:450-465）。
    /// </summary>
    /// <param name="scrollDeltaY">滚动量。Windows 的 WM_MOUSEWHEEL 以 120 为档，
    /// 调用方需除以 120 折算成 Mac 的「行」语义。</param>
    /// <param name="withCommand">是否按住 ⌘（Mac）/ Ctrl（Windows 映射）。</param>
    public bool Scroll(double scrollDeltaY, bool withCommand)
    {
        if (withCommand)
        {
            var next = PinTransform.NextAlpha(Alpha, scrollDeltaY);
            if (Math.Abs(next - Alpha) < double.Epsilon)
            {
                return false;
            }

            Alpha = next;
            return true;
        }

        var newSize = PinTransform.ResizeForWheel(_contentSize, scrollDeltaY);
        _origin = PinTransform.CenterPreservingOrigin(_origin, _contentSize, newSize);
        _contentSize = newSize;
        return true;
    }

    /// <summary>直接移动窗口原点（拖拽用）。</summary>
    public void MoveTo(double x, double y)
    {
        _origin = new PinPointD(x, y);
    }

    /// <summary>按拖拽位移移动窗口。Mac: mouseDragged（:412-424）。</summary>
    public void MoveBy(double deltaX, double deltaY)
    {
        _origin = new PinPointD(_origin.X + deltaX, _origin.Y + deltaY);
    }

    /// <summary>开始裁剪。Mac: beginCrop（:472-477）。</summary>
    public void BeginCrop()
    {
        IsCropping = true;
        CropDragStart = null;
        CropDragCurrent = null;
    }

    /// <summary>取消裁剪（拖拽过程中被取消时）。</summary>
    public void CancelCrop()
    {
        IsCropping = false;
        CropDragStart = null;
        CropDragCurrent = null;
    }

    /// <summary>裁剪拖拽中更新选区。Mac: mouseDown/mouseDragged 的裁剪分支（:401-406, :413-417）。</summary>
    public void UpdateCropDrag(PinPointD start, PinPointD current)
    {
        CropDragStart = start;
        CropDragCurrent = current;
    }

    /// <summary>
    /// 提交裁剪。返回 true 表示裁剪矩形发生了变化。
    /// Mac: mouseUp 的裁剪提交（:426-448）。
    /// </summary>
    public bool CommitCrop(RectD bounds)
    {
        var start = CropDragStart;
        var current = CropDragCurrent;
        CancelCrop();

        if (start is not { } from || current is not { } to)
        {
            return false;
        }

        var selected = PinnedImageCrop.IntersectWithBounds(PinnedImageCrop.Normalize(from, to), bounds);
        var changed = PinnedImageCrop.TryMapViewRectToImage(selected, bounds, CropRect, ImageWidth, ImageHeight, out var newCrop);
        if (!changed)
        {
            return false;
        }

        CropRect = newCrop;
        ApplyAspect();
        return true;
    }

    /// <summary>重置裁剪。Mac: resetCrop（:479-483）。</summary>
    public bool ResetCrop()
    {
        if (CropRect is null)
        {
            return false;
        }

        CropRect = null;
        ApplyAspect();
        return true;
    }

    /// <summary>按当前图像宽高比重排窗口高度。Mac: resizeWindowForCurrentAspect（:620-628）。</summary>
    public void ApplyAspect()
    {
        var current = EffectiveImageRect;
        var newSize = PinTransform.ResizeForAspect(_contentSize, current.Width, current.Height);
        _origin = PinTransform.CenterPreservingOrigin(_origin, _contentSize, newSize);
        _contentSize = newSize;
    }

    /// <summary>
    /// 标记为关闭中。返回 false 表示此前已标记过（重复触发，需忽略）。
    /// Mac: closeForDoubleClickIfNeeded / closePin 的 isClosing 守卫（:634-647）。
    /// </summary>
    public bool TryBeginClose()
    {
        if (IsClosing)
        {
            return false;
        }

        IsClosing = true;
        return true;
    }

    /// <summary>宽高互换并保持中心。Mac: :493-497。</summary>
    private void SwapContentSize()
    {
        var swapped = PinTransform.Swap(_contentSize);
        _origin = PinTransform.CenterPreservingOrigin(_origin, _contentSize, swapped);
        _contentSize = swapped;
    }
}

/// <summary>
/// 装饰态。对应 Mac 的 <c>PinnedImageDecorationState</c>（:262-273）与
/// <c>PinnedImageDecoration</c> 常量（:275-279）。
/// </summary>
public sealed record PinDecorationState(bool ShowsBorder = true, bool ShowsShadow = true)
{
    public PinDecorationState Toggled(bool showsBorder, bool showsShadow) =>
        new(showsBorder, showsShadow);
}

/// <summary>
/// 源图像的抽象。让 <see cref="PinViewState"/> 只依赖尺寸，不直接持有位图，
/// 便于构造纯状态做单测。
/// </summary>
public readonly record struct RgbaBitmapSource(int Width, int Height)
{
    public static RgbaBitmapSource From(Ta.Core.Imaging.RgbaBitmap bitmap) => new(bitmap.Width, bitmap.Height);
}

/// <summary>
/// 一次绘制请求。把 <see cref="PinViewState"/> 投影成渲染器需要的纯参数，
/// 渲染器因此可以在不构造窗口的情况下被单测驱动。
/// </summary>
public sealed record PinDrawRequest
{
    public required Ta.Core.Imaging.RgbaBitmap Source { get; init; }

    /// <summary>裁剪矩形（源图像素坐标）；null 表示整幅。</summary>
    public RectI? Crop { get; init; }

    /// <summary>像素滤镜。</summary>
    public PinFilterMode Filter { get; init; } = PinFilterMode.None;

    /// <summary>旋转圈数。</summary>
    public int QuarterTurns { get; init; }

    public bool MirrorHorizontally { get; init; }
    public bool MirrorVertically { get; init; }

    /// <summary>内容区宽（物理像素）。</summary>
    public required int ViewWidth { get; init; }

    /// <summary>内容区高（物理像素）。</summary>
    public required int ViewHeight { get; init; }

    public bool ShowsBorder { get; init; } = true;
    public bool ShowsShadow { get; init; } = true;

    /// <summary>窗口透明度 [0.2, 1]。</summary>
    public double Alpha { get; init; } = 1;

    /// <summary>裁剪遮罩（视图坐标的起止两点）；null 表示不在裁剪状态。</summary>
    public (PinPointD Start, PinPointD Current)? CropOverlay { get; init; }

    public static PinDrawRequest FromState(
        Ta.Core.Imaging.RgbaBitmap source,
        PinViewState state,
        int viewWidth,
        int viewHeight) =>
        new()
        {
            Source = source,
            Crop = state.CropRect,
            Filter = state.FilterMode,
            QuarterTurns = state.RotationQuarterTurns,
            MirrorHorizontally = state.MirroredHorizontally,
            MirrorVertically = state.MirroredVertically,
            ViewWidth = viewWidth,
            ViewHeight = viewHeight,
            ShowsBorder = state.Decoration.ShowsBorder,
            ShowsShadow = state.Decoration.ShowsShadow,
            Alpha = state.Alpha,
            CropOverlay = state.IsCropping && state.CropDragStart is { } s && state.CropDragCurrent is { } c
                ? (s, c)
                : null,
        };
}
