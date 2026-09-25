using Ta.OCR.Models;

namespace Ta.OCR.Text;

/// <summary>
/// 识别行的阅读顺序排序。
///
/// 对应 Mac 版 <c>VisionOCRService.recognizeLegacy</c> 里的行内排序闭包
/// （<c>AIScreenshotCore/OCR/VisionOCRService.swift:65-71</c>），以及
/// macOS 26 分支的同名闭包（`:136-140`）。
///
/// ⚠️ 与 <see cref="OCRDocumentLayoutAnalyzer"/> 的 <c>readingOrder</c> **不是同一个比较器**：
/// 这里垂直阈值是**固定 0.015**，而分析器用的是
/// <c>max(0.012, max(h1,h2)*0.65)</c>。Mac 版同样分成两处，逐字保留。
/// </summary>
public static class RecognitionLineOrdering
{
    /// <summary>垂直同行的判定阈值（`:67`、`:138`）。</summary>
    public const double VerticalTolerance = 0.015;

    public static IReadOnlyList<OCRTextLine> Sort(IReadOnlyList<OCRTextLine> lines)
    {
        return lines
            .OrderBy(line => line, VisionLineOrderComparer.Instance)
            .ToList();
    }

    private sealed class VisionLineOrderComparer : IComparer<OCRTextLine>
    {
        public static readonly VisionLineOrderComparer Instance = new();

        public int Compare(OCRTextLine lhs, OCRTextLine rhs)
        {
            var verticalDistance = Math.Abs(lhs.BoundingBox.MidY - rhs.BoundingBox.MidY);

            // :67-68 —— 同一行按 minX 升序（从左到右）。
            if (verticalDistance < VerticalTolerance)
            {
                return lhs.BoundingBox.MinX.CompareTo(rhs.BoundingBox.MinX);
            }

            // :69-70 —— 否则 midY 大的在前（Y 轴向上）。
            return rhs.BoundingBox.MidY.CompareTo(lhs.BoundingBox.MidY);
        }
    }
}
