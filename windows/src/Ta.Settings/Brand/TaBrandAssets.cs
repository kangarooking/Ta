using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ta.Settings.Brand;

/// <summary>
/// 品牌资源定位与加载。
///
/// 对应 macOS <c>TaBrandAssets</c>（TaDesignSystem.swift:21-30），那里用
/// <c>Bundle.main.url(forResource:withExtension:subdirectory:)</c> 从 App Bundle 取
/// <c>Brand/Ta-AppIcon.png</c> 与 <c>Brand/Providers/*.png</c>。
/// Windows 没有 Bundle 概念，这里按「从可执行目录逐级向上找 Resources\Brand」的方式定位，
/// 另外支持用环境变量 <c>TA_BRAND_ROOT</c> 显式指定仓库根目录（打包/测试用）。
/// </summary>
public static class TaBrandAssets
{
    /// <summary>环境变量名：显式指定包含 <c>Resources\Brand</c> 的根目录。</summary>
    public const string RootOverrideEnvironmentVariable = "TA_BRAND_ROOT";

    private static readonly object Gate = new();
    private static string? _brandRoot;

    /// <summary>品牌资源根目录（&lt;repo&gt;\Resources\Brand），找不到时为 null。</summary>
    public static string? BrandRoot
    {
        get
        {
            lock (Gate)
            {
                return _brandRoot ??= LocateBrandRoot();
            }
        }
    }

    /// <summary>重置缓存（测试用）。</summary>
    public static void ResetCache()
    {
        lock (Gate)
        {
            _brandRoot = null;
        }
    }

    /// <summary>
    /// 取 <c>Resources/Brand/Providers/&lt;name&gt;.png</c>。
    /// 已有 6 个：claude / deepseek / gemini / openai / openrouter / zhipu。
    /// </summary>
    public static BitmapImage? ProviderImage(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var root = BrandRoot;
        if (root is null)
        {
            return null;
        }

        var path = Path.Combine(root, "Providers", name + ".png");
        return LoadImage(path);
    }

    /// <summary>取 <c>Resources/Brand/Ta-AppIcon.png</c>。</summary>
    public static BitmapImage? AppIconImage()
    {
        var root = BrandRoot;
        if (root is null)
        {
            return null;
        }

        return LoadImage(Path.Combine(root, "Ta-AppIcon.png"));
    }

    private static string? LocateBrandRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(RootOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            var candidate = Path.Combine(overrideRoot, "Resources", "Brand");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        var start = AppContext.BaseDirectory;
        for (var depth = 0; depth < 8 && start is not null; depth++)
        {
            var candidate = Path.Combine(start, "Resources", "Brand");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            start = Path.GetDirectoryName(start.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return null;
    }

    private static BitmapImage? LoadImage(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // 图片损坏/被占用都不应该让设置界面打不开
            return null;
        }
    }
}

/// <summary>
/// 品牌图标元素：优先用 <c>Ta-AppIcon.png</c>，取不到时画 SwiftUI 的 fallback mark。
///
/// 对应 macOS <c>TaAppIcon</c>（TaDesignSystem.swift:32-93）：
/// 圆角 <c>size*0.22</c> 的 paper 方块 + padding <c>size*0.20</c> 的 cinnabar 内方块
/// （圆角 <c>size*0.08</c>）+ 衬线加粗「拓」字（<c>size*0.42</c>，paper 色）。
/// </summary>
public sealed class TaAppIcon : FrameworkElement
{
    /// <summary>图标边长。</summary>
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(TaAppIcon),
        new FrameworkPropertyMetadata(48.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var size = Size;
        var outer = new Rect(0, 0, size, size);
        var outerRadius = size * 0.22;
        var innerInset = size * 0.20;
        var inner = Rect.Inflate(outer, -innerInset, -innerInset);
        var innerRadius = size * 0.08;

        dc.DrawRoundedRectangle(TaBrushes.Paper, null, outer, outerRadius, outerRadius);

        var image = TaBrandAssets.AppIconImage();
        if (image is not null)
        {
            dc.DrawImage(image, outer);
            return;
        }

        dc.DrawRoundedRectangle(TaBrushes.Cinnabar, null, inner, innerRadius, innerRadius);

        var formatted = new FormattedText(
            TaBrand.Name,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            TaTypography.SerifTitle,
            size * 0.42,
            TaBrushes.Paper,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var x = (size - formatted.Width) / 2;
        var y = (size - formatted.Height) / 2;
        dc.DrawText(formatted, new Point(x, y));
    }
}
