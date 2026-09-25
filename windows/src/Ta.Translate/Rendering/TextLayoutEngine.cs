namespace Ta.Translate.Rendering;

/// <summary>
/// 文字换行与块度量。
///
/// 对应 Mac 的 <c>NSParagraphStyle(lineBreakMode: .byWordWrapping)</c> +
/// <c>NSString.boundingRect(options: [.usesLineFragmentOrigin, .usesFontLeading])</c>。
///
/// ⚠️ 中文保真差距（如实说明）：AppKit 的 byWordWrapping 由 CoreText 排版器驱动，
/// 对 CJK 按字符断行、对拉丁按词断行、并正确处理标点悬挂等规则。
/// GDI 没有排版器，本实现用「按词贪心 + CJK 逐字」的近似换行：
/// · 拉丁连续字符算一个「词」，整词换行；超宽词再按字符拆；
/// · CJK 码位逐字成词，因此天然按字符断行；
/// · 空白折叠为单个空格（Mac 不折叠，连续空格宽度会略有差异）。
/// 行高一律 = 字体行距（tmHeight + tmExternalLeading），不使用 Mac 的字体 leading 细节。
/// 在常见截图文案（短句、标题、UI 标签）上与 Mac 结果接近，长段落排版会有可见差异。
/// </summary>
internal static class TextLayoutEngine
{
    /// <summary>一段排版好的文字：行列表 + 最大行宽 + 总高度。</summary>
    public sealed class Layout
    {
        public required IReadOnlyList<string> Lines { get; init; }

        /// <summary>最宽行宽度（像素）。对应 Mac boundingRect 的 size.width。</summary>
        public required double MaxLineWidth { get; init; }

        /// <summary>总高度 = 行数 × 行距。对应 Mac boundingRect 的 size.height。</summary>
        public required double TotalHeight { get; init; }
    }

    /// <summary>
    /// 按 <paramref name="contentWidth"/> 换行并度量。
    /// 对应 Mac: textHeight（:224-232）与 drawFittedText（:193-211）里的度量。
    /// </summary>
    public static Layout WrapAndMeasure(
        GdiTextEngine engine,
        string text,
        double contentWidth,
        string face,
        int weight,
        double pointSize)
    {
        var font = engine.GetFont(face, weight, pointSize);
        var lineHeight = font.LineHeight;
        var lines = WrapLines(engine, text, contentWidth, font);

        var maxWidth = 0d;
        foreach (var line in lines)
        {
            maxWidth = Math.Max(maxWidth, font.MeasureWidth(engine, line));
        }

        return new Layout
        {
            Lines = lines,
            MaxLineWidth = maxWidth,
            TotalHeight = lines.Count * lineHeight,
        };
    }

    /// <summary>按词贪心换行（CJK 逐字）。返回至少一行（可能为空串）。</summary>
    public static IReadOnlyList<string> WrapLines(
        GdiTextEngine engine,
        string text,
        double contentWidth,
        GdiFont font)
    {
        var lines = new List<string>();
        var current = new System.Text.StringBuilder();
        var currentWidth = 0d;

        // 空白宽度按单个空格量（近似：Mac 按实际空白宽度量）。
        var spaceWidth = string.IsNullOrEmpty(text)
            ? 0
            : font.MeasureWidth(engine, " ");

        void Flush()
        {
            lines.Add(current.ToString());
            current.Clear();
            currentWidth = 0;
        }

        foreach (var token in Tokenize(text))
        {
            if (token.IsHardBreak)
            {
                Flush();
                continue;
            }

            if (token.IsWhitespace)
            {
                // 行首丢弃前导空白；行内折叠为单空格。
                if (current.Length > 0)
                {
                    current.Append(' ');
                    currentWidth += spaceWidth;
                }

                continue;
            }

            var tokenWidth = font.MeasureWidth(engine, token.Text);

            // 当前行放不下且已有内容 → 换行。
            if (currentWidth > 0 && currentWidth + tokenWidth > contentWidth + 1e-6)
            {
                Flush();
            }

            // 单词本身超过行宽（超长 URL 等）：按字符硬拆，Mac 的排版器也会拆。
            if (tokenWidth > contentWidth + 1e-6 && current.Length == 0)
            {
                foreach (var ch in token.Text.EnumerateRunes())
                {
                    var piece = ch.ToString();
                    var pieceWidth = font.MeasureWidth(engine, piece);
                    if (currentWidth > 0 && currentWidth + pieceWidth > contentWidth + 1e-6)
                    {
                        Flush();
                    }

                    current.Append(piece);
                    currentWidth += pieceWidth;
                }

                continue;
            }

            current.Append(token.Text);
            currentWidth += tokenWidth;
        }

        Flush();

        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        return lines;
    }

    private readonly struct Token
    {
        public Token(string text, bool isHardBreak, bool isWhitespace)
        {
            Text = text;
            IsHardBreak = isHardBreak;
            IsWhitespace = isWhitespace;
        }

        public string Text { get; }

        public bool IsHardBreak { get; }

        public bool IsWhitespace { get; }
    }

    /// <summary>
    /// 分词：换行符 → 硬断；空白串 → 空白 token；CJK 码位 → 单字 token；
    /// 其余连续字符 → 一个词 token。
    /// </summary>
    private static IEnumerable<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var word = new System.Text.StringBuilder();

        void EmitWord()
        {
            if (word.Length > 0)
            {
                tokens.Add(new Token(word.ToString(), false, false));
                word.Clear();
            }
        }

        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;

            if (value == '\n' || value == '\r')
            {
                EmitWord();
                tokens.Add(new Token(string.Empty, true, false));
                continue;
            }

            if (char.IsWhiteSpace((char)value) || value == 0x3000)
            {
                EmitWord();

                // 连续空白合并成一个 token（行内折叠为单空格）。
                tokens.Add(new Token(" ", false, true));
                continue;
            }

            if (FontFaces.IsCjkCodePoint(value))
            {
                EmitWord();
                tokens.Add(new Token(rune.ToString(), false, false));
                continue;
            }

            word.Append(rune);
        }

        EmitWord();
        return tokens;
    }
}
