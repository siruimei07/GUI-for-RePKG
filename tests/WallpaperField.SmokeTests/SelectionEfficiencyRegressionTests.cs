using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;

internal static class SelectionEfficiencyRegressionTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    internal static async Task RunAsync(Action<bool, string> assert)
    {
        var densityType = VerifyPublicContract(assert);
        if (densityType is null)
        {
            return;
        }

        VerifySettingsCompatibility(densityType, assert);
        await VerifyCombinedFiltersAndSelectionAsync(densityType, assert);
        VerifyDensityXamlContract(assert);
    }

    internal static void VerifyWindowDensity(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        shell.NavigateTo("SCAN");
        shell.ScannedWallpapers.Add(new WallpaperCardViewModel(new WallpaperRecord
        {
            WorkshopId = "density-fixture",
            Title = "Density fixture",
            SourceDirectory = @"C:\density-fixture",
            OutputDirectory = @"C:\density-output",
            HasScenePackage = true,
            ScenePackagePath = @"C:\density-fixture\scene.pkg"
        }));

        shell.Density = DisplayDensity.Comfortable;
        window.UpdateLayout();
        window.Dispatcher.Invoke(
            () => { },
            System.Windows.Threading.DispatcherPriority.DataBind);

        var list = WpfElementFinder.FindByName<ListBox>(window, "ScanResultsList");
        var container = list?.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
        var card = FindVisualDescendant<Button>(container, candidate =>
            candidate.Name == "WallpaperCardButton");
        var technicalHeader = FindVisualDescendant<TextBlock>(container, candidate =>
            candidate.Name == "CardTechnicalHeader");
        var emptyPreviewLabel = FindVisualDescendant<TextBlock>(container, candidate =>
            candidate.Name == "CardPreviewEmptyLabel");
        var toolbar = WpfElementFinder.FindByName<FrameworkElement>(window, "ScanEfficiencyToolbar");
        var comfortableHeight = card?.ActualHeight ?? 0;
        assert(Math.Abs(comfortableHeight - 154d) < 0.5d
               && technicalHeader?.Visibility == Visibility.Visible
               && emptyPreviewLabel?.Visibility == Visibility.Visible
               && toolbar is { ActualHeight: <= 40.5d },
            "The 920-DIP Comfortable card or single-row efficiency toolbar is not using its approved runtime geometry.");

        shell.Density = DisplayDensity.Compact;
        window.UpdateLayout();
        window.Dispatcher.Invoke(
            () => { },
            System.Windows.Threading.DispatcherPriority.DataBind);
        var compactHeight = card?.ActualHeight ?? 0;
        var problemPage = WpfElementFinder.FindByName<WallpaperField.Views.ProblemCenterView>(
            window,
            "ProblemCenterPage");
        var environmentFactory = typeof(WallpaperField.Views.ProblemCenterView).GetMethod(
            "CreateDiagnosticEnvironment",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var environment = environmentFactory?.Invoke(problemPage, null) as DiagnosticEnvironment;
        assert(Math.Abs(compactHeight - 112d) < 0.5d
               && compactHeight <= comfortableHeight - 40d
               && technicalHeader?.Visibility == Visibility.Collapsed
               && emptyPreviewLabel?.Visibility == Visibility.Collapsed
               && environment?.Density == "Compact",
            "Compact density did not reduce the live shared card, hide secondary detail, or reach diagnostics.");

        shell.Density = DisplayDensity.Comfortable;
        shell.ScannedWallpapers.Clear();
        window.UpdateLayout();
    }

    internal static void VerifyToolbarAtCurrentWidth(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        shell.NavigateTo("SCAN");
        window.UpdateLayout();
        window.Dispatcher.Invoke(
            () => { },
            System.Windows.Threading.DispatcherPriority.DataBind);
        var toolbar = WpfElementFinder.FindByName<FrameworkElement>(window, "ScanEfficiencyToolbar");
        assert(toolbar is { IsVisible: true, ActualHeight: <= 40.5d },
            $"The efficiency toolbar wrapped or disappeared at {window.ActualWidth:N0} DIP "
            + $"(height {toolbar?.ActualHeight ?? 0:N1}).");
    }

    private static Type? VerifyPublicContract(Action<bool, string> assert)
    {
        var assembly = typeof(ShellViewModel).Assembly;
        var densityType = assembly.GetType("WallpaperField.Models.DisplayDensity");
        assert(densityType is { IsEnum: true }
               && Enum.GetNames(densityType).SequenceEqual(["Comfortable", "Compact"]),
            "DisplayDensity is missing or does not preserve the approved Comfortable/Compact order.");
        if (densityType is null)
        {
            return null;
        }

        var settingsDensity = typeof(UserSettings).GetProperty("Density");
        var shellProperties = new[]
        {
            "Density",
            "IsCompactDensity",
            "ShowOnlyProcessable",
            "ShowOnlyProblems",
            "HasScanFilters"
        };
        var shellCommands = new[]
        {
            "SelectCurrentMatchesCommand",
            "ClearUnpackSelectionCommand"
        };
        assert(settingsDensity?.PropertyType == densityType
               && shellProperties.All(name => typeof(ShellViewModel).GetProperty(name) is not null)
               && shellCommands.All(name => typeof(ShellViewModel).GetProperty(name) is not null)
               && typeof(WallpaperCardViewModel).GetProperty("HasOpenIssues") is not null,
            "The approved settings/filter/selection/card-issue contract is incomplete.");
        return densityType;
    }

    private static void VerifySettingsCompatibility(
        Type densityType,
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-DensitySettings-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(testRoot, "settings.json");
        Directory.CreateDirectory(testRoot);

        try
        {
            var densityProperty = typeof(UserSettings).GetProperty("Density")!;
            File.WriteAllText(
                settingsPath,
                """
                {
                  "sourcePath": "legacy-source",
                  "outputPath": "legacy-output"
                }
                """,
                new UTF8Encoding(false));
            var store = new UserSettingsStore(settingsPath);
            var legacy = store.Load();
            assert(legacy.SourcePath == "legacy-source"
                   && legacy.OutputPath == "legacy-output"
                   && densityProperty.GetValue(legacy)?.ToString() == "Comfortable",
                "A legacy settings document without density did not default to Comfortable.");

            var compact = new UserSettings
            {
                SourcePath = "source",
                OutputPath = "output"
            };
            densityProperty.SetValue(compact, Enum.Parse(densityType, "Compact"));
            assert(store.Save(compact)
                   && densityProperty.GetValue(store.Load())?.ToString() == "Compact",
                "Compact density did not round-trip through the atomic settings store.");

            File.WriteAllText(settingsPath, "{\"density\":999}", new UTF8Encoding(false));
            assert(densityProperty.GetValue(store.Load())?.ToString() == "Comfortable",
                "An undefined persisted density did not fail soft to Comfortable.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task VerifyCombinedFiltersAndSelectionAsync(
        Type densityType,
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-SelectionEfficiency-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            var shell = new ShellViewModel(
                new FilterFixtureScanService(),
                new EmptyLibraryService(),
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                new EmptyUnpackService())
            {
                SourcePath = sourceRoot,
                OutputPath = outputRoot
            };
            await shell.ScanCommand.ExecuteAsync();
            assert(shell.ScannedWallpapers.Count == 4,
                "The selection fixture did not publish four stable scan cards.");

            var densityProperty = typeof(ShellViewModel).GetProperty("Density")!;
            var compactProperty = typeof(ShellViewModel).GetProperty("IsCompactDensity")!;
            densityProperty.SetValue(shell, Enum.Parse(densityType, "Compact"));
            assert(compactProperty.GetValue(shell) is true,
                "The Compact enum value did not update the bindable density toggle.");
            compactProperty.SetValue(shell, false);
            assert(densityProperty.GetValue(shell)?.ToString() == "Comfortable",
                "The bindable density toggle did not restore Comfortable.");

            var beta = shell.ScannedWallpapers.Single(card => card.WorkshopId == "B");
            var delta = shell.ScannedWallpapers.Single(card => card.WorkshopId == "D");
            var betaContext = Path.GetFullPath(beta.SourceFolder);
            var deltaContext = delta.WorkshopId.ToUpperInvariant();
            shell.PublishIssue(AppIssue.Create(
                "SCAN_ITEM_FAILED",
                AppIssueSeverity.Error,
                AppIssueSource.Scan,
                "Beta failed",
                "fixture",
                AppDiskFact.NotModified,
                AppIssueAction.Retry,
                betaContext,
                pathContext: beta.SourceFolder));
            shell.PublishIssue(AppIssue.Create(
                "UNPACK_ITEM_WARNING",
                AppIssueSeverity.Warning,
                AppIssueSource.Unpack,
                "Delta warning",
                "fixture",
                AppDiskFact.Committed,
                AppIssueAction.OpenOutput,
                deltaContext));

            var hasOpenIssues = typeof(WallpaperCardViewModel).GetProperty("HasOpenIssues")!;
            assert(hasOpenIssues.GetValue(beta) is true
                   && hasOpenIssues.GetValue(delta) is true,
                "Open structured path/WorkshopId issues were not projected to their scan cards.");

            SetBoolean(shell, "ShowOnlyProcessable", true);
            assert(shell.FilteredScannedWallpapers.Select(card => card.WorkshopId)
                       .SequenceEqual(["A", "B"])
                   && shell.FilteredScanCount == 2,
                "The only-processable predicate admitted an unavailable item or lost a ready item.");

            SetBoolean(shell, "ShowOnlyProblems", true);
            assert(shell.FilteredScannedWallpapers.Select(card => card.WorkshopId)
                       .SequenceEqual(["B"])
                   && shell.FilteredScanCount == 1,
                "The processable+problem predicate did not compose as an intersection.");

            SetBoolean(shell, "ShowOnlyProcessable", false);
            assert(shell.FilteredScannedWallpapers.Select(card => card.WorkshopId)
                       .SequenceEqual(["B", "D"]),
                "The only-problems predicate did not consume structured card issue state.");

            shell.ScanSearchText = " beta ";
            assert(shell.FilteredScanCount == 1
                   && shell.FilteredScannedWallpapers.Single().WorkshopId == "B",
                "Title search did not compose with the problem predicate.");

            SetBoolean(shell, "ShowOnlyProblems", false);
            shell.ScanSearchText = "Alpha";
            var selectMatches = GetCommand(shell, "SelectCurrentMatchesCommand");
            var clearSelection = GetCommand(shell, "ClearUnpackSelectionCommand");
            assert(selectMatches.CanExecute(null),
                "Select-current-matches was disabled with one unselected processable match.");
            selectMatches.Execute(null);
            var alpha = shell.ScannedWallpapers.Single(card => card.WorkshopId == "A");
            assert(alpha.IsSelectedForUnpack && shell.SelectedUnpackCount == 1,
                "Selecting the Alpha match did not select exactly one processable item.");

            shell.ScanSearchText = "Beta";
            selectMatches.Execute(null);
            assert(alpha.IsSelectedForUnpack
                   && beta.IsSelectedForUnpack
                   && shell.SelectedUnpackCount == 2
                   && shell.SelectionSummaryText == "已选 2 · 当前匹配 1",
                "Selecting a new filtered snapshot cleared or modified the hidden Alpha selection.");
            assert(clearSelection.CanExecute(null),
                "Clear-selection was disabled while hidden selections existed.");
            clearSelection.Execute(null);
            assert(shell.SelectedUnpackCount == 0
                   && shell.ScannedWallpapers.All(card => !card.IsSelectedForUnpack),
                "Clear-selection did not clear both visible and hidden selections.");

            shell.ScanSearchText = "Gamma";
            selectMatches.Execute(null);
            assert(shell.SelectedUnpackCount == 0,
                "Select-current-matches selected a non-processable card.");

            shell.ScanSearchText = string.Empty;
            SetBoolean(shell, "ShowOnlyProblems", true);
            shell.ResolveIssues(AppIssueSource.Scan, "SCAN_ITEM_FAILED", betaContext);
            assert(hasOpenIssues.GetValue(beta) is false
                   && shell.FilteredScannedWallpapers.Select(card => card.WorkshopId)
                       .SequenceEqual(["D"]),
                "Resolving the exact scan issue did not remove Beta from the problem predicate.");
            shell.ResolveIssues(AppIssueSource.Unpack, "UNPACK_ITEM_WARNING", deltaContext);
            assert(hasOpenIssues.GetValue(delta) is false
                   && shell.FilteredScanCount == 0
                   && (bool)typeof(ShellViewModel).GetProperty("HasScanFilters")!.GetValue(shell)!
                   && shell.ScanEmptyTitle == "未找到匹配壁纸",
                "Resolving all card issues did not produce an accurate active-filter empty state.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void VerifyDensityXamlContract(Action<bool, string> assert)
    {
        var domainTheme = XDocument.Load(
            FindRepositoryFile(Path.Combine("Themes", "DomainComponents.xaml")));
        var scanPage = XDocument.Load(
            FindRepositoryFile(Path.Combine("Views", "ScanPageView.xaml")));
        foreach (var name in new[]
                 {
                     "OnlyProcessableFilter",
                     "OnlyProblemsFilter",
                     "SelectCurrentMatchesButton",
                     "ClearUnpackSelectionButton",
                     "CompactDensityToggle",
                     "SelectionSummaryText"
                 })
        {
            assert(FindNamedElement(scanPage, name) is not null,
                $"The scan efficiency surface is missing {name}.");
        }

        assert(scanPage.Descendants().Any(element =>
                   element.Name.LocalName == "Condition"
                   && (Attribute(element, "Binding")?.Contains(
                       "ScanSession.IsSelectionWritable",
                       StringComparison.Ordinal) ?? false)
                   && Attribute(element, "Value") == "True"),
            "The Scan selection surface does not project the session-owned foreground-I/O gate.");

        var densityTrigger = domainTheme.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "DataTrigger"
            && (Attribute(element, "Binding")?.Contains("Density", StringComparison.Ordinal) ?? false)
            && Attribute(element, "Value") == "Compact");
        assert(densityTrigger is not null,
            "The shared wallpaper card template has no Compact density trigger.");

        var theme = XDocument.Load(
            FindRepositoryFile(Path.Combine("Themes", "Tokens.xaml")));
        foreach (var key in new[]
                 {
                     "ComfortableCardHeight",
                     "CompactCardHeight",
                     "ComfortableCardPreviewWidth",
                     "CompactCardPreviewWidth"
                 })
        {
            assert(theme.Descendants().Any(element => Attribute(element, "Key") == key),
                $"The theme is missing density token {key}.");
        }

        var appSource = File.ReadAllText(FindRepositoryFile("App.xaml.cs"));
        var windowSource = File.ReadAllText(FindRepositoryFile("MainWindow.xaml.cs"));
        assert(appSource.Contains("Density = savedSettings.Density", StringComparison.Ordinal)
               && windowSource.Contains("Density = viewModel.Density", StringComparison.Ordinal),
            "App startup/closing does not restore and persist the density preference.");
    }

    private static void SetBoolean(ShellViewModel shell, string propertyName, bool value)
        => typeof(ShellViewModel).GetProperty(propertyName)!.SetValue(shell, value);

    private static ICommand GetCommand(ShellViewModel shell, string propertyName)
        => (ICommand)(typeof(ShellViewModel).GetProperty(propertyName)!.GetValue(shell)
            ?? throw new InvalidOperationException($"{propertyName} returned null."));

    private static XElement? FindNamedElement(XDocument document, string name)
        => document.Descendants().FirstOrDefault(element =>
            string.Equals(
                element.Attribute(XName.Get("Name", XamlNamespace))?.Value,
                name,
                StringComparison.Ordinal));

    private static T? FindVisualDescendant<T>(
        DependencyObject? root,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            if (FindVisualDescendant(child, predicate) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

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

    private sealed class FilterFixtureScanService : IWallpaperScanService
    {
        public Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ScanResult
            {
                Items =
                [
                    CreateRecord(request, "A", "Alpha ready", true, now),
                    CreateRecord(request, "B", "Beta issue", true, now),
                    CreateRecord(request, "C", "Gamma static", false, now),
                    CreateRecord(request, "D", "Delta issue", false, now)
                ],
                StartedAtUtc = now,
                CompletedAtUtc = now
            });
        }

        private static WallpaperRecord CreateRecord(
            WallpaperScanRequest request,
            string id,
            string title,
            bool processable,
            DateTimeOffset timestamp)
            => new()
            {
                WorkshopId = id,
                Title = title,
                SourceDirectory = Path.Combine(request.SourceDirectory, id),
                OutputDirectory = Path.Combine(request.OutputDirectory, id),
                HasScenePackage = processable,
                ScenePackagePath = processable
                    ? Path.Combine(request.SourceDirectory, id, "scene.pkg")
                    : null,
                ScannedAtUtc = timestamp
            };
    }

    private sealed class EmptyLibraryService : IWallpaperLibraryService
    {
        public Task<WallpaperLibraryResult> LoadAsync(
            string outputDirectory,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WallpaperLibraryResult());
    }

    private sealed class EmptyUnpackService : IWallpaperUnpackService
    {
        public Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WallpaperUnpackResult());
    }

    private sealed class NullFolderPickerService : IFolderPickerService
    {
        public string? PickFolder(string title, string? initialPath = null) => null;
    }

    private sealed class NullSystemFolderService : ISystemFolderService
    {
        public void OpenFolder(string folderPath)
        {
        }
    }
}
