using System.Globalization;

namespace WallpaperField.Infrastructure;

internal sealed record LaunchOptions
{
    public string? SourceDirectory { get; init; }

    public string? OutputDirectory { get; init; }

    public string Page { get; init; } = "scan";

    public string? SnapshotPath { get; init; }

    public double? Width { get; init; }

    public double? Height { get; init; }

    public bool StartScan { get; init; }

    public bool ReducedMotion { get; init; }

    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        string? source = null;
        string? output = null;
        string page = "scan";
        string? snapshot = null;
        double? width = null;
        double? height = null;
        var startScan = false;
        var reducedMotion = false;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            string? NextValue() => index + 1 < args.Count ? args[++index] : null;

            switch (argument.ToLowerInvariant())
            {
                case "--source":
                    source = NextValue();
                    break;
                case "--output":
                    output = NextValue();
                    break;
                case "--page":
                    page = NextValue()?.Trim().ToLowerInvariant() ?? page;
                    break;
                case "--snapshot":
                    snapshot = NextValue();
                    break;
                case "--width":
                    if (double.TryParse(NextValue(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedWidth))
                    {
                        width = parsedWidth;
                    }
                    break;
                case "--height":
                    if (double.TryParse(NextValue(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedHeight))
                    {
                        height = parsedHeight;
                    }
                    break;
                case "--scan":
                    startScan = true;
                    break;
                case "--reduced-motion":
                    reducedMotion = true;
                    break;
            }
        }

        return new LaunchOptions
        {
            SourceDirectory = source,
            OutputDirectory = output,
            Page = page,
            SnapshotPath = snapshot,
            Width = width,
            Height = height,
            StartScan = startScan,
            ReducedMotion = reducedMotion
        };
    }
}
