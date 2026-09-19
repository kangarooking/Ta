namespace Ta.Core.Agent;

/// <summary>
/// 标注颜色。
///
/// 逐行对应 Mac 版 AnnotationRecipe.swift:100-153 的 AnnotationColor。
///
/// ⚠️ 序列化格式是**十六进制字符串**而非对象，且规则有若干细节必须精确复现：
///   · 接受 <c>#RRGGBB</c>（7 字符）与 <c>#RRGGBBAA</c>（9 字符）
///   · 输出恒为**大写**
///   · 仅当 alpha &lt; 0.999 时才输出 8 位形式，否则省略 alpha
///   · 舍入用「四舍五入远离零」，与 Swift 的 Double.rounded() 一致
///     （.NET 的 Math.Round 默认是银行家舍入，直接换会得到不同结果）
/// </summary>
public readonly record struct AnnotationColor
{
    public double Red { get; }
    public double Green { get; }
    public double Blue { get; }
    public double Alpha { get; }

    public AnnotationColor(double red, double green, double blue, double alpha = 1)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
    }

    /// <summary>alpha 低于该值时输出 8 位形式。对应 Mac: alpha &lt; 0.999。</summary>
    public const double AlphaOmissionThreshold = 0.999;

    // 对应 Mac: AnnotationColor.red = (1, 59/255, 48/255) = #FF3B30
    public static AnnotationColor Red_ { get; } = new(1, 59.0 / 255, 48.0 / 255);

    // 对应 Mac: AnnotationColor.highlighter = (1, 214/255, 10/255, alpha 0.35) = #FFD60A59
    public static AnnotationColor Highlighter { get; } = new(1, 214.0 / 255, 10.0 / 255, 0.35);

    /// <summary>从十六进制串解析。</summary>
    public static bool TryParseHex(string hex, out AnnotationColor color, out string? error)
    {
        color = default;

        if (!hex.StartsWith('#') || (hex.Length != 7 && hex.Length != 9))
        {
            error = $"颜色必须使用 #RRGGBB 或 #RRGGBBAA：{hex}。";
            return false;
        }

        var digits = hex[1..];
        if (!ulong.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out var value))
        {
            error = $"颜色包含无效十六进制字符：{hex}。";
            return false;
        }

        if (digits.Length == 6)
        {
            color = new AnnotationColor(
                ((value >> 16) & 0xFF) / 255.0,
                ((value >> 8) & 0xFF) / 255.0,
                (value & 0xFF) / 255.0,
                1);
        }
        else
        {
            color = new AnnotationColor(
                ((value >> 24) & 0xFF) / 255.0,
                ((value >> 16) & 0xFF) / 255.0,
                ((value >> 8) & 0xFF) / 255.0,
                (value & 0xFF) / 255.0);
        }

        error = null;
        return true;
    }

    /// <summary>序列化为十六进制串。</summary>
    public string ToHex()
    {
        static int Byte(double value) =>
            (int)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);

        return Alpha < AlphaOmissionThreshold
            ? $"#{Byte(Red):X2}{Byte(Green):X2}{Byte(Blue):X2}{Byte(Alpha):X2}"
            : $"#{Byte(Red):X2}{Byte(Green):X2}{Byte(Blue):X2}";
    }

    public override string ToString() => ToHex();
}
