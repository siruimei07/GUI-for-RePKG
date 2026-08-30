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
        VerifyLayeredThemeContract(assert);
        VerifyAccessibleTheme(assert);
    }

    internal static void VerifyWindow(
        WallpaperField.MainWindow window,
        Action<bool, string> assert)
    {
        foreach (var titleName in new[]
                 {
                     "ScanPageTitle",
                     "BrowsePageTitle",
                     "LibraryPageTitle",
                     "ProblemsPageTitle"
                 })
        {
            var title = WpfElementFinder.FindByName<TextBlock>(window, titleName);
            assert(title is not null
                   && AutomationProperties.GetHeadingLevel(title) == AutomationHeadingLevel.Level1
                   && !title.Focusable,
                $"{titleName} is not a non-tabbable Level1 heading at runtime.");
        }

        foreach (var (name, expected) in new[]
                 {
                     ("ScanNavButton", "扫描中心"),
                     ("BrowseNavButton", "项目浏览"),
                     ("LibraryNavButton", "输出壁纸库"),
                     ("ProblemNavButton", "问题中心"),
                     ("RefreshLibraryButton", "刷新输出壁纸库")
                 })
        {
            var element = WpfElementFinder.FindByName<FrameworkElement>(window, name);
            assert(element is not null
                   && AutomationProperties.GetName(element) == expected,
                $"{name} lost its stable Chinese automation name.");
        }

        VerifyKeyboardAccess(window, assert);
        VerifyLayeredThemeResourcesAtRuntime(assert);

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
                     "ScanPage",
                     "BrowsePage",
                     "LibraryPage",
                     "ProblemCenterPage"
                 })
        {
            var target = WpfElementFinder.FindByName<DependencyObject>(window, name);
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
                     ("BROWSE", new[]
                     {
                         "BrowseNavButton",
                         "BrowseScanCenterButton"
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
                var control = WpfElementFinder.FindByName<Control>(window, name);
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
        var mainDocument = XDocument.Load(
            FindRepositoryFile("MainWindow.xaml"),
            LoadOptions.PreserveWhitespace);
        foreach (var (document, header, title, expectedText) in new[]
                 {
                     (XDocument.Load(FindRepositoryFile(Path.Combine("Views", "ScanPageView.xaml"))), "ScanPageHeader", "ScanPageTitle", "扫描壁纸项目"),
                     (XDocument.Load(FindRepositoryFile(Path.Combine("Views", "BrowsePageView.xaml"))), "BrowsePageHeader", "BrowsePageTitle", "项目浏览"),
                     (XDocument.Load(FindRepositoryFile(Path.Combine("Views", "LibraryPageView.xaml"))), "LibraryPageHeader", "LibraryPageTitle", "输出壁纸库"),
                     (XDocument.Load(FindRepositoryFile(Path.Combine("Views", "ProblemCenterView.xaml"))), "ProblemsPageHeader", "ProblemsPageTitle", "问题中心")
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
                     ("{Binding NavigateBrowseCommand}", "项目浏览"),
                     ("{Binding NavigateLibraryCommand}", "输出壁纸库"),
                     ("{Binding NavigateProblemsCommand}", "问题中心")
                 })
        {
            var button = mainDocument.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "Button"
                && Attribute(element, "Command") == command);
            assert(button is not null
                   && Attribute(button, "AutomationProperties.Name") == expectedName,
                $"Navigation command {command} has no stable Chinese automation name.");
        }
    }

    private static void VerifyAccessibleTheme(Action<bool, string> assert)
    {
        var themes = LoadThemeLayerDocuments();
        var tokens = themes[0];
        var accessibilityMotion = themes[1];
        var focusStyle = accessibilityMotion.Descendants().FirstOrDefault(element =>
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

        var systemColorReferences = accessibilityMotion.Descendants()
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

        assert(!themes.SelectMany(theme => theme.Descendants()).Any(element =>
                element.Name.LocalName == "BeginStoryboard"),
            "High-frequency control templates still contain independent storyboards.");

        var colors = tokens.Root?.Elements()
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

    private static void VerifyLayeredThemeContract(Action<bool, string> assert)
    {
        var themePath = FindRepositoryFile(Path.Combine("Themes", "EndfieldTheme.xaml"));
        var themeDirectory = Path.GetDirectoryName(themePath)
            ?? throw new InvalidDataException("The theme path has no parent directory.");
        var layerFiles = new[]
        {
            "Tokens.xaml",
            "AccessibilityMotion.xaml",
            "BaseControls.xaml",
            "DomainComponents.xaml"
        };
        foreach (var layerFile in layerFiles)
        {
            assert(File.Exists(Path.Combine(themeDirectory, layerFile)),
                $"Theme layer Themes/{layerFile} is missing.");
        }

        var app = XDocument.Load(
            FindRepositoryFile("App.xaml"),
            LoadOptions.PreserveWhitespace);
        var compatibilityTheme = XDocument.Load(themePath, LoadOptions.PreserveWhitespace);
        var expectedAppSources = layerFiles.Select(file => $"Themes/{file}").ToArray();
        assert(ReadMergedDictionarySources(app).SequenceEqual(expectedAppSources),
            "App.xaml does not merge the four theme layers in the approved order.");
        assert(ReadMergedDictionarySources(compatibilityTheme).SequenceEqual(layerFiles),
            "EndfieldTheme.xaml is not an ordered compatibility-only theme entry.");
        assert(!compatibilityTheme.Root!.Elements().Any(element =>
                element.Name.LocalName != "ResourceDictionary.MergedDictionaries"),
            "EndfieldTheme.xaml still owns resources outside its compatibility merge list.");

        var themes = LoadThemeLayerDocuments();
        var expectedOwnership = new Dictionary<int, string[]>
        {
            [0] =
            [
                "InkColor",
                "PaperColor",
                "SignalColor",
                "InkBrush",
                "PaperBrush",
                "SignalBrush",
                "FocusOuterBrush",
                "FocusInnerBrush",
                "EngineeringGridBrush",
                "ComfortableCardHeight"
            ],
            [1] =
            [
                "HighContrastWindowBrush",
                "HighContrastWindowTextBrush",
                "HighContrastHighlightBrush",
                "HighContrastHighlightTextBrush",
                "HighContrastDisabledBrush",
                "FocusVisual"
            ],
            [2] =
            [
                "BooleanToVisibilityConverter",
                "RoundedButtonTemplate",
                "EndfieldButtonBase",
                "PrimaryButton",
                "FilterToggleButton",
                "SearchTextBox",
                "EndfieldScrollThumb",
                "VerticalScrollBarTemplate"
            ],
            [3] =
            [
                "NavButton",
                "CardButton",
                "ProblemFilterComboBox",
                "WallpaperCardTemplate",
                "LightPanel",
                "StatusPill",
                "TechnicalLabel",
                "SectionTitle"
            ]
        };
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var layerIndex = 0; layerIndex < themes.Length; layerIndex++)
        {
            var layer = themes[layerIndex];
            var layerKeys = ReadTopLevelResourceIdentities(layer).ToArray();
            foreach (var expectedKey in expectedOwnership[layerIndex])
            {
                assert(layerKeys.Contains(expectedKey, StringComparer.Ordinal),
                    $"Theme layer {layerFiles[layerIndex]} does not own {expectedKey}.");
            }

            foreach (var key in layerKeys)
            {
                assert(owners.TryAdd(key, layerFiles[layerIndex]),
                    $"Theme resource {key} is duplicated by {owners.GetValueOrDefault(key)} and {layerFiles[layerIndex]}.");
            }
        }

        for (var layerIndex = 0; layerIndex < themes.Length; layerIndex++)
        {
            foreach (var reference in themes[layerIndex].Descendants()
                         .SelectMany(element => element.Attributes())
                         .Select(attribute => attribute.Value)
                         .Where(value => value.StartsWith("{StaticResource ", StringComparison.Ordinal)))
            {
                var referencedKey = reference[16..^1];
                var identity = referencedKey.StartsWith("{x:Type ", StringComparison.Ordinal)
                    ? $"implicit-style:{referencedKey}"
                    : referencedKey;
                assert(owners.TryGetValue(identity, out var owner)
                       && string.Equals(owner, layerFiles[layerIndex], StringComparison.Ordinal),
                    $"Theme layer {layerFiles[layerIndex]} captures cross-layer StaticResource {referencedKey} from {owner ?? "an unknown owner"}.");
            }
        }

        foreach (var theme in themes.Skip(1))
        {
            var staticBrushReference = theme.Descendants()
                .SelectMany(element => element.Attributes())
                .Select(attribute => attribute.Value)
                .FirstOrDefault(value =>
                    value.StartsWith("{StaticResource ", StringComparison.Ordinal)
                    && value.Contains("Brush}", StringComparison.Ordinal));
            assert(staticBrushReference is null,
                $"An overridable palette brush is statically captured by {staticBrushReference}.");
        }

        var localResourceDocuments = new[]
        {
            "MainWindow.xaml",
            Path.Combine("Views", "ScanPageView.xaml"),
            Path.Combine("Views", "LibraryPageView.xaml"),
            Path.Combine("Views", "ProblemCenterView.xaml")
        }.Select(path => XDocument.Load(
            FindRepositoryFile(path),
            LoadOptions.PreserveWhitespace));
        foreach (var localKey in new[]
                 {
                     "BooleanToVisibilityConverter",
                     "ProblemFilterComboBoxItem",
                     "ProblemFilterComboBox",
                     "EngineeringGridBrush",
                     "WallpaperCardTemplate"
                 })
        {
            assert(!localResourceDocuments.Any(document => document.Descendants().Any(element =>
                    Attribute(element, "Key") == localKey)),
                $"Theme resource {localKey} still has a window/page-local owner.");
        }
    }

    private static void VerifyLayeredThemeResourcesAtRuntime(Action<bool, string> assert)
    {
        var resources = Application.Current.Resources;
        var mergedNames = ReadRuntimeMergedDictionaryNames(resources);
        assert(mergedNames.SequenceEqual(new[]
               {
                   "Tokens.xaml",
                   "AccessibilityMotion.xaml",
                   "BaseControls.xaml",
                   "DomainComponents.xaml"
               }),
            "The live application did not load the four theme layers in order.");
        foreach (var key in new[]
                 {
                     "InkBrush",
                     "FocusVisual",
                     "PrimaryButton",
                     "NavButton",
                     "WallpaperCardTemplate",
                     "ProblemFilterComboBox"
                 })
        {
            assert(resources.Contains(key),
                $"The live application could not resolve theme resource {key}.");
        }

        var compatibilityTheme = new ResourceDictionary
        {
            Source = new Uri(
                "/WallpaperField;component/Themes/EndfieldTheme.xaml",
                UriKind.Relative)
        };
        assert(ReadRuntimeMergedDictionaryNames(compatibilityTheme).SequenceEqual(new[]
               {
                   "Tokens.xaml",
                   "AccessibilityMotion.xaml",
                   "BaseControls.xaml",
                   "DomainComponents.xaml"
               }),
            "The compatibility theme did not load its four runtime layers in order.");
        foreach (var key in new[] { "InkBrush", "FocusVisual", "PrimaryButton", "WallpaperCardTemplate" })
        {
            assert(compatibilityTheme.Contains(key),
                $"The compatibility theme could not resolve theme resource {key}.");
        }
    }

    private static string[] ReadRuntimeMergedDictionaryNames(ResourceDictionary resources)
        => resources.MergedDictionaries
            .Select(dictionary => Path.GetFileName(dictionary.Source?.OriginalString) ?? string.Empty)
            .ToArray();

    private static XDocument[] LoadThemeLayerDocuments()
        => new[]
        {
            "Tokens.xaml",
            "AccessibilityMotion.xaml",
            "BaseControls.xaml",
            "DomainComponents.xaml"
        }.Select(file => XDocument.Load(
            FindRepositoryFile(Path.Combine("Themes", file)),
            LoadOptions.PreserveWhitespace)).ToArray();

    private static IEnumerable<string> ReadMergedDictionarySources(XDocument document)
        => document.Descendants()
            .Where(element => element.Name.LocalName == "ResourceDictionary")
            .Select(element => Attribute(element, "Source"))
            .Where(source => source is not null)
            .Select(source => source!.Replace('\\', '/'));

    private static IEnumerable<string> ReadTopLevelResourceIdentities(XDocument document)
    {
        foreach (var element in document.Root?.Elements() ?? [])
        {
            var key = Attribute(element, "Key");
            if (!string.IsNullOrWhiteSpace(key))
            {
                yield return key;
                continue;
            }

            if (element.Name.LocalName == "Style"
                && Attribute(element, "TargetType") is { } targetType)
            {
                yield return $"implicit-style:{targetType}";
            }
        }
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
