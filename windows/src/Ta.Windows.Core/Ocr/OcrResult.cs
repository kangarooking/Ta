namespace Ta.Windows.Core.Ocr;

public sealed record OcrWord(
    string Text,
    double X,
    double Y,
    double Width,
    double Height);

public sealed record OcrLine(
    string Text,
    IReadOnlyList<OcrWord> Words);

public sealed record OcrResult(
    string Text,
    IReadOnlyList<OcrLine> Lines,
    string EngineLabel,
    bool IsLocal,
    string? LanguageTag)
{
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}
