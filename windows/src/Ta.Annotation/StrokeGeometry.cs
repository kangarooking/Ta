using Ta.Core.Agent;
using Ta.Core.Drawing;

namespace Ta.Annotation;

/// <summary>
/// 描边几何：把折线「膨胀」成单一闭合多边形，再由 <see cref="SoftwareCanvas"/> 非零环绕填充。
///
/// 对应 Mac 版 CGContext 的描边调用：
///   · 圆头/圆角连接  ← context.setLineCap(.round) / setLineJoin(.round)（Mac:126-127）
///   · 方头（虚线段、闭合的矩形/椭圆轮廓）← CGContext 默认 .butt cap
///   · 虚线           ← context.setLineDash(phase: 0, lengths: [max(4,w*2), max(3,w*1.4)])（Mac:354）
///
/// 设计取舍：把描边转成多边形，而不是逐线段填充。
/// 逐线段填充在重叠处会**重复混合**颜色 —— alpha &lt; 1 的高亮笔（#FFD60A59）会因此
/// 出现一圈圈可见接缝。转成单一多边形 + 非零环绕后，重叠区环绕数为 2，
/// 仍然只判定为「内部」一次，混合只发生一次。
///
/// 轮廓构造（左链前进 → 末端端帽 → 右链后退 → 起点端帽）：
///   · 每个拐点插入半径 = lineWidth/2 的**圆弧**，走两法向间的短弧
///     —— 这正是「圆角连接」的几何含义：并集 = 线段矩形 ∪ 拐点圆盘
///   · 圆头端帽 = 半圆弧；方头端帽 = 直接闭合（无弧）
/// </summary>
internal static class StrokeGeometry
{
    /// <summary>整圆的分段数。连接弧与端帽按角度比例换算。</summary>
    public const int FullCircleSegments = 24;

    /// <summary>边长小于该值视为退化，不参与法向计算。</summary>
    private const double LengthEpsilon = 1e-9;

    /// <summary>开放折线的描边轮廓。roundCap 对应 setLineCap(.round)。</summary>
    public static List<PointD> StrokeOutline(IReadOnlyList<PointD> points, double width, bool roundCap)
    {
        if (points.Count < 2)
        {
            return [];
        }

        var half = Math.Max(width, 0.1) / 2;
        var segmentCount = points.Count - 1;
        var normals = ComputeNormals(points);

        var outline = new List<PointD>((segmentCount * 4) + (FullCircleSegments * 2));

        // 起点端帽：从右侧偏移点（法向 + 180°）绕到左侧偏移点，
        // 穿过「起点反方向」—— 这正是 CG 圆头端帽的形状。
        if (roundCap)
        {
            AppendArc(outline, points[0], half, NormalAngle(normals[0]) + Math.PI, -Math.PI);
        }

        // 左链前进：必须包含**首尾两点**。漏掉末段的左偏移点会让多边形退化
        // （方头端帽时尤其致命 —— 左链直接从倒数第二个点跳到右链，面积为零）。
        outline.Add(Offset(points[0], normals[0], half));
        for (var joint = 1; joint < segmentCount; joint++)
        {
            AppendJointArc(outline, points[joint], half, normals[joint - 1], normals[joint]);
            outline.Add(Offset(points[joint], normals[joint], half));
        }

        outline.Add(Offset(points[segmentCount], normals[segmentCount - 1], half));

        // 末端端帽：从左侧偏移点绕到右侧偏移点，穿过「终点正方向」。
        if (roundCap)
        {
            AppendArc(outline, points[segmentCount], half, NormalAngle(normals[segmentCount - 1]), -Math.PI);
        }

        // 右链后退（拐点弧逆序）：同样必须包含首尾两点。
        outline.Add(Offset(points[segmentCount], normals[segmentCount - 1], -half));
        for (var joint = segmentCount - 1; joint >= 1; joint--)
        {
            AppendJointArc(outline, points[joint], half, Negate(normals[joint]), Negate(normals[joint - 1]));
            outline.Add(Offset(points[joint], normals[joint], -half));
        }

        outline.Add(Offset(points[0], normals[0], -half));

        return outline;
    }

    /// <summary>
    /// 闭合路径的描边轮廓：无端帽，首尾相接处同样插入连接弧。
    /// 用于矩形（圆角 5）、椭圆描边、放大镜白环。
    /// </summary>
    public static List<PointD> ClosedStrokeOutline(IReadOnlyList<PointD> points, double width)
    {
        var count = points.Count;
        if (count < 3)
        {
            return [];
        }

        var half = Math.Max(width, 0.1) / 2;
        var normals = ComputeNormalsClosed(points);

        var outline = new List<PointD>((count * 4) + FullCircleSegments)
        {
            Offset(points[0], normals[0], half),
        };

        for (var joint = 1; joint < count; joint++)
        {
            AppendJointArc(outline, points[joint], half, normals[joint - 1], normals[joint]);
            outline.Add(Offset(points[joint], normals[joint], half));
        }

        // 首尾相接的拐点（外圈闭合）。
        AppendJointArc(outline, points[0], half, normals[count - 1], normals[0]);

        // ── 内圈 ────────────────────────────────────────────────
        // 只构建外圈会把矩形/椭圆/圆环渲染成**填充**图形。
        // 正确做法是再走一圈内偏移，两条链方向相反：
        //   · 外圈沿路径正向绕 → 环绕数 +1
        //   · 内圈沿路径反向绕 → 洞内 +1 + (-1) = 0
        // 于是非零环绕规则下「描边环内为实心、洞内为空心」，正是 CG strokePath 的语义。
        outline.Add(Offset(points[0], normals[0], -half));
        for (var joint = count - 1; joint >= 1; joint--)
        {
            AppendJointArc(outline, points[joint], half, Negate(normals[joint]), Negate(normals[joint - 1]));
            outline.Add(Offset(points[joint], normals[joint], -half));
        }

        // 内圈首尾相接的拐点。
        AppendJointArc(outline, points[0], half, Negate(normals[0]), Negate(normals[count - 1]));

        return outline;
    }

    /// <summary>
    /// 按 <c>[dashLength, gapLength]</c>、phase 0 切分折线，返回若干「实线段」。
    /// 与 Mac:354 的 setLineDash(phase: 0, lengths: [max(4, w*2), max(3, w*1.4)]) 对齐。
    /// </summary>
    public static List<List<PointD>> DashPieces(
        IReadOnlyList<PointD> points,
        double dashLength,
        double gapLength,
        bool closed)
    {
        var pieces = new List<List<PointD>>();
        if (points.Count < 2 || dashLength <= 0 || gapLength < 0)
        {
            return pieces;
        }

        var segmentCount = points.Count - 1;
        var on = true;
        var used = 0.0;
        var current = new List<PointD>();

        for (var segment = 0; segment < segmentCount; segment++)
        {
            var segmentLength = Distance(points[segment], points[segment + 1]);
            var remaining = segmentLength;
            var position = 0.0;

            while (remaining > LengthEpsilon)
            {
                var interval = on ? dashLength : gapLength;
                var available = interval - used;
                var take = Math.Min(available, remaining);
                var end = position + take;

                if (on)
                {
                    if (current.Count == 0)
                    {
                        // 虚线段从段中起始时，先把段内起点补进去。
                        current.Add(Lerp(points[segment], points[segment + 1], position / segmentLength));
                    }

                    current.Add(Lerp(points[segment], points[segment + 1], end / segmentLength));
                }
                else if (current.Count > 0)
                {
                    pieces.Add(current);
                    current = [];
                }

                position = end;
                remaining -= take;
                used += take;

                if (used >= interval - LengthEpsilon)
                {
                    used = 0;
                    on = !on;
                }
            }
        }

        if (on && current.Count > 1)
        {
            pieces.Add(current);
        }

        // 闭合路径：若图案在末尾仍处于「实线」且起点也是实线，
        // 首尾两段需接成一个闭环（CG 对闭合子路径同样连续打虚线）。
        if (closed && pieces.Count > 1 && on)
        {
            var merged = new List<PointD>(pieces[^1]);
            merged.AddRange(pieces[0].Skip(1));
            pieces.RemoveAt(pieces.Count - 1);
            pieces[0] = merged;
        }

        return pieces;
    }

    /// <summary>圆（直径 = lineWidth 的填充圆，用于单点画笔）。</summary>
    public static List<PointD> Circle(PointD center, double radius) =>
        Ellipse(center, radius, radius);

    /// <summary>
    /// 椭圆轮廓。分段数按半径自适应，弦高误差约 0.08px。
    /// 用于椭圆描边、编号圆、放大镜 clip 与白环。
    /// </summary>
    public static List<PointD> Ellipse(PointD center, double rx, double ry, int? segmentCount = null)
    {
        if (rx <= 0 || ry <= 0)
        {
            return [];
        }

        var segments = segmentCount ?? EllipseSegments(Math.Max(rx, ry));
        var points = new List<PointD>(segments);
        var step = (2 * Math.PI) / segments;

        for (var i = 0; i < segments; i++)
        {
            var angle = i * step;
            points.Add(new PointD(
                center.X + (Math.Cos(angle) * rx),
                center.Y + (Math.Sin(angle) * ry)));
        }

        return points;
    }

    /// <summary>圆角矩形轮廓。圆角固定 5 —— 对应 Mac:99 的 cornerWidth/cornerHeight: 5。</summary>
    public static List<PointD> RoundedRectangle(AnnotationRect rect, double cornerRadius)
    {
        var radius = Math.Clamp(cornerRadius, 0, Math.Min(rect.Width, rect.Height) / 2);
        if (radius <= 0)
        {
            return Rectangle(rect);
        }

        var points = new List<PointD>();
        var left = rect.X;
        var right = rect.X + rect.Width;
        var top = rect.Y;
        var bottom = rect.Y + rect.Height;

        // 第一段弧**包含起点**，这样多边形闭合时最后一条直边恰好落在 x = left 上；
        // 其余三段不含起点，起点由前一段的终点或中间的直边隐式连接。
        AppendCornerArc(points, new PointD(left + radius, top + radius), radius, Math.PI, Math.PI * 1.5, includeStart: true);
        AppendCornerArc(points, new PointD(right - radius, top + radius), radius, Math.PI * 1.5, Math.PI * 2);
        AppendCornerArc(points, new PointD(right - radius, bottom - radius), radius, 0, Math.PI * 0.5);
        AppendCornerArc(points, new PointD(left + radius, bottom - radius), radius, Math.PI * 0.5, Math.PI);

        return points;
    }

    /// <summary>轴对齐矩形轮廓（4 点）。用于马赛克/模糊的 clip 区域。</summary>
    public static List<PointD> Rectangle(AnnotationRect rect) =>
    [
        new(rect.X, rect.Y),
        new(rect.X + rect.Width, rect.Y),
        new(rect.X + rect.Width, rect.Y + rect.Height),
        new(rect.X, rect.Y + rect.Height),
    ];

    private static void AppendCornerArc(
        List<PointD> target,
        PointD center,
        double radius,
        double from,
        double to,
        bool includeStart = false)
    {
        var segments = Math.Max(2, (int)Math.Ceiling((to - from) / ((2 * Math.PI) / FullCircleSegments)));
        var step = (to - from) / segments;
        var first = includeStart ? 0 : 1;

        for (var i = first; i <= segments; i++)
        {
            var angle = from + (step * i);
            target.Add(new PointD(
                center.X + (Math.Cos(angle) * radius),
                center.Y + (Math.Sin(angle) * radius)));
        }
    }

    /// <summary>在拐点处插入连接弧：从法向 from 绕到 to，走短弧（|Δ| ≤ 180°）。</summary>
    private static void AppendJointArc(List<PointD> target, PointD center, double half, PointD from, PointD to)
    {
        var fromAngle = Math.Atan2(from.Y, from.X);
        var toAngle = Math.Atan2(to.Y, to.X);
        AppendArc(target, center, half, fromAngle, NormalizeDelta(toAngle - fromAngle));
    }

    /// <summary>
    /// 沿半径绕指定角度差追加弧点。
    /// delta 为负表示顺时针（y 向下坐标系下即屏幕顺时针）。
    /// 端帽固定用 -π（半圆），连接弧用归一化到 (-π, π] 的短弧。
    /// </summary>
    private static void AppendArc(List<PointD> target, PointD center, double radius, double fromAngle, double delta)
    {
        var segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / ((2 * Math.PI) / FullCircleSegments)));
        var step = delta / segments;

        for (var i = 0; i <= segments; i++)
        {
            var angle = fromAngle + (step * i);
            target.Add(new PointD(
                center.X + (Math.Cos(angle) * radius),
                center.Y + (Math.Sin(angle) * radius)));
        }
    }

    /// <summary>把角度差归一化到 (-π, π]，即「短弧」。</summary>
    private static double NormalizeDelta(double delta)
    {
        while (delta > Math.PI)
        {
            delta -= 2 * Math.PI;
        }

        while (delta <= -Math.PI)
        {
            delta += 2 * Math.PI;
        }

        return delta;
    }

    private static double NormalAngle(PointD normal) => Math.Atan2(normal.Y, normal.X);

    private static PointD[] ComputeNormals(IReadOnlyList<PointD> points)
    {
        var normals = new PointD[points.Count];
        var fallback = new PointD(0, 1);

        for (var i = 0; i + 1 < points.Count; i++)
        {
            normals[i] = NormalOf(points[i], points[i + 1], ref fallback);
        }

        // 末段法向已由循环填好（index = points.Count - 2 是最后一段）。
        // points.Count - 1 位置不会用到（左链只取到 segmentCount - 1）。
        return normals;
    }

    private static PointD[] ComputeNormalsClosed(IReadOnlyList<PointD> points)
    {
        var normals = new PointD[points.Count];
        var fallback = new PointD(0, 1);

        for (var i = 0; i < points.Count; i++)
        {
            var next = (i + 1) % points.Count;
            normals[i] = NormalOf(points[i], points[next], ref fallback);
        }

        return normals;
    }

    private static PointD NormalOf(PointD from, PointD to, ref PointD fallback)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= LengthEpsilon)
        {
            return fallback;
        }

        var normal = new PointD(-dy / length, dx / length);
        fallback = normal;
        return normal;
    }

    private static PointD Negate(PointD point) => new(-point.X, -point.Y);

    private static PointD Offset(PointD point, PointD normal, double amount) =>
        new(point.X + (normal.X * amount), point.Y + (normal.Y * amount));

    private static PointD Lerp(PointD from, PointD to, double t) =>
        new(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t));

    private static double Distance(PointD a, PointD b) => Math.Sqrt(PointD.DistanceSquared(a, b));

    /// <summary>
    /// 椭圆分段数：弦高误差 ≈ r·(1-cos(π/n)) 保持在 0.08px 量级。
    /// sqrt(r)·8·2 在该误差目标下近似成立。
    /// </summary>
    private static int EllipseSegments(double radius) =>
        Math.Clamp((int)Math.Ceiling(Math.Sqrt(Math.Max(radius, 1)) * 8) * 2, 32, 4096);
}
