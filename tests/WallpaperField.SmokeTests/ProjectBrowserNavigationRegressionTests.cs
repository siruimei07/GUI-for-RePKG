using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Xml.Linq;
using WallpaperField.Composition;
using WallpaperField.Infrastructure;
using WallpaperField.ViewModels;

internal static class ProjectBrowserNavigationRegressionTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    internal static void Run(Action<bool, string> assert)
    {
        VerifyParserAndShellContract(assert);
        VerifyCompositionIdentity(assert);
        VerifyFourPageXamlContract(assert);
        VerifyBrowseEmptyStateContract(assert);
    }

    private static void VerifyParserAndShellContract(Action<bool, string> assert)
    {
        foreach (var (alias, expected) in new[]
                 {
                     ("scan", "scan"), ("01", "scan"),
                     ("browse", "browse"), ("02", "browse"),
                     ("library", "library"), ("03", "library"),
                     ("problems", "problems"), ("04", "problems")
                 })
        {
            var parsed = StartupOptions.Parse(["--page", alias]);
            assert(parsed.Issues.Count == 0 && parsed.Options.Page == expected,
                $"Startup page alias '{alias}' did not map to '{expected}'.");
        }

        var shellType = typeof(ShellViewModel);
        assert(shellType.GetProperty("BrowsePageViewModel")?.PropertyType
                   == typeof(BrowsePageViewModel)
               && shellType.GetProperty("NavigateBrowseCommand") is not null
               && shellType.GetProperty("IsBrowsePage") is not null
               && typeof(IDisposable).IsAssignableFrom(shellType),
            "Shell is missing the single Browse VM and page-only navigation surface.");
        foreach (var forbidden in new[] { "BrowseSearchText", "BrowseSort", "BrowseRows" })
        {
            assert(shellType.GetProperty(forbidden) is null,
                $"Shell must not forward Browse collection state through {forbidden}.");
        }
    }

    private static void VerifyCompositionIdentity(Action<bool, string> assert)
    {
        var shell = AppComposition.CreateShellViewModel();
        var browse = AppComposition.CreateBrowsePageViewModel(shell);
        var shellBrowse = typeof(ShellViewModel).GetProperty("BrowsePageViewModel")?.GetValue(shell);
        assert(ReferenceEquals(browse, shellBrowse),
            "AppComposition did not create exactly one Browse VM shared by the Shell.");
        shell.Dispose();
    }

    private static void VerifyFourPageXamlContract(Action<bool, string> assert)
    {
        var assembly = typeof(WallpaperField.MainWindow).Assembly;
        var browseType = assembly.GetType("WallpaperField.Views.BrowsePageView");
        assert(browseType is { IsPublic: true, IsSealed: true }
               && typeof(UserControl).IsAssignableFrom(browseType),
            "BrowsePageView is not a public sealed UserControl route boundary.");

        var document = XDocument.Load(FindRepositoryFile("MainWindow.xaml"));
        var rail = FindNamedElement(document, "NavigationRail");
        var operationLabel = FindNamedElement(document, "NavSectionLabel");
        assert(rail is not null
               && operationLabel?.Ancestors().Contains(rail) == true
               && Attribute(operationLabel, "Text")?.EndsWith("04", StringComparison.Ordinal) == true
               && ((string?)operationLabel.Attribute("AutomationProperties.Name"))
                   ?.Contains("4 个页面", StringComparison.Ordinal) == true,
            "Main rail does not announce the four-operation route count.");

        var expected = new[]
        {
            ("NavigateScanCommand", "扫描中心", "扫描中心"),
            ("NavigateBrowseCommand", "项目浏览", "项目浏览"),
            ("NavigateLibraryCommand", "输出库", "输出壁纸库"),
            ("NavigateProblemsCommand", "问题中心", "问题中心")
        };
        var navigationButtons = rail?.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .ToArray() ?? [];
        assert(navigationButtons.Length == expected.Length,
            "Main rail does not expose exactly four page actions.");
        foreach (var (command, label, automationName) in expected)
        {
            var matches = navigationButtons.Where(element =>
                Attribute(element, "Command") == $"{{Binding {command}}}").ToArray();
            var button = matches.Length == 0 ? null : matches[0];
            assert(matches.Length == 1
                   && button is not null
                   && (string?)button.Attribute("AutomationProperties.Name") == automationName
                   && button.Descendants().Any(element =>
                       element.Name.LocalName == "TextBlock"
                       && Attribute(element, "Text") == label),
                $"Rail route {command} must have one action with its visible label and automation name.");
        }

        var host = FindNamedElement(document, "BrowsePage");
        assert(host?.Name.LocalName == "BrowsePageView",
            "MainWindow does not host BrowsePageView.");

        var browseDocument = XDocument.Load(
            FindRepositoryFile(Path.Combine("Views", "BrowsePageView.xaml")));
        var browseRoot = FindNamedElement(browseDocument, "BrowseView");
        assert(browseRoot?.Descendants().Any(element =>
                   element.Name.LocalName == "DataTrigger"
                   && Attribute(element, "Binding") == "{Binding IsBrowsePage}"
                   && Attribute(element, "Value") == "True") == true,
            "BrowsePageView does not own its IsBrowsePage visibility route.");
    }

    private static void VerifyBrowseEmptyStateContract(Action<bool, string> assert)
    {
        var path = FindRepositoryFile(Path.Combine("Views", "BrowsePageView.xaml"));
        assert(File.Exists(path), "BrowsePageView.xaml is missing.");
        if (!File.Exists(path))
        {
            return;
        }

        var document = XDocument.Load(path);
        var title = FindNamedElement(document, "BrowsePageTitle");
        var cta = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Button"
            && Attribute(element, "Command") == "{Binding NavigateScanCommand}");
        var allText = string.Join(" ", document.Descendants()
            .SelectMany(element => element.Attributes())
            .Select(attribute => attribute.Value));
        assert(title is not null
               && title.Attributes().Any(attribute => attribute.Value == "Level1")
               && cta is not null
               && allText.Contains("前往扫描中心", StringComparison.Ordinal)
               && allText.Contains("BrowsePageViewModel.EmptyTitle", StringComparison.Ordinal)
               && allText.Contains("BrowsePageViewModel.EmptyDescription", StringComparison.Ordinal)
               && allText.Contains("BrowsePageViewModel.CurrentSourcePath", StringComparison.Ordinal)
               && allText.Contains("BrowsePageViewModel.SnapshotSourcePath", StringComparison.Ordinal)
               && allText.Contains("BrowsePageViewModel.SnapshotSourceStatusText", StringComparison.Ordinal),
            "Browse empty route lacks its heading, two-state copy, source status, or Scan CTA.");
        var emptyState = FindNamedElement(document, "BrowseEmptyState");
        var readyState = FindNamedElement(document, "BrowseReadyState");
        assert(emptyState is not null
               && readyState?.Descendants().Any(element =>
                   element.Name.LocalName == "DataTrigger"
                   && Attribute(element, "Binding")
                       == "{Binding BrowsePageViewModel.HasVisibleProjects}"
                   && Attribute(element, "Value") == "True") == true,
            "Task 5 must preserve the Task 3 empty route while gating the real browser workspace on visible projects.");
    }

    private static XElement? FindNamedElement(XDocument document, string name)
        => document.Descendants().FirstOrDefault(element =>
            string.Equals((string?)element.Attribute(XName.Get("Name", XamlNamespace)), name,
                StringComparison.Ordinal));

    private static string? Attribute(XElement element, string name)
        => name.Contains('.', StringComparison.Ordinal)
            ? (string?)element.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName == name[(name.IndexOf('.') + 1)..])
            : (string?)element.Attribute(name);

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.GetFullPath(relativePath);
    }
}
