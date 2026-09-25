using System.Windows;
using System.Windows.Media;

namespace Ta.Settings.Brand;

/// <summary>图标语义名。命名与 macOS 的 SF Symbol 一一对应，便于交叉核对。</summary>
public enum TaIconKind
{
    // ---- 设置窗口 7 个标签页（SettingsView.swift:108-118）----
    /// <summary>lock.shield —— 权限标签。</summary>
    LockShield,

    /// <summary>gearshape —— 常规标签。</summary>
    Gear,

    /// <summary>command —— 快捷键标签。</summary>
    Command,

    /// <summary>text.viewfinder —— 识别标签。</summary>
    TextViewfinder,

    /// <summary>character.book.closed —— 翻译标签。</summary>
    BookClosed,

    /// <summary>sparkles —— AI 模型标签。</summary>
    Sparkles,

    /// <summary>cpu —— Agent 标签。</summary>
    Cpu,

    // ---- 状态与通用 ----
    /// <summary>checkmark.circle.fill。</summary>
    CheckCircle,

    /// <summary>checkmark.circle。</summary>
    CheckCircleOutline,

    /// <summary>exclamationmark.triangle.fill。</summary>
    WarningTriangle,

    /// <summary>info.circle.fill。</summary>
    InfoCircle,

    /// <summary>xmark.octagon.fill。</summary>
    OctagonX,

    /// <summary>xmark.circle.fill。</summary>
    XCircle,

    /// <summary>checkmark.shield.fill。</summary>
    ShieldCheck,

    /// <summary>lock.shield.fill。</summary>
    LockShieldFilled,

    /// <summary>network。</summary>
    Network,

    /// <summary>circle.fill。</summary>
    Dot,

    /// <summary>circle.dashed。</summary>
    DashedCircle,

    /// <summary>plus。</summary>
    Plus,

    /// <summary>minus。</summary>
    Minus,

    /// <summary>chevron.down。</summary>
    ChevronDown,

    /// <summary>ellipsis.circle。</summary>
    Ellipsis,

    /// <summary>trash。</summary>
    Trash,

    /// <summary>doc.on.doc。</summary>
    Copy,

    /// <summary>checkmark。</summary>
    Check,

    /// <summary>arrow.right。</summary>
    ArrowRight,

    /// <summary>arrow.clockwise。</summary>
    Rotate,

    /// <summary>terminal。</summary>
    Terminal,

    /// <summary>key.fill。</summary>
    Key,

    /// <summary>photo。</summary>
    Photo,

    /// <summary>text.bubble。</summary>
    Bubble,

    /// <summary>cursorarrow.rays。</summary>
    Cursor,

    /// <summary>icloud.and.arrow.up。</summary>
    CloudUpload,

    /// <summary>cloud。</summary>
    Cloud,

    /// <summary>viewfinder —— 欢迎页主卡片。</summary>
    Viewfinder,

    /// <summary>rectangle.dashed。</summary>
    DashedRect,

    /// <summary>pin.fill。</summary>
    Pin,

    /// <summary>clipboard。</summary>
    Clipboard,

    /// <summary>arrow.down.to.line.compact。</summary>
    DownToLine,

    /// <summary>shippingbox。</summary>
    Box,

    /// <summary>slider.horizontal.3。</summary>
    Sliders,

    /// <summary>hand.raised.fill。</summary>
    HandRaised,

    /// <summary>pencil —— 手动导入。</summary>
    Pencil,
}

/// <summary>图标里的一个形状：一段几何 + 是否填充。</summary>
/// <param name="Geometry">24×24 设计网格里的几何。</param>
/// <param name="Filled">true 表示实心绘制（如圆点），false 表示描边绘制。</param>
public readonly record struct TaIconShape(Geometry Geometry, bool Filled);

/// <summary>
/// 图标几何库。
///
/// ⚠️ **最大的移植缺口**：SF Symbols 在 Windows 上完全没有等价物（参考文档 §14 高风险项、
/// §12「SF Symbol 无 Windows 等价物」）。本类用**手绘的 24×24 矢量几何**替代，
/// 形状取自开源图标集 Lucide（MIT）的同名图标的几何意图，并做了统一：
/// - 网格统一 24×24，默认描边 2，圆角与圆头端点一致；
/// - 不依赖任何字体（不用 Segoe Fluent Icons，也不装 Lucide 字体），
///   因此在任何 Windows 版本、任何 DPI 下都不会出现「缺字形变豆腐块」。
///
/// 每个语义名都标注了它替代的 SF Symbol，完整映射表见项目报告。
/// </summary>
public static class TaIcons
{
    private static readonly Dictionary<TaIconKind, TaIconShape[]> Shapes = Build();

    /// <summary>取图标的全部形状。</summary>
    public static TaIconShape[] Get(TaIconKind kind)
        => Shapes.TryGetValue(kind, out var shapes) ? shapes : Array.Empty<TaIconShape>();

    /// <summary>取图标全部形状合并成一个几何（用于无障碍名或命中测试）。</summary>
    public static Geometry Merged(TaIconKind kind)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var shape in Get(kind))
        {
            group.Children.Add(shape.Geometry);
        }

        group.Freeze();
        return group;
    }

    /// <summary>该图标是否需要用虚线绘制（dashed circle / dashed rect）。</summary>
    public static bool IsDashed(TaIconKind kind) => kind is TaIconKind.DashedCircle or TaIconKind.DashedRect;

    private static Dictionary<TaIconKind, TaIconShape[]> Build()
    {
        var map = new Dictionary<TaIconKind, TaIconShape[]>();

        void Add(TaIconKind kind, params TaIconShape[] shapes) => map[kind] = shapes;

        // 便捷构造
        TaIconShape S(string path) => new(G(path), false);
        TaIconShape F(string path) => new(G(path), true);

        Add(TaIconKind.LockShield,
            S("M12 2.5 L19.5 5.5 V11.5 C19.5 16 16.4 19.2 12 21 C7.6 19.2 4.5 16 4.5 11.5 V5.5 Z"),
            S("M9.2 11 H14.8 V16.4 H9.2 Z"),
            S("M10.2 11 V9.3 A1.8 1.8 0 0 1 13.8 9.3 V11"));

        Add(TaIconKind.LockShieldFilled,
            F("M12 2.5 L19.5 5.5 V11.5 C19.5 16 16.4 19.2 12 21 C7.6 19.2 4.5 16 4.5 11.5 V5.5 Z"),
            S("M9.2 11 H14.8 V16.4 H9.2 Z"),
            S("M10.2 11 V9.3 A1.8 1.8 0 0 1 13.8 9.3 V11"));

        Add(TaIconKind.Gear,
            S("M12 3.8 A8.2 8.2 0 1 0 12 20.2 A8.2 8.2 0 1 0 12 3.8 Z"),
            S("M12 9 A3 3 0 1 0 12 15 A3 3 0 1 0 12 9 Z"),
            S("M12 1.4 V3.8"),
            S("M12 20.2 V22.6"),
            S("M1.4 12 H3.8"),
            S("M20.2 12 H22.6"),
            S("M4.6 4.6 L6.4 6.4"),
            S("M17.6 6.4 L19.4 4.6"),
            S("M6.4 17.6 L4.6 19.4"),
            S("M17.6 17.6 L19.4 19.4"));

        // command（⌘）在 Windows 上没有对应字形；语义上是「键盘快捷键」，
        // 因此用键盘图形替代（见报告映射表）。
        Add(TaIconKind.Command,
            S("M3 7 H21 A1.5 1.5 0 0 1 22.5 8.5 V15.5 A1.5 1.5 0 0 1 21 17 H3 A1.5 1.5 0 0 1 1.5 15.5 V8.5 A1.5 1.5 0 0 1 3 7 Z"),
            S("M6 10.2 H7.4"),
            S("M10.3 10.2 H11.7"),
            S("M14.6 10.2 H16"),
            S("M18.9 10.2 H19.9"),
            S("M7.5 13.2 H16.5"));

        Add(TaIconKind.TextViewfinder,
            S("M4 8 V5 A1 1 0 0 1 5 4 H8"),
            S("M16 4 H19 A1 1 0 0 1 20 5 V8"),
            S("M20 16 V19 A1 1 0 0 1 19 20 H16"),
            S("M8 20 H5 A1 1 0 0 1 4 19 V16"),
            S("M7.4 10 H16.6"),
            S("M7.4 13.4 H16.6"),
            S("M10 16.8 H14"));

        Add(TaIconKind.BookClosed,
            S("M5 3.5 H13.5 A2 2 0 0 1 15.5 5.5 V20.5 H5 A1.5 1.5 0 0 1 3.5 19 V5 A1.5 1.5 0 0 1 5 3.5 Z"),
            S("M15.5 20.5 V5.5 A2 2 0 0 0 13.5 3.5"),
            S("M6.6 8.4 H11.8"),
            S("M6.6 11.8 H11.8"));

        Add(TaIconKind.Sparkles,
            F("M11 2.6 L12.7 8.3 L18.4 10 L12.7 11.7 L11 17.4 L9.3 11.7 L3.6 10 L9.3 8.3 Z"),
            F("M18 15 L18.7 17.3 L21 18 L18.7 18.7 L18 21 L17.3 18.7 L15 18 L17.3 17.3 Z"));

        Add(TaIconKind.Cpu,
            S("M6.5 6.5 H17.5 V17.5 H6.5 Z"),
            S("M9.6 9.6 H14.4 V14.4 H9.6 Z"),
            S("M10 3 V6.5"),
            S("M14 3 V6.5"),
            S("M10 17.5 V21"),
            S("M14 17.5 V21"),
            S("M3 10 H6.5"),
            S("M3 14 H6.5"),
            S("M17.5 10 H21"),
            S("M17.5 14 H21"));

        Add(TaIconKind.CheckCircle,
            S("M12 3.4 A8.6 8.6 0 1 0 12 20.6 A8.6 8.6 0 1 0 12 3.4 Z"),
            S("M7.9 12.2 L10.8 15.1 L16.3 9.2"));

        Add(TaIconKind.CheckCircleOutline,
            S("M12 3.4 A8.6 8.6 0 1 0 12 20.6 A8.6 8.6 0 1 0 12 3.4 Z"),
            S("M7.9 12.2 L10.8 15.1 L16.3 9.2"));

        Add(TaIconKind.WarningTriangle,
            S("M12 3.8 L21.2 19.6 H2.8 Z"),
            S("M12 9.2 V13.8"),
            S("M12 16.2 V16.9"));

        Add(TaIconKind.InfoCircle,
            S("M12 3.4 A8.6 8.6 0 1 0 12 20.6 A8.6 8.6 0 1 0 12 3.4 Z"),
            S("M12 10.6 V16.4"),
            S("M12 7.2 V7.9"));

        Add(TaIconKind.OctagonX,
            S("M8.2 3.4 H15.8 L20.6 8.2 V15.8 L15.8 20.6 H8.2 L3.4 15.8 V8.2 Z"),
            S("M9.3 9.3 L14.7 14.7"),
            S("M14.7 9.3 L9.3 14.7"));

        Add(TaIconKind.XCircle,
            S("M12 3.4 A8.6 8.6 0 1 0 12 20.6 A8.6 8.6 0 1 0 12 3.4 Z"),
            S("M9.1 9.1 L14.9 14.9"),
            S("M14.9 9.1 L9.1 14.9"));

        Add(TaIconKind.ShieldCheck,
            S("M12 2.6 L19.4 5.5 V11.4 C19.4 15.9 16.3 19.1 12 20.9 C7.7 19.1 4.6 15.9 4.6 11.4 V5.5 Z"),
            S("M8.7 11.6 L11.1 14 L15.4 9.5"));

        Add(TaIconKind.Network,
            S("M5.4 5.4 A2.6 2.6 0 1 0 10.6 5.4 A2.6 2.6 0 1 0 5.4 5.4 Z"),
            S("M13.4 5.4 A2.6 2.6 0 1 0 18.6 5.4 A2.6 2.6 0 1 0 13.4 5.4 Z"),
            S("M9.4 18.6 A2.6 2.6 0 1 0 14.6 18.6 A2.6 2.6 0 1 0 9.4 18.6 Z"),
            S("M7.9 5.4 H16.1"),
            S("M6.9 7.7 L10.4 16.2"),
            S("M17.1 7.7 L13.6 16.2"));

        Add(TaIconKind.Dot, new TaIconShape(new EllipseGeometry(new Point(12, 12), 4.2, 4.2), true));

        Add(TaIconKind.DashedCircle,
            S("M12 3.4 A8.6 8.6 0 1 0 12 20.6 A8.6 8.6 0 1 0 12 3.4 Z"));

        Add(TaIconKind.Plus,
            S("M12 5.4 V18.6"),
            S("M5.4 12 H18.6"));

        Add(TaIconKind.Minus, S("M5.6 12 H18.4"));

        Add(TaIconKind.ChevronDown, S("M6.2 9.4 L12 15.2 L17.8 9.4"));

        Add(TaIconKind.Ellipsis,
            F("M5.2 10.4 A1.6 1.6 0 1 0 8.4 10.4 A1.6 1.6 0 1 0 5.2 10.4 Z"),
            F("M10.4 10.4 A1.6 1.6 0 1 0 13.6 10.4 A1.6 1.6 0 1 0 10.4 10.4 Z"),
            F("M15.6 10.4 A1.6 1.6 0 1 0 18.8 10.4 A1.6 1.6 0 1 0 15.6 10.4 Z"));

        Add(TaIconKind.Trash,
            S("M4.4 7 H19.6"),
            S("M9.6 7 V5.2 A1.4 1.4 0 0 1 11 3.8 H13 A1.4 1.4 0 0 1 14.4 5.2 V7"),
            S("M6.4 7 L7.3 19 A1.6 1.6 0 0 0 8.9 20.5 H15.1 A1.6 1.6 0 0 0 16.7 19 L17.6 7"),
            S("M10.4 10.8 V16.4"),
            S("M13.6 10.8 V16.4"));

        Add(TaIconKind.Copy,
            S("M6.4 6.4 H14.6 A1.6 1.6 0 0 1 16.2 8 V16.6 A1.6 1.6 0 0 1 14.6 18.2 H6.4 A1.6 1.6 0 0 1 4.8 16.6 V8 A1.6 1.6 0 0 1 6.4 6.4 Z"),
            S("M8.4 4.8 H18.2 A1.4 1.4 0 0 1 19.6 6.2 V15.4"));

        Add(TaIconKind.Check, S("M4.8 12.4 L9.6 17.2 L19.2 6.8"));

        Add(TaIconKind.ArrowRight,
            S("M4.4 12 H19"),
            S("M13.6 6.4 L19 12 L13.6 17.6"));

        Add(TaIconKind.Rotate,
            S("M20.2 12 A8.2 8.2 0 1 1 16.4 5.3"),
            S("M13.4 4.6 L16.4 5.3 L15.7 8.3"));

        Add(TaIconKind.Terminal,
            S("M3.4 5.4 H20.6 A1.2 1.2 0 0 1 21.8 6.6 V17.4 A1.2 1.2 0 0 1 20.6 18.6 H3.4 A1.2 1.2 0 0 1 2.2 17.4 V6.6 A1.2 1.2 0 0 1 3.4 5.4 Z"),
            S("M6.6 9.8 L9.9 12 L6.6 14.2"),
            S("M12.2 14.4 H17.2"));

        Add(TaIconKind.Key,
            S("M10.4 10 A3.6 3.6 0 1 0 10.4 17.2 A3.6 3.6 0 1 0 10.4 10 Z"),
            S("M13.2 11 L19.6 4.6"),
            S("M15.4 8.8 L13.4 10.8"),
            S("M17.8 6.4 L15.8 8.4"));

        Add(TaIconKind.Photo,
            S("M4 6.4 H20 A1.2 1.2 0 0 1 21.2 7.6 V17.4 A1.2 1.2 0 0 1 20 18.6 H4 A1.2 1.2 0 0 1 2.8 17.4 V7.6 A1.2 1.2 0 0 1 4 6.4 Z"),
            S("M8.6 10.4 A1.7 1.7 0 1 0 12 10.4 A1.7 1.7 0 1 0 8.6 10.4 Z"),
            S("M4.6 17 L9.4 12.6 L12.8 16 L15.8 13.6 L19.4 17"));

        Add(TaIconKind.Bubble,
            S("M4.4 6.4 H19.6 A1.6 1.6 0 0 1 21.2 8 V15.4 A1.6 1.6 0 0 1 19.6 17 H9.4 L5.4 20.2 V17 H4.4 A1.6 1.6 0 0 1 2.8 15.4 V8 A1.6 1.6 0 0 1 4.4 6.4 Z"),
            S("M7.4 10 H16.6"),
            S("M7.4 13.2 H13.4"));

        Add(TaIconKind.Cursor,
            F("M5 3.8 L5 15.6 L7.8 13.1 L9.8 18.1 L11.8 17.1 L9.8 12.3 L14 12.3 Z"));

        Add(TaIconKind.CloudUpload,
            S("M7.6 18.6 A3.9 3.9 0 0 1 7.9 10.8 A5 5 0 0 1 17.3 9.4 A3.5 3.5 0 0 1 17.4 18.6 Z"),
            S("M12 15.6 V9.6"),
            S("M9.6 12 L12 9.6 L14.4 12"));

        Add(TaIconKind.Cloud,
            S("M7.6 18.6 A3.9 3.9 0 0 1 7.9 10.8 A5 5 0 0 1 17.3 9.4 A3.5 3.5 0 0 1 17.4 18.6 Z"));

        Add(TaIconKind.Viewfinder,
            S("M3 8.4 V6 A1 1 0 0 1 4 5 H7"),
            S("M17 5 H20 A1 1 0 0 1 21 6 V8.4"),
            S("M21 15.6 V18 A1 1 0 0 1 20 19 H17"),
            S("M7 19 H4 A1 1 0 0 1 3 18 V15.6"),
            F("M10.2 10.2 A1.8 1.8 0 1 0 13.8 10.2 A1.8 1.8 0 1 0 10.2 10.2 Z"));

        Add(TaIconKind.DashedRect,
            S("M6.4 4.6 H17.6 A1.8 1.8 0 0 1 19.4 6.4 V17.6 A1.8 1.8 0 0 1 17.6 19.4 H6.4 A1.8 1.8 0 0 1 4.6 17.6 V6.4 A1.8 1.8 0 0 1 6.4 4.6 Z"));

        Add(TaIconKind.Pin,
            F("M12 21.2 C9.4 17.6 6.4 14 6.4 10.4 A5.6 5.6 0 1 1 17.6 10.4 C17.6 14 14.6 17.6 12 21.2 Z"),
            S("M12 7.9 A2.1 2.1 0 1 0 12 12.1 A2.1 2.1 0 1 0 12 7.9 Z"));

        Add(TaIconKind.Clipboard,
            S("M9 4.4 H15 A1.5 1.5 0 0 1 16.5 5.9 V7 H19 A1.5 1.5 0 0 1 20.5 8.5 V19 A1.5 1.5 0 0 1 19 20.5 H5 A1.5 1.5 0 0 1 3.5 19 V8.5 A1.5 1.5 0 0 1 5 7 H7.5 V5.9 A1.5 1.5 0 0 1 9 4.4 Z"),
            S("M7.6 5.9 V4.4 A1.5 1.5 0 0 1 9.1 2.9 H14.9 A1.5 1.5 0 0 1 16.4 4.4 V5.9"),
            S("M8 11.4 H16"),
            S("M8 15.4 H13.4"));

        Add(TaIconKind.DownToLine,
            S("M12 4.4 V15.6"),
            S("M8 11.6 L12 15.6 L16 11.6"),
            S("M5.2 19.6 H18.8"));

        Add(TaIconKind.Box,
            S("M3.4 8 L12 3.4 L20.6 8 V16 L12 20.6 L3.4 16 Z"),
            S("M3.4 8 L12 12.6 L20.6 8"),
            S("M12 12.6 V20.6"));

        Add(TaIconKind.Sliders,
            S("M4 7.4 H20"),
            S("M4 12 H20"),
            S("M4 16.6 H20"),
            S("M9 5.6 A1.8 1.8 0 1 0 12.6 5.6 A1.8 1.8 0 1 0 9 5.6 Z"),
            S("M15 10.2 A1.8 1.8 0 1 0 18.6 10.2 A1.8 1.8 0 1 0 15 10.2 Z"),
            S("M7 14.8 A1.8 1.8 0 1 0 10.6 14.8 A1.8 1.8 0 1 0 7 14.8 Z"));

        Add(TaIconKind.HandRaised,
            S("M8 11.6 V5.8 A1.6 1.6 0 0 1 11.2 5.8 V11"),
            S("M11.2 10.2 V4.6 A1.6 1.6 0 0 1 14.4 4.6 V10.2"),
            S("M14.4 10.8 V6.6 A1.6 1.6 0 0 1 17.6 6.6 V13.6 A6 6 0 0 1 11.6 19.6 H10.2 A6.4 6.4 0 0 1 6.2 18.2 L4.3 15.7 A1.7 1.7 0 0 1 6.6 13.6 L8 15.4"));

        Add(TaIconKind.Pencil,
            S("M4.4 19.6 L5.4 15.2 L16 4.6 A2 2 0 0 1 18.8 7.4 L8.2 18 L4.4 19.6 Z"),
            S("M14.6 6 L18 9.4"));

        // 冻结所有几何，避免命中测试/渲染时反复解析
        foreach (var shapes in map.Values)
        {
            for (var i = 0; i < shapes.Length; i++)
            {
                shapes[i].Geometry.Freeze();
            }
        }

        return map;
    }

    private static Geometry G(string path)
    {
        try
        {
            return Geometry.Parse(path);
        }
        catch (FormatException)
        {
            // 图标几何写错不应该让整个设置界面崩掉，退化成空几何。
            return Geometry.Empty;
        }
    }
}

/// <summary>
/// 图标渲染元素。纯矢量，不依赖字体。
/// </summary>
public sealed class TaIcon : FrameworkElement
{
    /// <summary>图标语义。</summary>
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(TaIconKind), typeof(TaIcon),
        new FrameworkPropertyMetadata(TaIconKind.Dot, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>边长（正方形）。</summary>
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(TaIcon),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>描边/填充颜色。</summary>
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(TaIcon),
        new FrameworkPropertyMetadata(Brand.TaBrushes.Ink, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>描边粗细，默认 2（与图标设计网格一致）。</summary>
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(TaIcon),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public TaIconKind Kind
    {
        get => (TaIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Brush? Brush
    {
        get => (Brush?)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var shapes = TaIcons.Get(Kind);
        if (shapes.Length == 0)
        {
            return;
        }

        var brush = Brush ?? Brand.TaBrushes.Ink;
        var pen = new Pen(brush, Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (TaIcons.IsDashed(Kind))
        {
            pen.DashStyle = new DashStyle(new double[] { 2.4, 2.0 }, 0);
        }
        pen.Freeze();

        // 图标在 24×24 的网格里设计，缩放到 Size
        var scale = Size / 24.0;
        dc.PushTransform(new ScaleTransform(scale, scale));
        foreach (var shape in shapes)
        {
            if (shape.Filled)
            {
                dc.DrawGeometry(brush, null, shape.Geometry);
            }
            else
            {
                dc.DrawGeometry(null, pen, shape.Geometry);
            }
        }

        dc.Pop();
    }
}
