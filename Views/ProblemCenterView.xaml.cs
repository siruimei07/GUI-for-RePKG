using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WallpaperField.Composition;
using WallpaperField.Infrastructure;
using WallpaperField.Models;
using WallpaperField.ViewModels;

namespace WallpaperField.Views;

public sealed partial class ProblemCenterView : UserControl
{
    private readonly HashSet<Guid> _expandedIssueIds = [];
    private bool _restoringExpansion;

    public ProblemCenterView()
    {
        InitializeComponent();
    }

    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    internal Task<bool> PositionSnapshotAsync(
        int requestedIndex,
        Func<bool> isBusy)
        => SnapshotListPositioner.PositionAsync(
            ProblemResultsList,
            requestedIndex,
            isBusy,
            verifyPreview: false);

    private void CopySelectedIssue_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.ProblemCenterSession is not { SelectedIssue: not null } problemCenter)
        {
            return;
        }

        CopyIssueText(problemCenter.CopySelected());
    }

    private void CopyAllIssues_Click(object sender, RoutedEventArgs e)
        => CopyIssueText(ViewModel?.ProblemCenterSession.CopyAll() ?? string.Empty);

    private void ProblemDetails_Loaded(object sender, RoutedEventArgs e)
        => RestoreProblemDetailsState(sender as Expander);

    private void ProblemDetails_DataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
        => RestoreProblemDetailsState(sender as Expander);

    private void ProblemDetails_Expanded(object sender, RoutedEventArgs e)
    {
        if (!_restoringExpansion
            && sender is Expander { DataContext: AppIssue issue })
        {
            PruneExpandedIssueIds();
            _expandedIssueIds.Add(issue.Id);
        }
    }

    private void ProblemDetails_Collapsed(object sender, RoutedEventArgs e)
    {
        if (!_restoringExpansion
            && sender is Expander { DataContext: AppIssue issue })
        {
            _expandedIssueIds.Remove(issue.Id);
        }
    }

    private void RestoreProblemDetailsState(Expander? expander)
    {
        if (expander?.DataContext is not AppIssue issue)
        {
            return;
        }

        var expected = _expandedIssueIds.Contains(issue.Id);
        if (expander.IsExpanded == expected)
        {
            return;
        }

        _restoringExpansion = true;
        try
        {
            expander.IsExpanded = expected;
        }
        finally
        {
            _restoringExpansion = false;
        }
    }

    private void PruneExpandedIssueIds()
    {
        if (_expandedIssueIds.Count < AppIssueStore.MaxVisibleIssues)
        {
            return;
        }

        _expandedIssueIds.IntersectWith(
            ViewModel?.ProblemCenterSession.Issues.Select(issue => issue.Id) ?? []);
    }

    private void CopyIssueText(string text)
    {
        const string context = "problem-center-clipboard";
        try
        {
            Clipboard.SetText(string.IsNullOrWhiteSpace(text) ? "当前没有问题记录。" : text);
            ViewModel?.ProblemCenterSession.Resolve(
                AppIssueSource.Diagnostics,
                "CLIPBOARD_WRITE_FAILED",
                context,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            ViewModel?.ProblemCenterSession.Publish(
            [
                AppIssue.Create(
                    "CLIPBOARD_WRITE_FAILED",
                    AppIssueSeverity.Warning,
                    AppIssueSource.Diagnostics,
                    "无法把问题记录写入剪贴板。",
                    exception.Message,
                    AppDiskFact.NotModified,
                    AppIssueAction.ExportDiagnostics,
                    context)
            ]);
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Write("Problem center requested the local log directory.");
        var logDirectory = Path.GetDirectoryName(AppLog.FilePath);
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            ViewModel?.OpenFolderCommand.Execute(logDirectory);
        }
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel
            || Window.GetWindow(this) is not Window owner)
        {
            return;
        }

        var includePaths = IncludePathContextsCheckBox.IsChecked == true;
        var preview = includePaths
            ? "将导出版本、系统、显示设置、问题记录及完整本地路径。不会导出文件内容、PKG/TEX 数据或预览。"
            : "将导出版本、系统、显示设置和问题记录；完整本地路径默认排除。不会导出文件内容、PKG/TEX 数据或预览。";
        if (MessageBox.Show(
                owner,
                preview,
                "确认诊断字段",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information) != MessageBoxResult.OK)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".json",
            Filter = "JSON 诊断文件 (*.json)|*.json",
            FileName = $"wallpaper-field-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Title = "导出 Wallpaper Field 诊断"
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        try
        {
            await AppComposition.CreateDiagnosticExportService(viewModel).ExportAsync(
                new DiagnosticExportRequest(
                    dialog.FileName,
                    CreateDiagnosticEnvironment(),
                    viewModel.ProblemCenterSession.Issues.ToArray(),
                    includePaths));
            MessageBox.Show(
                owner,
                "诊断文件已导出。",
                "Wallpaper Field",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception)
        {
            MessageBox.Show(
                owner,
                "诊断导出失败，详情已记录到问题中心，可复制后重试。",
                "Wallpaper Field",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not Window owner)
        {
            return;
        }

        var identity = ReadApplicationIdentity();
        MessageBox.Show(
            owner,
            $"Wallpaper Field\n版本 {identity.ApplicationVersion}\n文件版本 {identity.FileVersion}\n本地只读扫描与安全 scene.pkg/TEX 提取工具",
            "关于 Wallpaper Field",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private DiagnosticEnvironment CreateDiagnosticEnvironment()
    {
        var identity = ReadApplicationIdentity();
        var dpi = VisualTreeHelper.GetDpi(this);
        var owner = Window.GetWindow(this) as MainWindow;
        return new DiagnosticEnvironment(
            identity.ApplicationVersion,
            identity.Commit,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            96 * dpi.DpiScaleX,
            SystemParameters.HighContrast,
            owner?.MotionEnabled != true,
            ViewModel?.Density.ToString() ?? DisplayDensity.Comfortable.ToString(),
            identity.FileVersion);
    }

    private static (string ApplicationVersion, string FileVersion, string Commit)
        ReadApplicationIdentity()
    {
        var assembly = typeof(ProblemCenterView).Assembly;
        var applicationVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var fileVersion = assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?
            .Version
            ?? "unknown";
        var separator = applicationVersion.IndexOf('+');
        var commit = separator >= 0 && separator + 1 < applicationVersion.Length
            ? applicationVersion[(separator + 1)..]
            : "unknown";
        return (applicationVersion, fileVersion, commit);
    }
}
