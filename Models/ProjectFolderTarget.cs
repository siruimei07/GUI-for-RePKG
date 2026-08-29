namespace WallpaperField.Models;

public enum ProjectFolderTargetKind
{
    Source,
    Output
}

public sealed record ProjectFolderTarget(
    string ProjectKey,
    string Path,
    ProjectFolderTargetKind Kind)
{
    public string KindLabel => Kind == ProjectFolderTargetKind.Output
        ? "已有输出目录"
        : "项目来源目录";
}

public sealed record ProjectFolderOpenResult(
    ProjectFolderTarget Target,
    bool Succeeded,
    string? FailureCode,
    string? FailureSummary)
{
    public static ProjectFolderOpenResult Success(ProjectFolderTarget target)
        => new(target, true, null, null);

    public static ProjectFolderOpenResult Failure(
        ProjectFolderTarget target,
        string code,
        string summary)
        => new(target, false, code, summary);
}

public sealed class ProjectFolderOpenFailedEventArgs(
    ProjectFolderOpenResult result) : EventArgs
{
    public ProjectFolderOpenResult Result { get; } = result;
}
