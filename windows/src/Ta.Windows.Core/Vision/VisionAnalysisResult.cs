namespace Ta.Windows.Core.Vision;

public sealed record VisionAnalysisResult(
    string Text,
    string ProviderLabel,
    string Model,
    bool CloudUploaded)
{
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}
