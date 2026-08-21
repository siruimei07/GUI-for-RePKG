# Wallpaper Field v1.2.2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 在补丁兼容边界内完成 WP5–WP11，把源码、用户体验、文档和本地发布候选统一为 v1.2.2，并在固定起点审阅通过后合并回 `main`。

**Architecture:** 先在现有 Shell/service 边界上以测试固定并修复 WP5–WP8 行为，通过稳定性门禁后，再把唯一状态所有者提取为 `TaskLifecycleCoordinator` 与四个 session，最后拆三个深页面和四层主题资源。安全 PKG/TEX、完整规划、staging/rollback 与只读扫描边界保持不动；结构移动和行为修改分开提交。

**Tech Stack:** C# 13、.NET 10、WPF/XAML、PowerShell、现有 console SmokeTests harness、Git。

**Spec:** `docs/superpowers/specs/2026-08-21-wallpaper-field-v1-2-2-roadmap-design.md`

## Global Constraints

- 固定审阅起点为 `61a129c855c17b745c048b6841acb96a449eadc1`；实施分支为 `codex/v1.2.2-roadmap`。
- 不暂存、提交、覆盖或删除用户已有 `GUI_for_RePKG.exe` 与根 `AGENTS.md`。
- `temp/` 只存运行证据、截图、基准、审计和候选物，不加入 Git，也不得进入 SDK 默认 glob。
- 所有产品 PKG 读取继续只经第一方 adapter 和 `SafePackageReader`；不把上游 eager reader/writer 带回产品路径。
- 每个行为缺陷先新增会失败的回归断言，再做最小实现；结构迁移只能在 WP5–WP8 稳定性门禁通过后开始。
- 每个任务提交前运行该任务最小验证、完整 SmokeTests、`git diff --check`，并检查用户两项已有状态未漂移。
- `dotnet test WallpaperField.slnx` 当前执行 0 项，只记录为发现能力限制；真实测试门禁是 console SmokeTests 的正数 assertions 与 exit code。
- UI 自动化证据不能冒充 High Contrast、屏幕阅读器和 DPI 人工验收；无法实测的项目在最终报告中明确列为未验证。
- 本计划在当前线程采用 inline execution；未经用户明确授权不分派子代理。

## Verification Conventions

从仓库根目录运行：

```powershell
dotnet restore WallpaperField.slnx --configfile NuGet.Config
dotnet build WallpaperField.slnx -c Release --no-restore
dotnet run --project tests/WallpaperField.SmokeTests/WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore
dotnet test WallpaperField.slnx -c Release --no-build --no-restore
git diff --check
git status --short --branch
```

Smoke 的成功摘要必须满足 `tests=1`、`assertions>0`、`passed=1`、`failed=0`。每个 RED 步骤只接受新增断言所描述的预期失败；编译错误、fixture 错误或其他回归不算有效 RED。

---

### Task 1: 固定 WP5–WP8 行为特征与内部测试缝隙

**Files:**

- Create: `Properties/AssemblyInfo.cs`
- Create: `tests/WallpaperField.SmokeTests/RoadmapBehaviorRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

**Public/test seam:**

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WallpaperField.SmokeTests")]
```

- [ ] 在 `RoadmapBehaviorRegressionTests.RunAsync` 中固定当前合法 CLI、扫描只读、过滤不丢隐藏选择、输出根 identity 禁用解包、只处理选中项、图库递归 metadata、虚拟化资源存在和发布版本合同。
- [ ] 把该入口注册到 `Program.cs`，运行 SmokeTests，确认基线断言全部通过且 assertion 数高于 136。
- [ ] 记录新增 assertion 数和运行时间到任务 `progress.md`；执行 `git diff --check`。
- [ ] Commit: `test: characterize v1.2.2 roadmap behavior`

### Task 2: WP5 可等待命令、统一前台状态与可见取消

**Files:**

- Create: `Models/TaskLifecycleModels.cs`
- Create: `tests/WallpaperField.SmokeTests/TaskLifecycleRegressionTests.cs`
- Modify: `ViewModels/AsyncRelayCommand.cs`
- Modify: `ViewModels/ShellViewModel.cs`
- Modify: `MainWindow.xaml`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

**Exact lifecycle contract:**

```csharp
public enum TaskLifecycleState
{
    Idle,
    Running,
    CancellationRequested,
    CommitCritical,
    Succeeded,
    Failed,
    Cancelled
}

public enum ForegroundOperationKind
{
    Scan,
    Unpack,
    LibraryRefresh
}

public sealed record TaskLifecycleSnapshot(
    Guid? OperationId,
    ForegroundOperationKind? OperationKind,
    TaskLifecycleState State,
    bool CancellationPending,
    DateTimeOffset ChangedAtUtc);
```

`AsyncRelayCommand` 增加 `Task ExecutionTask { get; }`、`bool TryCancel()` 与 `Task WaitForCompletionAsync()`；现有 `void Cancel()` 保留并转发到 `TryCancel()`，避免破坏调用者。

- [ ] 写失败测试：三个操作不能并发占用前台槽；重复取消幂等；三个操作都有可执行取消命令；导航和问题查看在运行中仍可用；`WaitForPendingWorkAsync` 等到 command `finally`。
- [ ] 运行 SmokeTests，确认失败只来自缺失生命周期/取消契约。
- [ ] 在 Shell 增加单一 operation ID/kind/state 投影、`CancelUnpackCommand`、`CancelLibraryRefreshCommand`、`CancelPendingWork()` 和 `Task<bool> WaitForPendingWorkAsync(TimeSpan timeout)`；运行冲突命令只禁用，不清页面。
- [ ] 在 XAML 为扫描、解包、刷新各放置常驻中文取消入口，并设置稳定 `AutomationProperties.Name`；绑定 `CanBeCanceled` 与 state 文案。
- [ ] 运行定向 Smoke、完整 Smoke、Release build 和 `git diff --check`。
- [ ] Commit: `feat: add cancellable foreground task lifecycle`

### Task 3: WP5 扫描快照、identity 与真实当前项

**Files:**

- Create: `tests/WallpaperField.SmokeTests/ScanLifecycleRegressionTests.cs`
- Modify: `Models/WallpaperScanModels.cs`
- Modify: `Services/WallpaperScanService.cs`
- Modify: `ViewModels/ShellViewModel.cs`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

**Snapshot identity:**

```csharp
public sealed record ScanSnapshotIdentity(
    string SourceDirectory,
    string OutputDirectory,
    DateTimeOffset CompletedAtUtc);
```

- [ ] 写失败测试：已有扫描列表在下一次扫描失败/取消时保持相同对象序列；当前输入 identity 变化后旧列表可浏览但解包禁用；失败目录的进度标题不回退到上一成功 title。
- [ ] 运行 SmokeTests，观察旧快照被 `ResetScanState()` 清空及错误 title 断言失败。
- [ ] 移除扫描开始前的列表清空；把新结果先保存在局部变量，服务正常返回后一次替换并更新 identity；失败/取消只更新 lifecycle 与问题。
- [ ] 在 `WallpaperScanService` 的每项 progress 使用正在处理目录解析出的 title/folder，不使用 `items.LastOrDefault()`。
- [ ] 运行定向 Smoke、完整 Smoke、Release build；确认扫描前后 source 树 hash 不变。
- [ ] Commit: `fix: preserve scan snapshots across failure and cancellation`

### Task 4: WP5 解包逐项结果、提交事实与真实进度

**Files:**

- Create: `tests/WallpaperField.SmokeTests/UnpackLifecycleRegressionTests.cs`
- Modify: `Models/WallpaperUnpackModels.cs`
- Modify: `Services/RePkgWallpaperUnpackService.cs`
- Modify: `Services/PackageExtractionPlanner.cs`
- Modify: `Services/TransactionalDirectoryCommitter.cs`
- Modify: `ViewModels/ShellViewModel.cs`
- Modify: `MainWindow.xaml`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

**Append-only result/progress surface:**

```csharp
public enum WallpaperUnpackOutcome { Succeeded, Skipped, Failed, Cancelled }
public enum WallpaperWorkUnit { Items, Entries, Bytes }
public enum WallpaperUnpackStage { Planning, Extracting, Converting, Committing, RollingBack, Completed }

public sealed record WallpaperUnpackItemResult
{
    public string WorkshopId { get; init; } = string.Empty;
    public string OutputTarget { get; init; } = string.Empty;
    public WallpaperUnpackOutcome Outcome { get; init; }
    public WallpaperItemCommitState CommitState { get; init; }
    public long CompletedWork { get; init; }
    public WallpaperWorkUnit WorkUnit { get; init; }
    public IReadOnlyList<string> IssueCodes { get; init; } = Array.Empty<string>();
}
```

`WallpaperUnpackResult` 追加 `IReadOnlyList<WallpaperUnpackItemResult> ItemResults`；`WallpaperUnpackProgress` 追加 operation stage、`long CompletedWork`、`long? TotalWork`、unit、`IsIndeterminate` 和 `CanCancel`，现有字段全部保留。

- [ ] 写失败测试：冻结请求；重复输出目标在写盘前拒绝；只取消 `Committed` 成功项选择；失败/跳过/取消项保留；commit/rollback 期间状态真实；未知总量显示 indeterminate；取消后的旧树 hash 与 stage/backup 枚举不变。
- [ ] 运行 SmokeTests，确认逐项结果和选择语义断言失败。
- [ ] 在 planner 暴露已规划 entry/physical byte 总量；service 按 item append 结果，commit 成功后才写 `Committed`；异常按 rollback 事实写 `NotModified` 或 `AdditionalEffectsPossible`。
- [ ] 在 Shell 仅对 operation ID 匹配且 `Committed` 的结果取消选择；失败项发布可重试状态；进度 UI 分列显示阶段和工作量。
- [ ] 运行事务故障注入、完整 Smoke、Release build、输出树 SHA-256 对比与 `git diff --check`。
- [ ] Commit: `feat: report unpack work and per-item commit outcomes`

### Task 5: WP5 图库快照与稳定重复项处理

**Files:**

- Create: `tests/WallpaperField.SmokeTests/LibraryLifecycleRegressionTests.cs`
- Modify: `Models/WallpaperLibraryModels.cs`
- Modify: `Services/WallpaperLibraryService.cs`
- Modify: `ViewModels/ShellViewModel.cs`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

**Duplicate group contract:**

```csharp
public sealed record LibraryConflict
{
    public string WorkshopId { get; init; } = string.Empty;
    public IReadOnlyList<string> CandidatePaths { get; init; } = Array.Empty<string>();
}
```

`WallpaperLibraryResult` 追加 `IReadOnlyList<LibraryConflict> Conflicts`，现有 Items/Errors 保留。

- [ ] 写失败测试：刷新失败/取消保留上一快照；候选路径排序稳定；同一 WorkshopId 的全部重复候选被排除并产生单组冲突；合法递归 metadata 仍可发现；解包器拥有目录和 reparse 仍跳过。
- [ ] 运行 SmokeTests，确认当前 first-wins 和快照清空断言失败。
- [ ] 先收集、规范化和排序全部候选，再按 WorkshopId 分组；重复组全排除，非冲突项继续逐项隔离加载。
- [ ] Shell 仅在 service 正常返回后一次替换图库；失败/取消仅更新状态和问题。
- [ ] 运行定向/完整 Smoke、Release build 和 `git diff --check`。
- [ ] Commit: `fix: make library refresh stable and snapshot-safe`

### Task 6: WP8 统一路径、JSON 预算与表驱动启动参数

**Files:**

- Create: `Models/PathValidationModels.cs`
- Create: `Models/AppIssueModels.cs`
- Create: `Services/PathInputValidator.cs`
- Create: `Services/BoundedJsonReader.cs`
- Create: `Infrastructure/StartupOptions.cs`
- Create: `tests/WallpaperField.SmokeTests/InputValidationRegressionTests.cs`
- Modify: `Services/OutputPathPolicy.cs`
- Modify: `Services/WallpaperScanService.cs`
- Modify: `Services/WallpaperLibraryService.cs`
- Delete: `Infrastructure/LaunchOptions.cs`
- Modify: `App.xaml.cs`
- Modify: `ViewModels/ShellViewModel.cs`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

**Exact validation surfaces:**

先建立所有启动/输入问题共用的不可变领域模型，后续问题中心只增加存储、筛选和动作，不另造问题类型：

```csharp
public enum AppIssueSeverity { Information, Warning, Error }
public enum AppIssueSource { Startup, Scan, Unpack, Library, Settings, Diagnostics }
public enum AppDiskFact { Unknown, NotModified, Committed, RolledBack, AdditionalEffectsPossible }
public enum AppIssueResolutionState { Open, Resolved }
public enum AppIssueAction { None, Retry, ReviewInput, OpenOutput, OpenLogs, ExportDiagnostics }

public sealed record AppIssue(
    Guid Id,
    string Code,
    AppIssueSeverity Severity,
    AppIssueSource Source,
    Guid? OperationId,
    DateTimeOffset TimestampUtc,
    string Summary,
    string Details,
    AppDiskFact DiskFact,
    AppIssueAction SuggestedAction,
    string? PathContext,
    string ContextKey,
    AppIssueResolutionState ResolutionState,
    DateTimeOffset? ResolvedAtUtc);
```

```csharp
public enum PathInputRole { Source, Output }
public enum ValidationSeverity { None, Information, Warning, Error }

public sealed record PathValidationRequest(
    string Value,
    PathInputRole Role,
    string? OtherPath,
    long Version);

public sealed record PathValidationResult(
    string Input,
    string? NormalizedPath,
    ValidationSeverity Severity,
    string Code,
    string Message,
    long Version)
{
    public bool IsValid => Severity != ValidationSeverity.Error;
}
```

`BoundedJsonReader.MaxJsonBytes` 固定为 `4L * 1024 * 1024`；读取流最多允许 4 MiB + 1 byte，超限抛出稳定 `InputBudgetExceededException`。

```csharp
internal sealed record StartupParseResult(
    StartupOptions Options,
    IReadOnlyList<AppIssue> Issues);

internal static StartupParseResult Parse(IReadOnlyList<string> args);
```

- [ ] 写表驱动失败矩阵：缺值不吞下一 flag、first valid wins、重复 Warning、未知 token 只忽略自身、乱序、NaN/Infinity/超 16384、低于最小值夹紧、`--page problems`、exact `NUL`、尾点/空格、父子重叠、junction/symlink、4 MiB 边界和检查后增长。
- [ ] 运行 SmokeTests，确认当前 CLI、JSON 无界和路径策略漂移断言失败。
- [ ] 深化 `OutputPathPolicy` 并让 validator、scan、library、unpack 共用；UI 同步语法检查后以 250 ms latest-wins 物理检查，扫描验证不创建探针文件，staging 创建仍是写入权威检查。
- [ ] 用 `BoundedJsonReader` 替换 project/metadata 直接解析；超限逐项失败，不终止批任务。
- [ ] 用 `StartupOptions` 表驱动结果替换 parser；合法旧参数保持语义，非法参数发布启动问题并回退默认，存在 Error 时打开问题页。
- [ ] 运行完整输入矩阵、Smoke、Release build、`git diff --check`。
- [ ] Commit: `fix: validate paths json and startup options consistently`

### Task 7: WP6 结构化问题、隐私日志与诊断导出

**Files:**

- Modify: `Models/AppIssueModels.cs`
- Create: `Models/DiagnosticModels.cs`
- Create: `Services/DiagnosticExportService.cs`
- Create: `tests/WallpaperField.SmokeTests/ProblemDiagnosticsRegressionTests.cs`
- Modify: `Infrastructure/AppLog.cs`
- Modify: `Services/UserSettingsStore.cs`
- Modify: `ViewModels/ShellViewModel.cs`
- Modify: `Composition/AppComposition.cs`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

**Issue budget and immutable record:**

```csharp
public enum AppIssueSeverity { Information, Warning, Error }
public enum AppIssueSource { Startup, Scan, Unpack, Library, Settings, Diagnostics }
public enum AppDiskFact { Unknown, NotModified, Committed, RolledBack, AdditionalEffectsPossible }
public enum AppIssueResolutionState { Open, Resolved }
public enum AppIssueAction { None, Retry, ReviewInput, OpenOutput, OpenLogs, ExportDiagnostics }
```

`AppIssue` 必含 `Guid Id`、code、severity、source、operation ID、timestamp、summary、details、disk fact、action、path context、context key、resolution state 和 resolved time。`Details` 最大 4096 字符；可见问题最大 10,000，超限按 source/code 聚合。

- [ ] 写失败测试：问题不可变；成功重试只 resolve 同 source/code/context；清除只删 resolved；10,001 条触发聚合；复制全部不受筛选；默认导出无完整路径；显式选择后才含路径。
- [ ] 写日志失败测试：追加前 2 MiB 轮转、最多 5 份、删除 14 天前文件、并发写不交错、用户目录被占位符/指纹替换、日志失败进入内存问题且不递归写日志。
- [ ] 运行 SmokeTests，确认结构化问题、rolling 与导出契约 RED。
- [ ] 把 scan/unpack/library/startup/settings/log/diagnostics 失败映射为 `AppIssue`；保留页面紧凑摘要但不再以截断 ErrorText 作为唯一证据。
- [ ] `DiagnosticExportService.ExportAsync` 写 schema-versioned UTF-8 JSON，包含版本/commit/OS/arch/DPI/HC/motion/density/issues/counts，不含文件内容或 payload。
- [ ] 运行 0/1/50/1000 功能测试、日志故障注入、完整 Smoke、Release build 和 `git diff --check`。
- [ ] Commit: `feat: add bounded problem diagnostics and rolling logs`

### Task 8: WP6/WP7 三页导航、问题中心与稳定滚动布局

**Files:**

- Create: `tests/WallpaperField.SmokeTests/UiStructureRegressionTests.cs`
- Modify: `MainWindow.xaml`
- Modify: `MainWindow.xaml.cs`
- Modify: `ViewModels/ShellViewModel.cs`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

**Responsive state:**

```csharp
public enum ShellLayoutMode
{
    Compact,
    Regular,
    Wide
}
```

映射固定为 Compact 920–1059 DIP、Regular 1060–1189 DIP、Wide ≥1190 DIP。

- [ ] 写失败测试：问题页常驻导航；刷新不属于可隐藏统计容器；三个页面固定标题/操作区且各列表独立滚动；外层 ScrollViewer 不包虚拟化 ListBox；920 进度文字/数值分列；程序定位不调用祖先 `BringIntoView`。
- [ ] 运行 WPF host/Smoke，确认当前结构断言失败。
- [ ] 在现有 `MainWindow.xaml` 内先建立第三页和完整问题工具栏/list；扫描/图库只保留数量、最高严重度和跳转命令。
- [ ] 用单一 `ShellLayoutMode` 触发器替代逐控件 responsive mutation；紧凑模式只隐藏装饰和重复统计，不隐藏刷新、取消、问题入口或主命令。
- [ ] 删除页面级 ScrollViewer 与 `container.BringIntoView()`；保留 Recycling、Pixel scrolling 与 page cache。
- [ ] 运行 UI 结构测试、完整 Smoke、Release build；在 `temp/agent-work/20260820-2130-v1-2-2-full-roadmap/screenshots` 保存 920×680、1060、1190+ 三档 100% DPI 截图。
- [ ] Commit: `fix: add persistent problem page and responsive scrolling`

### Task 9: WP7 键盘、语义、High Contrast 与 reduced-motion

**Files:**

- Create: `Models/MotionModels.cs`
- Create: `Services/MotionPolicy.cs`
- Create: `tests/WallpaperField.SmokeTests/AccessibilityRegressionTests.cs`
- Modify: `App.xaml`
- Modify: `MainWindow.xaml`
- Modify: `MainWindow.xaml.cs`
- Modify: `Themes/EndfieldTheme.xaml`
- Modify: `Controls/AnimatedPreviewImage.cs`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

**Motion policy:**

```csharp
public sealed record MotionPreference(
    bool SystemAnimationsEnabled,
    bool ReducedMotionRequested)
{
    public bool MotionEnabled => SystemAnimationsEnabled && !ReducedMotionRequested;
}
```

- [ ] 写失败测试：三页标题有 HeadingLevel 且不进 Tab；图标按钮有稳定中文 AutomationName；焦点资源有深色外环+黄色内环；HC 资源引用 SystemColors；motion=false 时无页面/控件 storyboard；GIF 只加载静态首帧。
- [ ] 运行 Smoke/WPF host，确认当前 focus、HC、motion/GIF 契约 RED。
- [ ] 实现表面感知双层焦点；High Contrast 文本/背景/边界/选择/禁用态全部用 DynamicResource SystemColors，不靠固定黄表达状态。
- [ ] `MotionPolicy` 同时监听系统动画设置与 CLI，运行时更新；所有 Storyboard 与进度动画消费同一 policy。
- [ ] `AnimatedPreviewImage` 在 reduced-motion 分支用 WPF decoder 只取首帧并在 unload/offscreen 释放，不启动 XamlAnimatedGif。
- [ ] 运行键盘路径、WPF host、完整 Smoke、Release build；人工记录 HC、100/150/200% DPI 与 reduced-motion 结果，无法覆盖屏幕阅读器时标为未验证。
- [ ] Commit: `feat: complete accessible focus contrast and motion policy`

### Task 10: WP8 批量选择、筛选与密度效率

**Files:**

- Create: `Models/DisplayDensity.cs`
- Create: `tests/WallpaperField.SmokeTests/SelectionEfficiencyRegressionTests.cs`
- Modify: `Models/UserSettings.cs`
- Modify: `ViewModels/WallpaperCardViewModel.cs`
- Modify: `ViewModels/ShellViewModel.cs`
- Modify: `MainWindow.xaml`
- Modify: `Themes/EndfieldTheme.xaml`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

**Settings extension:**

```csharp
public enum DisplayDensity { Comfortable, Compact }
```

`UserSettings` append-only 增加 `DisplayDensity Density { get; init; } = DisplayDensity.Comfortable;`，旧 JSON 自动使用默认值。

- [ ] 写失败测试：仅可处理、仅失败/有问题筛选；选择当前匹配；清除选择；隐藏选择保持；选中数/匹配数准确；旧 settings 缺字段按 Comfortable 加载。
- [ ] 运行 SmokeTests，确认新筛选/批选/密度断言 RED。
- [ ] 在 Shell 增加组合 predicate 与命令；选择基于当前过滤快照执行，不修改未匹配项；问题状态来自结构化 issue code/context。
- [ ] 用 DynamicResource/trigger 实现两档卡片密度；主中文、次级工程英文在 Compact 可隐藏。
- [ ] 运行完整 Smoke、Release build；用同一窗口尺寸截图并记录两档首屏可见记录数。
- [ ] Commit: `feat: add efficient filters batch selection and density`

### Task 11: WP5–WP8 稳定性总门禁

**Files:**

- Modify: `temp/agent-work/20260820-2130-v1-2-2-full-roadmap/progress.md`
- Modify: `temp/maintenance-audit/02-bug-risk-register.md`
- Modify: `temp/maintenance-audit/04-verification-report.md`

- [ ] 运行 restore、Release rebuild、完整 Smoke、故意失败退出、自定义路径/CLI/JSON 矩阵和 `dotnet test` 发现检查。
- [ ] 对取消、Closing、commit/rollback 第 N 个 move 失败、磁盘树 SHA-256、stage/backup 残留做故障注入。
- [ ] 对问题 0/1/50/1000、920/1060/宽屏、100/150/200% DPI、键盘、HC、motion 做新鲜检查并保存 temp 证据。
- [ ] 从固定起点检查 `git diff --check`、调用者、错误/取消路径和用户已有状态；Blocker/Major 非零时回到对应任务修复。
- [ ] 仅在 WP5–WP8 Blocker/Major=0 时在进度记录门禁 PASS；temp 审计保留历史基线并追加 2026-08-21 状态。
- [ ] Commit 所有尚未提交的门禁内 tracked 修复；temp 文件不提交。

### Task 12: WP9 提取 TaskLifecycleCoordinator

**Files:**

- Create: `Application/TaskLifecycleCoordinator.cs`
- Create: `tests/WallpaperField.SmokeTests/CoordinatorRegressionTests.cs`
- Modify: `ViewModels/AsyncRelayCommand.cs`
- Modify: `ViewModels/ShellViewModel.cs`
- Modify: `Composition/AppComposition.cs`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

**Exact coordinator surface:**

```csharp
public sealed class TaskLifecycleCoordinator
{
    public TaskLifecycleSnapshot Current { get; }
    public event EventHandler<TaskLifecycleSnapshot>? Changed;
    public Task RunAsync(
        ForegroundOperationKind kind,
        Func<Guid, CancellationToken, Task> operation);
    public bool RequestCancellation();
    public bool SetCommitCritical(Guid operationId, bool isCritical);
    public Task<bool> WaitForQuiescenceAsync(TimeSpan timeout);
}
```

- [ ] 先让现有 Task 2 生命周期测试同时针对 coordinator seam 运行并失败，确认行为测试不依赖 Shell 私有字段。
- [ ] 把 operation ID/CTS/current Task/terminal state 从 Shell 移入 coordinator；不移动 scan/unpack/library 数据。
- [ ] Shell 只订阅只读 snapshot 并投影命令；App close 只调用 coordinator 取消/等待。
- [ ] 运行 Task 2/4/close 回归、完整 Smoke、Release build 和 `git diff --check`。
- [ ] Commit: `refactor: centralize foreground task coordination`

### Task 13: WP9 依次提取 Scan/Unpack/Library/ProblemCenter sessions

**Files:**

- Create: `ViewModels/Sessions/ScanSession.cs`
- Create: `ViewModels/Sessions/UnpackSession.cs`
- Create: `ViewModels/Sessions/LibrarySession.cs`
- Create: `ViewModels/Sessions/ProblemCenterSession.cs`
- Create: `tests/WallpaperField.SmokeTests/SessionBoundaryRegressionTests.cs`
- Modify: `ViewModels/ShellViewModel.cs`
- Modify: `Composition/AppComposition.cs`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

**Use-case surfaces:**

```csharp
public sealed class ScanSession : ObservableObject
{
    public Task ScanAsync();
    public IReadOnlyList<WallpaperRecord> FreezeSelectedItems();
    public void ApplyItemResults(Guid operationId, IReadOnlyList<WallpaperUnpackItemResult> results);
}

public sealed class UnpackSession : ObservableObject
{
    public Task UnpackAsync(IReadOnlyList<WallpaperRecord> frozenItems, string outputDirectory);
}

public sealed class LibrarySession : ObservableObject
{
    public Task RefreshAsync();
}

public sealed class ProblemCenterSession : ObservableObject
{
    public void Publish(IEnumerable<AppIssue> issues);
    public void Resolve(AppIssueSource source, string code, string contextKey, DateTimeOffset resolvedAtUtc);
    public void ClearResolved();
    public string CopySelected();
    public string CopyAll();
}
```

- [ ] 写边界失败测试：每个 session 是其领域数据唯一所有者；Shell 不再拥有可变 scan/library/issues 集合；service 不引用 ViewModel/WPF；合法 settings/metadata/服务契约未变化。
- [ ] 一次只提取一个 session：Scan → Unpack → Library → ProblemCenter；每次先让 Shell 暂时转发现有绑定，再运行对应特征测试，再删除重复字段和转换逻辑。
- [ ] composition 注入现有 I/O adapter 和具体 session；不为单一实现新增 interface/factory。
- [ ] Shell 最终只保留导航、布局、跨页跳转、全局摘要和四个 session 属性。
- [ ] 每个 session 提取后运行对应回归；最后运行完整 Smoke、Release build、架构引用检查和 `git diff --check`。
- [ ] Commit: `refactor: split shell state into focused sessions`

### Task 14: WP10 提取三个深页面

**Files:**

- Create: `Views/ScanPageView.xaml`
- Create: `Views/ScanPageView.xaml.cs`
- Create: `Views/LibraryPageView.xaml`
- Create: `Views/LibraryPageView.xaml.cs`
- Create: `Views/ProblemCenterView.xaml`
- Create: `Views/ProblemCenterView.xaml.cs`
- Modify: `MainWindow.xaml`
- Modify: `MainWindow.xaml.cs`
- Modify: `tests/WallpaperField.SmokeTests/UiStructureRegressionTests.cs`

- [ ] 扩展失败测试：MainWindow 只保留 rail/title/global summary/page host；每个 UserControl 拥有完整页面布局；绑定源分别是对应 session；没有重复可变状态。
- [ ] 依次移动 Scan、Library、ProblemCenter XAML，每次移动后运行 WPF host、绑定错误捕获、键盘/滚动/虚拟化/GIF 回归和目标尺寸截图。
- [ ] 页面 code-behind 仅处理其内部列表定位/生命周期；删除窗口逐页面控件引用和重复 responsive mutation。
- [ ] 运行完整 Smoke、Release build、`git diff --check`；对拆分前后同 fixture 截图做人工等价审阅。
- [ ] Commit: `refactor: extract scan library and problem views`

### Task 15: WP10 拆分四层主题资源

**Files:**

- Create: `Themes/Tokens.xaml`
- Create: `Themes/AccessibilityMotion.xaml`
- Create: `Themes/BaseControls.xaml`
- Create: `Themes/DomainComponents.xaml`
- Modify: `Themes/EndfieldTheme.xaml`
- Modify: `App.xaml`
- Modify: `tests/WallpaperField.SmokeTests/AccessibilityRegressionTests.cs`

合并顺序必须是 Tokens → Accessibility/Motion → BaseControls → DomainComponents；`EndfieldTheme.xaml` 只作为按该顺序 merge 的兼容入口。

- [ ] 写失败测试：四个字典路径存在且顺序固定；所有被覆盖色彩用 DynamicResource；启动时每个引用 key 可解析；HC/motion overlay 不被后层意外覆盖。
- [ ] 逐层搬移资源，每搬一层运行真实 WPF host；不同时修改视觉值。
- [ ] 删除原字典重复 key，保留兼容入口与黑/白/Signal Yellow token 名称。
- [ ] 运行完整 Smoke、Release build、HC/motion/focus 检查、截图对比和 `git diff --check`。
- [ ] Commit: `refactor: layer theme resources by responsibility`

### Task 16: PERF-001 测量并只在阈值触发时修复

**Files:**

- Create: `tests/WallpaperField.SmokeTests/PerformanceRegressionTests.cs`
- Create: `temp/agent-work/20260820-2130-v1-2-2-full-roadmap/performance-report.md`
- Modify conditionally: `Models/WallpaperRecord.cs`
- Modify conditionally: `ViewModels/WallpaperCardViewModel.cs`
- Modify conditionally: `Themes/DomainComponents.xaml`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

- [ ] 建立 1000-card fixture，记录首次呈现、5 次筛选、连续滚动 frame time 和 UI thread 文件系统调用；Problem Center 1000 条的加载/筛选/复制取 5 次最慢值。
- [ ] 判定门槛：非首次交互 p95 >200 ms、滚动 frame p95 >33.3 ms、getter/滚动发生任意文件系统 I/O、问题最慢值 >500 ms 或 host first idle >1s 时必须修复。
- [ ] 当前 `WallpaperRecord` 的 `HasPreview`/可用性 getter 若触发 I/O，先写 instrumentation 失败断言，再在加载边界缓存存在性；若每卡阴影是阈值根因，删除或降级该效果。
- [ ] 重新测量同一 fixture，记录 before/after、机器环境、运行次数和是否实施修复；未触阈值时记录“测量后不改”，不做猜测优化。
- [ ] 运行完整 Smoke、Release build 和 `git diff --check`。
- [ ] Commit（仅有 tracked 变更时）: `perf: remove measured card interaction bottlenecks`

### Task 17: WP11 删除有证据残留并评估上游 compile whitelist

**Files:**

- Create: `tests/WallpaperField.SmokeTests/UpstreamBoundaryRegressionTests.cs`
- Create: `scripts/verify-repkg-compile-surface.ps1`
- Modify: `Models/WallpaperScanModels.cs`
- Modify: `Services/WallpaperStorage.cs`
- Modify conditionally: `ThirdParty/RePKG/Source/RePKG.Application/RePKG.Application.csproj`
- Modify: `ThirdParty/RePKG/UPSTREAM-PATCHES.md`
- Modify: `THIRD-PARTY-NOTICES.md`
- Modify: `docs/EXTENDING.md`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

- [ ] 全仓 `rg` 建引用图并写失败/结构测试，证明产品 RePKG 调用只来自第一方 adapter；固定 PKG/TEX fixture 继续走 `SafePackageReader`。
- [ ] 在独立提交删除无调用者 `ScanStage.CopyingPreview/SavingMetadata/WritingIndex`、`WallpaperIndex`、旧 index/id 常量和 `WriteTextAtomicallyAsync`；每删除一组运行 build/Smoke。
- [ ] 用脚本输出当前 RePKG compile 集、候选 whitelist、实际减少数和遗漏引用；在隔离临时副本验证 whitelist build、Smoke、fixture、publish file set、licenses/notices。
- [ ] 只有“实际输入减少、全部验证通过、许可不减少、脚本可持续验证”四项同时成立才改 csproj；否则恢复候选实验文件并把不实施证据写入 UPSTREAM-PATCHES/审计，不手工维护脆弱清单。
- [ ] 复核 LIB-001/LIB-002、DWM P/Invoke 与剩余 P2/P3；只实施已在批准范围且有回归证据的最小修复。
- [ ] 运行 restore、完整 Smoke、Release build、许可/发布文件检查和 `git diff --check`。
- [ ] Commit: `chore: narrow verified upstream and evolution surface`

### Task 18: v1.2.2 版本、文档与发布合同

**Files:**

- Create: `docs/releases/v1.2.2.md`
- Modify: `WallpaperField.csproj`
- Modify: `build-release.ps1`
- Modify: `tests/WallpaperField.SmokeTests/ReleaseContractTests.cs`
- Modify: `README.md`
- Modify: `docs/EXTENDING.md`
- Modify: `temp/maintenance-audit/01-architecture-and-evolution.md`
- Modify: `temp/maintenance-audit/02-bug-risk-register.md`
- Modify: `temp/maintenance-audit/03-ux-audit.md`
- Modify: `temp/maintenance-audit/04-verification-report.md`
- Modify: `temp/maintenance-audit/05-maintenance-roadmap.md`
- Modify: `temp/maintenance-audit/06-security-dependencies-release.md`

版本常量必须为：`Version=1.2.2`、`FileVersion=1.2.2.0`、`AssemblyVersion=1.0.0.0`；InformationalVersion 在发布时为 `1.2.2+` 加 `git rev-parse HEAD` 返回的完整 40 位 SHA。

- [ ] 先把 ReleaseContractTests 的期望改为 v1.2.2 并运行 RED，确认当前 v1.2.1 被准确捕获。
- [ ] 更新 csproj 与发布脚本版本；更新 README、扩展规则和 v1.2.2 release notes，明确递归图库规则、问题/诊断、CLI、密度、取消/关闭、unsigned 与 `dotnet test` 限制。
- [ ] 审计文档只追加 2026-08-21 处置证据，不改写历史基线；每项列风险 ID、回归测试与实际命令。
- [ ] 运行 restore、Release build、完整 Smoke、PowerShell parser、release contract 和 `git diff --check`；确认根 EXE hash 未变化。
- [ ] Commit: `release: prepare Wallpaper Field v1.2.2`

### Task 19: 固定起点独立审阅与修复循环

**Files:**

- Create: `temp/agent-work/20260820-2130-v1-2-2-full-roadmap/review-report.md`
- Modify as findings require: only files already in roadmap scope

- [ ] 从 `61a129c..HEAD` 枚举每个 commit/file，逐条对照批准规格、WP5–WP11、调用者、取消/失败/磁盘事实、隐私、A11Y、性能和发布完整性。
- [ ] 运行 code-review 检查并按 Blocker/Major/Minor 分类；每个 finding 写文件/行、复现、风险 ID 和所需测试。
- [ ] 对每个 Blocker/Major 先新增失败断言，再最小修复并重跑相关矩阵；重复审阅直到 Blocker=0、Major=0。
- [ ] 运行完整 restore/build/Smoke、故意失败退出、`dotnet test` 限制检查、analyzer、NuGet vulnerability/deprecated/outdated、PowerShell parser/actionlint、UI/事务矩阵和 `git diff --check`。
- [ ] 最终报告写 `Verdict: PASS`、剩余 Minor/未验证项和用户原有改动身份；若没有 PASS，不得进入合并。
- [ ] Commit any review fixes: `fix: resolve v1.2.2 review findings`

### Task 20: 合并 main、合并后验证与本地 RC

**Files:**

- Modify: Git history only; do not modify `GUI_for_RePKG.exe` or `AGENTS.md`
- Create in ignored evidence area: `temp/maintenance-audit/v1.2.2-rc/`
- Modify: `temp/agent-work/20260820-2130-v1-2-2-full-roadmap/progress.md`
- Modify: `temp/agent-work/20260820-2130-v1-2-2-full-roadmap/task_plan.md`

- [ ] 在 feature branch 确认 tracked diff clean、用户 EXE SHA-256 仍为 `8001236992F498920283DEFCB1CC15B90657AB126FC4E7D845859D2D1F159BD7`、AGENTS 仍未跟踪。
- [ ] 切换 `main` 并用非破坏性 merge 合并 `codex/v1.2.2-roadmap`；不 rebase/reset 用户文件，不创建或 push tag。
- [ ] 从合并后的 `main` commit 建立 clean 隔离 worktree `D:\CSC Project\GUI-for-RePKG\temp\agent-work\20260820-2130-v1-2-2-full-roadmap\merged-main-worktree`；在其中重新运行 restore、Release build、完整 Smoke、失败退出合同、`dotnet test` 限制检查和 `git diff --check`。
- [ ] 在 clean worktree 运行 `./build-release.ps1 -OutputDirectory 'D:\CSC Project\GUI-for-RePKG\temp\maintenance-audit\v1.2.2-rc'`，绝不传 `-UpdateTrackedExecutable`。
- [ ] 验证 RC ZIP 精确包含 EXE、根 LICENSE、ThirdParty notices、RePKG license/patches、release notes、manifest、SBOM/dependencies 与 hash；核对 FileVersion/ProductVersion/commit、SHA-256 和诚实 `NotSigned`。
- [ ] 删除隔离 worktree前先验证其绝对路径只位于本任务临时目录；保留 RC 与日志证据，不删除用户工作树内容。
- [ ] 再次审阅 `main` 相对固定起点 diff，确认 review report 仍 PASS；最终 `task_plan.md` 所有阶段 completed、下一步“无”。

## Final Acceptance

- `main` 包含全部 v1.2.2 tracked 提交，工作树只剩用户最初的 EXE 修改和未跟踪 AGENTS。
- fresh merge-commit build 为 0 warnings/0 errors；Smoke machine summary 为正数且 exit 0；故意失败入口 exit 非零。
- 所有 Blocker/Major 已关闭，固定起点 review 为 `Verdict: PASS`。
- RC 来自 clean merged-main commit，未覆盖根 EXE；版本、manifest、ZIP、SBOM/dependencies、许可与 SHA-256 一致，签名状态如实为 `NotSigned`。
- 最终回复列出修改文件分组、实际命令/结果、未验证项、剩余风险、合并 commit 和 RC 绝对路径。
