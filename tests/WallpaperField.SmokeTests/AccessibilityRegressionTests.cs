using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Xml.Linq;
using WallpaperField.ViewModels;

internal static class AccessibilityRegressionTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    internal static void Run(Action<bool, string> assert)
    {
        VerifyMotionContract(assert);
        VerifySemanticXaml(assert);
        VerifyAccessibleTheme(assert);
    }

    internal static void VerifyWindow(
        WallpaperField.MainWindow window,
        Action<bool, string> assert)
    {
        foreach (var titleName in new[]
                 {
                     "ScanPageTitle",
                     "LibraryPageTitle",
                     "ProblemsPageTitle"
                 })
        {
            var title = window.FindName(titleName) as TextBlock;
            assert(title is not null
                   && AutomationProperties.GetHeadingLevel(title) == AutomationHeadingLevel.Level1
                   && !title.Focusable,
                $"{titleName} is not a non-tabbable Level1 heading at runtime.");
        }

        foreach (var (name, expected) in new[]
                 {
                     ("ScanNavButton", "扫描中心"),
                     ("LibraryNavButton", "输出壁纸库"),
                     ("ProblemNavButton", "问题中心"),
                     ("RefreshLibraryButton", "刷新输出壁纸库")
                 })
        {
            var element = window.FindName(name) as FrameworkElement;
            assert(element is not null
                   && AutomationProperties.GetName(element) == expected,
                $"{name} lost its stable Chinese automation name.");
        }

        VerifyKeyboardAccess(window, assert);

        window.SetReducedMotion(true);
        Invoke(window, "StartAmbientMotion");
        Invoke(window, "SetBusyAnimation", true);
        Invoke(window, "AnimateCurrentPage");
        foreach (var name in new[]
                 {
                     "BackgroundGridOffset",
                     "SignalBeacon",
                     "CalibrationInstrument",
                     "CalibrationRotation",
                     "ScanView",
                     "LibraryView",
                     "ProblemsView"
                 })
        {
            var target = window.FindName(name);
            var hasAnimatedProperties = target switch
            {
                UIElement element => element.HasAnimatedProperties,
                Animatable animatable => animatable.HasAnimatedProperties,
                _ => true
            };
            assert(target is not null && !hasAnimatedProperties,
                $"Reduced motion left an active animation clock on {name}.");
        }

        var palette = typeof(WallpaperField.MainWindow).GetMethod(
            "ApplyHighContrastPalette",
            BindingFlags.Instance | BindingFlags.NonPublic);
        assert(palette is not null,
            "MainWindow has no runtime High Contrast palette application seam.");
        if (palette is null)
        {
            return;
        }

        try
        {
            palette.Invoke(window, [true]);
            var resources = Application.Current.Resources;
            assert(ReferenceEquals(resources["PaperBrush"], SystemColors.WindowBrush)
                   && ReferenceEquals(resources["TextOnDarkMutedBrush"], SystemColors.WindowBrush)
                   && ReferenceEquals(resources["Paper24Brush"], SystemColors.WindowBrush)
                   && ReferenceEquals(resources["InkBrush"], SystemColors.WindowTextBrush)
                   && ReferenceEquals(resources["SelectionBackgroundBrush"], SystemColors.HighlightBrush)
                   && ReferenceEquals(resources["SelectionTextBrush"], SystemColors.HighlightTextBrush)
                   && ReferenceEquals(resources["SignalTextBrush"], SystemColors.HighlightTextBrush)
                   && ReferenceEquals(resources["DisabledBrush"], SystemColors.GrayTextBrush),
                "High Contrast did not map window/text/selection/disabled tokens to SystemColors.");
        }
        finally
        {
            palette.Invoke(window, [false]);
        }
    }

    private static void VerifyKeyboardAccess(
        WallpaperField.MainWindow window,
        Action<bool, string> assert)
    {
        var shell = window.DataContext as ShellViewModel;
        assert(shell is not null,
            "The live keyboard matrix has no ShellViewModel navigation owner.");
        if (shell is null)
        {
            return;
        }

        foreach (var (route, names) in new[]
                 {
                     ("SCAN", new[]
                     {
                         "ScanNavButton",
                         "SourcePathTextBox",
                         "BrowseSourceButton",
                         "OutputPathTextBox",
                         "BrowseOutputButton",
                         "ScanSearchBox",
                         "OnlyProcessableFilter",
                         "OnlyProblemsFilter",
                         "SelectCurrentMatchesButton",
                         "ClearUnpackSelectionButton",
                         "CompactDensityToggle"
                     }),
                     ("LIBRARY", new[]
                     {
                         "LibraryNavButton",
                         "RefreshLibraryButton",
                         "LibrarySearchBox"
                     }),
                     ("PROBLEMS", new[]
                     {
                         "ProblemNavButton",
                         "ProblemSearchBox",
                         "ProblemSeverityComboBox",
                         "ProblemSourceComboBox",
                         "IncludePathContextsCheckBox"
                     })
                 })
        {
            shell.NavigateTo(route);
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Input);
            foreach (var name in names)
            {
                var control = window.FindName(name) as Control;
                assert(control is { IsVisible: true, Focusable: true }
                       && KeyboardNavigation.GetIsTabStop(control),
                    $"{route} keyboard surface {name} is missing, hidden, or not in the Tab order.");
                if (control is null || !control.IsVisible || !control.IsEnabled)
                {
                    continue;
                }

                FocusManager.SetFocusedElement(window, control);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Input);
                assert(ReferenceEquals(FocusManager.GetFocusedElement(window), control),
                    $"{route} keyboard surface {name} could not receive logical keyboard focus.");
            }
        }
    }

    private static void VerifyMotionContract(Action<bool, string> assert)
    {
        var assembly = typeof(WallpaperField.MainWindow).Assembly;
        var preferenceType = assembly.GetType("WallpaperField.Models.MotionPreference");
        var policyType = assembly.GetType("WallpaperField.Services.MotionPolicy");
        assert(preferenceType is not null && policyType is not null,
            "The approved MotionPreference/MotionPolicy contract is missing.");
        if (preferenceType is null || policyType is null)
        {
            return;
        }

        foreach (var (systemEnabled, requested, expected) in new[]
                 {
                     (true, false, true),
                     (true, true, false),
                     (false, false, false),
                     (false, true, false)
                 })
        {
            var preference = Activator.CreateInstance(
                preferenceType,
                [systemEnabled, requested]);
            var actual = preferenceType.GetProperty("MotionEnabled")?.GetValue(preference);
            assert(actual is bool value && value == expected,
                $"MotionPreference({systemEnabled}, {requested}) produced {actual ?? "<null>"}.");
        }

        using var policy = Activator.CreateInstance(policyType, [false]) as IDisposable;
        var setReduced = policyType.GetMethod("SetReducedMotionRequested");
        var motionEnabled = policyType.GetProperty("MotionEnabled");
        assert(policy is not null && setReduced is not null && motionEnabled is not null,
            "MotionPolicy has no disposable CLI preference surface.");
        if (policy is null || setReduced is null || motionEnabled is null)
        {
            return;
        }

        setReduced.Invoke(policy, [true]);
        assert(motionEnabled.GetValue(policy) is false,
            "CLI reduced motion did not disable the live policy.");
    }

    private static void VerifySemanticXaml(Action<bool, string> assert)
    {
        var document = XDocument.Load(
            FindRepositoryFile("MainWindow.xaml"),
            LoadOptions.PreserveWhitespace);
        foreach (var (header, title, expectedText) in new[]
                 {
                     ("ScanPageHeader", "ScanPageTitle", "扫描壁纸项目"),
                     ("LibraryPageHeader", "LibraryPageTitle", "输出壁纸库"),
                     ("ProblemsPageHeader", "ProblemsPageTitle", "问题中心")
                 })
        {
            var headerElement = FindNamedElement(document, header);
            var titleElement = FindNamedElement(document, title);
            assert(headerElement is not null
                   && titleElement?.Ancestors().Contains(headerElement) == true
                   && Attribute(titleElement, "Text") == expectedText
                   && Attribute(titleElement, "AutomationProperties.HeadingLevel") == "Level1"
                   && Attribute(titleElement, "Focusable") != "True",
                $"{title} is not the approved non-tabbable Level1 page heading.");
        }

        foreach (var (command, expectedName) in new[]
                 {
                     ("{Binding NavigateScanCommand}", "扫描中心"),
                     ("{Binding NavigateLibraryCommand}", "输出壁纸库"),
                     ("{Binding NavigateProblemsCommand}", "问题中心")
                 })
        {
            var button = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "Button"
                && Attribute(element, "Command") == command);
            assert(button is not null
                   && Attribute(button, "AutomationProperties.Name") == expectedName,
                $"Navigation command {command} has no stable Chinese automation name.");
        }
    }

    private static void VerifyAccessibleTheme(Action<bool, string> assert)
    {
        var theme = XDocument.Load(
            FindRepositoryFile(Path.Combine("Themes", "EndfieldTheme.xaml")),
            LoadOptions.PreserveWhitespace);
        var focusStyle = theme.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Style"
            && Attribute(element, "Key") == "FocusVisual");
        var focusBrushes = focusStyle?.Descendants()
            .Where(element => element.Name.LocalName == "Border")
            .Select(element => Attribute(element, "BorderBrush"))
            .Where(value => value is not null)
            .ToArray() ?? [];
        assert(focusBrushes.Contains("{DynamicResource FocusOuterBrush}", StringComparer.Ordinal)
               && focusBrushes.Contains("{DynamicResource FocusInnerBrush}", StringComparer.Ordinal),
            "FocusVisual does not expose the approved dark outer + signal inner rings.");

        var systemColorReferences = theme.Descendants()
            .SelectMany(element => element.Attributes())
            .Select(attribute => attribute.Value)
            .Where(value => value.Contains("SystemColors.", StringComparison.Ordinal))
            .ToArray();
        foreach (var key in new[]
                 {
                     "WindowColorKey",
                     "WindowTextColorKey",
                     "HighlightColorKey",
                     "HighlightTextColorKey",
                     "GrayTextColorKey"
                 })
        {
            assert(systemColorReferences.Any(value =>
                    value.Contains(key, StringComparison.Ordinal)),
                $"The theme has no DynamicResource reference to SystemColors.{key}.");
        }

        assert(!theme.Descendants().Any(element =>
                element.Name.LocalName == "BeginStoryboard"),
            "High-frequency control templates still contain independent storyboards.");

        var colors = theme.Root?.Elements()
            .Where(element => element.Name.LocalName == "Color")
            .ToDictionary(
                element => Attribute(element, "Key") ?? string.Empty,
                element => element.Value,
                StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var ink = ParseRgb(colors["InkColor"]);
        var paper = ParseRgb(colors["PaperColor"]);
        var white = ParseRgb(colors["PaperElevatedColor"]);
        var signal = ParseRgb(colors["SignalColor"]);
        assert(ContrastRatio(ink, paper) >= 3
               && ContrastRatio(ink, white) >= 3
               && ContrastRatio(ink, signal) >= 3
               && ContrastRatio(signal, ink) >= 3,
            "The dual focus colors do not keep at least one 3:1 ring on dark, paper, white, and signal surfaces.");
    }

    private static (byte Red, byte Green, byte Blue) ParseRgb(string text)
    {
        var value = text.Trim().TrimStart('#');
        if (value.Length == 8)
        {
            value = value[2..];
        }

        if (value.Length != 6)
        {
            throw new InvalidDataException($"Unsupported color token '{text}'.");
        }

        return (
            Convert.ToByte(value[..2], 16),
            Convert.ToByte(value.Substring(2, 2), 16),
            Convert.ToByte(value.Substring(4, 2), 16));
    }

    private static double ContrastRatio(
        (byte Red, byte Green, byte Blue) first,
        (byte Red, byte Green, byte Blue) second)
    {
        static double Luminance((byte Red, byte Green, byte Blue) color)
        {
            static double Linearize(byte channel)
            {
                var normalized = channel / 255d;
                return normalized <= 0.04045
                    ? normalized / 12.92
                    : Math.Pow((normalized + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linearize(color.Red)
                   + 0.7152 * Linearize(color.Green)
                   + 0.0722 * Linearize(color.Blue);
        }

        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static void Invoke(object target, string methodName, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        method.Invoke(target, arguments);
    }

    private static XElement? FindNamedElement(XDocument document, string name)
        => document.Descendants().FirstOrDefault(element => HasName(element, name));

    private static bool HasName(XElement element, string name)
        => string.Equals(
            element.Attribute(XName.Get("Name", XamlNamespace))?.Value,
            name,
            StringComparison.Ordinal);

    private static string? Attribute(XElement element, string localName)
        => element.Attributes().FirstOrDefault(attribute =>
            string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal))?.Value;

    private static string FindRepositoryFile(string relativePath)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file {relativePath}.");
    }
}
