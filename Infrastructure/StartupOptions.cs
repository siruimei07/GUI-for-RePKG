using System.Globalization;
using WallpaperField.Models;

namespace WallpaperField.Infrastructure;

internal sealed record StartupOptions
{
    private const double MaximumWindowDimension = 16_384d;

    private static readonly Dictionary<string, OptionDescriptor> OptionTable =
        new Dictionary<string, OptionDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["--source"] = new(true, ParseRequiredText, (state, value) => state.SourceDirectory = (string)value!),
            ["--output"] = new(true, ParseRequiredText, (state, value) => state.OutputDirectory = (string)value!),
            ["--page"] = new(true, ParsePage, (state, value) => state.Page = (string)value!),
            ["--snapshot"] = new(true, ParseRequiredText, (state, value) => state.SnapshotPath = (string)value!),
            ["--width"] = new(true, ParseWindowDimension, (state, value) => state.Width = (double)value!),
            ["--height"] = new(true, ParseWindowDimension, (state, value) => state.Height = (double)value!),
            ["--scan"] = new(false, ParseSwitch, (state, _) => state.StartScan = true),
            ["--reduced-motion"] = new(false, ParseSwitch, (state, _) => state.ReducedMotion = true),
            ["--scroll-index"] = new(true, ParseScrollIndex, (state, value) => state.ScrollIndex = (int)value!)
        };

    public string? SourceDirectory { get; init; }
    public string? OutputDirectory { get; init; }
    public string Page { get; init; } = "scan";
    public string? SnapshotPath { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public bool StartScan { get; init; }
    public bool ReducedMotion { get; init; }
    public int? ScrollIndex { get; init; }

    internal static StartupParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var state = new MutableOptions();
        var acceptedOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<AppIssue>();

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index] ?? string.Empty;
            if (!OptionTable.TryGetValue(argument, out var descriptor))
            {
                issues.Add(CreateIssue(
                    "STARTUP_OPTION_UNKNOWN",
                    AppIssueSeverity.Warning,
                    "发现未知启动参数，已忽略。",
                    "未知参数只忽略自身，不会改变后续参数。",
                    NormalizeContextKey(argument)));
                continue;
            }

            string? value = null;
            if (descriptor.RequiresValue)
            {
                var hasValue = index + 1 < args.Count
                    && !LooksLikeOption(args[index + 1]);
                if (!hasValue)
                {
                    issues.Add(acceptedOptions.Contains(argument)
                        ? CreateDuplicateIssue(argument)
                        : CreateIssue(
                            "STARTUP_OPTION_MISSING_VALUE",
                            AppIssueSeverity.Error,
                            $"启动参数 {argument} 缺少值。",
                            "该参数未生效，后续 flag 仍会继续解析。",
                            argument));
                    continue;
                }

                value = args[++index];
            }

            if (acceptedOptions.Contains(argument))
            {
                issues.Add(CreateDuplicateIssue(argument));
                continue;
            }

            var parsed = descriptor.Parser(value);
            if (!parsed.IsValid)
            {
                issues.Add(CreateIssue(
                    "STARTUP_OPTION_INVALID_VALUE",
                    AppIssueSeverity.Error,
                    $"启动参数 {argument} 的值无效。",
                    parsed.ErrorDetails,
                    argument));
                continue;
            }

            descriptor.Apply(state, parsed.Value);
            acceptedOptions.Add(argument);
        }

        return new StartupParseResult(state.ToOptions(), issues.ToArray());
    }

    private static bool LooksLikeOption(string? value)
        => value?.StartsWith("--", StringComparison.Ordinal) == true;

    private static ParsedOption ParseRequiredText(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? ParsedOption.Invalid("值不能为空。")
            : ParsedOption.Valid(value);

    private static ParsedOption ParseSwitch(string? value)
        => ParsedOption.Valid(true);

    private static ParsedOption ParsePage(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "scan" or "01" or "scan field" => ParsedOption.Valid("scan"),
            "browse" or "02" => ParsedOption.Valid("browse"),
            "library" or "output" or "03" or "output library" => ParsedOption.Valid("library"),
            "problems" or "problem" or "04" => ParsedOption.Valid("problems"),
            _ => ParsedOption.Invalid("页面必须是 scan、browse、library 或 problems。")
        };
    }

    private static ParsedOption ParseWindowDimension(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed)
            || parsed <= 0d
            || parsed > MaximumWindowDimension)
        {
            return ParsedOption.Invalid("窗口尺寸必须是大于 0 且不超过 16384 的有限数字。");
        }

        return ParsedOption.Valid(parsed);
    }

    private static ParsedOption ParseScrollIndex(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
           && parsed >= 0
            ? ParsedOption.Valid(parsed)
            : ParsedOption.Invalid("滚动索引必须是非负整数。");

    private static AppIssue CreateDuplicateIssue(string argument)
        => CreateIssue(
            "STARTUP_OPTION_DUPLICATE",
            AppIssueSeverity.Warning,
            $"启动参数 {argument} 重复，已保留第一个合法值。",
            "后续重复值已忽略。",
            argument);

    private static AppIssue CreateIssue(
        string code,
        AppIssueSeverity severity,
        string summary,
        string details,
        string contextKey)
        => new(
            Guid.NewGuid(),
            code,
            severity,
            AppIssueSource.Startup,
            null,
            DateTimeOffset.UtcNow,
            summary,
            details,
            AppDiskFact.NotModified,
            AppIssueAction.ReviewInput,
            null,
            contextKey,
            AppIssueResolutionState.Open,
            null);

    private static string NormalizeContextKey(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 128 ? trimmed : trimmed[..128];
    }

    private sealed record OptionDescriptor(
        bool RequiresValue,
        Func<string?, ParsedOption> Parser,
        Action<MutableOptions, object?> Apply);

    private sealed record ParsedOption(bool IsValid, object? Value, string ErrorDetails)
    {
        internal static ParsedOption Valid(object? value) => new(true, value, string.Empty);

        internal static ParsedOption Invalid(string details) => new(false, null, details);
    }

    private sealed class MutableOptions
    {
        internal string? SourceDirectory { get; set; }
        internal string? OutputDirectory { get; set; }
        internal string Page { get; set; } = "scan";
        internal string? SnapshotPath { get; set; }
        internal double? Width { get; set; }
        internal double? Height { get; set; }
        internal bool StartScan { get; set; }
        internal bool ReducedMotion { get; set; }
        internal int? ScrollIndex { get; set; }

        internal StartupOptions ToOptions()
            => new()
            {
                SourceDirectory = SourceDirectory,
                OutputDirectory = OutputDirectory,
                Page = Page,
                SnapshotPath = SnapshotPath,
                Width = Width,
                Height = Height,
                StartScan = StartScan,
                ReducedMotion = ReducedMotion,
                ScrollIndex = ScrollIndex
            };
    }
}

internal sealed record StartupParseResult(
    StartupOptions Options,
    IReadOnlyList<AppIssue> Issues);
