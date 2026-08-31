using Ta.Windows.Core.Capture;

namespace Ta.Windows.Core.Export;

public static class SafeFileName
{
    private static readonly HashSet<char> InvalidCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static string Create(
        DateTimeOffset capturedAt,
        CaptureKind kind,
        string? source = null)
    {
        var kindName = kind switch
        {
            CaptureKind.Region => "region",
            CaptureKind.FullDesktop => "full-desktop",
            CaptureKind.RepeatedRegion => "repeat-region",
            CaptureKind.SmartText => "smart-text",
            CaptureKind.Window => "window",
            _ => "capture",
        };

        var baseName = $"Ta_{capturedAt:yyyyMMdd_HHmmss}_{kindName}";
        var safeSource = Sanitize(source);
        return string.IsNullOrEmpty(safeSource)
            ? baseName
            : $"{baseName}_{safeSource}";
    }

    private static string Sanitize(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var characters = source.Trim()
            .Select(character => InvalidCharacters.Contains(character) || char.IsControl(character)
                ? '_'
                : character)
            .ToArray();

        return new string(characters).Trim().TrimEnd('.');
    }
}
