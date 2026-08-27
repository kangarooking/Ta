namespace Ta.Windows.Core.Settings;

public sealed record ShortcutSettings(
    string SmartText,
    string Region,
    string Window,
    string FullDesktop,
    string RepeatRegion);

public sealed record CaptureOutputSettings(
    bool AutoSave,
    bool ShowResultWindow,
    bool HideApplicationDuringCapture,
    string TemporaryDirectory);

public sealed record VisionApiSettings(
    string BaseUrl,
    string Model,
    string TaskPrompt,
    int TimeoutSeconds,
    int MaximumImageDimension,
    int JpegQuality);

public sealed record AppSettings(
    ShortcutSettings Shortcuts,
    CaptureOutputSettings Output,
    VisionApiSettings Vision);
