# Wallpaper Field v1.3.0 Project Browser Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development` and execute one task at a time with a fresh task review.

**Goal:** 在不改变 v1.2.2 安全提取、事务、取消和既有三页语义的前提下，交付独立 BROWSE 第四页、高密度虚拟化封面目录、安全单项/批量处理、完整无障碍/性能证据，并把版本统一为 1.3.0。

**Architecture:** `ScanSession` 在成功扫描后原子发布含 revision 的共享 card 快照并继续拥有选择；`BrowsePageViewModel` 只做纯内存筛选、排序、当前项和虚拟化行投影；`UnpackSession` 只接受由当前 snapshot 生成的冻结请求并复用唯一任务槽；应用级预览服务用租约、4 并发和双上限缓存隔离不可信图片；Shell 只增加第四路由和跨页协调。

**Tech Stack:** C# 13、.NET 10、WPF/XAML、PowerShell、现有 console SmokeTests harness、Git。

**Spec:** `docs/superpowers/specs/2026-08-21-wallpaper-field-v1-3-0-project-browser-design.md`

## Global Constraints

- 实施基线固定为 `main@ab5123d71b1fce58177f2439fd9a228209a3bf72`，实施分支固定为 `codex/v1.3.0-project-browser`。
- 当前主工作树已有用户改动 `M GUI_for_RePKG.exe`、`?? AGENTS.md`；不得暂存、提交、覆盖、删除或把它们计为本任务改动。
- 每个新的开发 Agent 在首次编辑前，必须从外层仓库完整读取 `D:\CSC Project\GUI-for-RePKG\AGENTS.md`，并枚举、逐份读到 EOF 的 `D:\CSC Project\GUI-for-RePKG\temp\maintenance-audit\*.md`；不得复用其他 Agent 的 manifest。
- 不执行 `doc/plan` 中尚未授权的目录重组；保持当前根源码、`tests/`、`ThirdParty/`、`docs/superpowers/` 布局。
- 不修改 ThirdParty/RePKG、SafePackageReader、PKG/TEX reader、输出规划、staging、commit/rollback 或发布许可集合；产品 PKG 路径继续只经第一方安全 adapter。
- 扫描保持只读；失败/取消保留上一成功 snapshot；成功才一次发布新 revision 并清空旧选择。
- 分类唯一且全链一致：Website → valid declared Video → invalid declared Video/Other → valid Package → Other。Website 与失效 video 不得因 stray PKG 进入请求。
- 单项/批量必须使用 `FrozenWallpaperProcessRequest`；`UnpackSession` 启动前验证当前 revision/identity/membership/output，服务继续验证真实磁盘事实和重复目标。
- 任一前台 I/O 活动时用户选择只读；只有 `Succeeded + Committed` 项从共享选择移除。
- Browse 网格/详情 GIF 都是静态首帧。预览硬上限：压缩输入 64 MiB、宽高各 4096、源画布 16M pixels、active decode 4、cache 128 entries 且估算解码字节不超过 128 MiB。
- getter、筛选、排序、分行、CanExecute 和 UI Dispatcher 滚动回调不得做同步文件 I/O；后台预览 I/O 必须由租约、取消、并发和预算约束。
- 外层回收虚拟化行是唯一主滚动；不得使用 WrapPanel、祖先 ScrollViewer、`BringIntoView`、第三方面板或每卡阴影。
- 920×680 至宽屏保留搜索/筛选、详情动作、问题入口、清选、处理、取消和全局状态；新增资源必须覆盖 High Contrast 和统一 motion policy。
- 不覆盖或提交根 EXE，不 push/tag/remote release，不使用签名秘密。最终 RC 只从合并后的 clean main commit 输出到 `temp/`。
- 每个任务遵循 RED→GREEN→REFACTOR；RED 必须是新增行为断言的预期失败，不接受编译错误、fixture 错误或其他回归。
- 每个任务提交前运行 focused evidence、完整 Release build、完整 SmokeTests 和 `git diff --check`。Smoke 摘要必须 tests>0、assertions>0、passed=1、failed=0。
- `dotnet test WallpaperField.slnx` 当前执行 0 项，只作为发现能力限制记录，不能替代 console SmokeTests。
- 真实 High Contrast、Narrator/屏幕阅读器、物理 150%/200% DPI、低端硬件、签名、SmartScreen 和干净 VM 若环境不可用，必须列为未验证，不得推断通过。

## Standard Verification

从实现 worktree 根运行：

```powershell
dotnet restore WallpaperField.slnx --configfile NuGet.Config
dotnet build WallpaperField.slnx -c Release --no-restore --no-incremental
dotnet run --project tests/WallpaperField.SmokeTests/WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore
dotnet run --project tests/WallpaperField.SmokeTests/WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore -- --verify-failure-exit
git diff --check
git status --short --branch
```

故意失败入口预期非零；普通 Smoke 预期 exit 0。每个 RED 后先运行同一完整 harness，确认唯一新增失败，再实施 GREEN。

---

### Task 1: 固定项目分类、原子 snapshot 与安全冻结请求

**Files:**

- Create: `Models/ProjectBrowserModels.cs`
- Create: `ViewModels/ScanProjectSnapshot.cs`
- Create: `tests/WallpaperField.SmokeTests/ProjectBrowserFoundationRegressionTests.cs`
- Modify: `Models/WallpaperRecord.cs`
- Modify: `Services/WallpaperScanService.cs`
- Modify: `Services/RePkgWallpaperUnpackService.cs`
- Modify: `ViewModels/WallpaperCardViewModel.cs`
- Modify: `ViewModels/Sessions/ScanSession.cs`
- Modify: `ViewModels/Sessions/UnpackSession.cs`
- Modify: `ViewModels/ShellViewModel.cs`
- Modify: `Composition/AppComposition.cs`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`
- Modify: `tests/WallpaperField.SmokeTests/SessionBoundaryRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/UnpackLifecycleRegressionTests.cs`

**Required contracts:**

```csharp
public enum WallpaperProjectKind
{
    Package,
    Video,
    Website,
    Other
}

public sealed record FrozenWallpaperProcessRequest(
    ScanSnapshotIdentity SnapshotIdentity,
    long SnapshotRevision,
    string OutputDirectory,
    IReadOnlyList<WallpaperRecord> Items);

public sealed record ScanProjectSnapshot(
    ScanSnapshotIdentity Identity,
    long Revision,
    IReadOnlyList<WallpaperCardViewModel> Projects);
```

`WallpaperRecord` 追加纯计算 `ProjectKind`、`ProjectKey`、`IsProcessable`，以及扫描时事实 `PreviewFileLength`、`PreviewLastWriteTimeUtc`、`PreviewFormat`。ProjectKey 为 Workshop ID 加规范化项目源路径的 SHA-256 指纹，不包含明文路径。

- [ ] RED：注册 foundation tests，用 reflection/现有服务 fixture 证明四类合同缺失；web+pkg、web+video、video+pkg、invalid-video+pkg 当前分类/dispatch 不符合唯一规则；成功扫描没有单一 snapshot revision；stale/current-path-changed/foreign-item 请求仍可绕过 session identity。
- [ ] GREEN：实现唯一分类器并让 `HasUnpackableContent`、card 资格、冻结和 `RePkgWallpaperUnpackService` dispatch 共用；不改变 PKG/TEX/事务内部实现。
- [ ] GREEN：扫描发现 preview 时拒绝 reparse，捕获长度/mtime/格式；失败降级为 warning/无预览，不让单项终止全扫描。
- [ ] GREEN：`ScanSession` 先创建共享 cards，再设置 identity，最后一次发布递增 revision 的 `ProjectSnapshot`；失败/取消不发布。
- [ ] GREEN：增加 `TrySetUnpackSelection`、批量 set/clear、`TryFreezeSelectedRequest`、`TryFreezeItemRequest` 与 `IsCurrentSnapshot`；保留现有 Scan 页 card/集合兼容面。
- [ ] GREEN：`UnpackSession.UnpackAsync(FrozenWallpaperProcessRequest)` 在占用协调器前复制 Items 并调用同一 ScanSession 验证 revision/identity/membership/output；Shell 的旧 `UnpackCommand` 改走新请求。
- [ ] 验证 mixed-type 不写盘矩阵、stale request、duplicate item/output、current path drift、Committed-only 清选和原有事务/取消回归；运行标准验证。
- [ ] Commit: `feat: secure project snapshots and processing requests`

### Task 2: 实现 Browse 纯投影、当前项与共享选择语义

**Files:**

- Create: `ViewModels/BrowsePageViewModel.cs`
- Create: `ViewModels/BrowseProjectViewModel.cs`
- Create: `ViewModels/BrowseRowViewModel.cs`
- Create: `tests/WallpaperField.SmokeTests/ProjectBrowserProjectionRegressionTests.cs`
- Modify: `Composition/AppComposition.cs`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`

**Projection surface:**

```csharp
public enum ProjectBrowserKindFilter { All, Package, Video, Website, Other }
public enum ProjectBrowserSort { Name, WorkshopId, KindThenName }

public sealed class BrowsePageViewModel : ObservableObject, IDisposable
{
    public RangeObservableCollection<BrowseRowViewModel> Rows { get; }
    public IReadOnlyList<BrowseProjectViewModel> VisibleProjects { get; }
    public BrowseProjectViewModel? CurrentProject { get; set; }
    public string SearchText { get; set; }
    public ProjectBrowserKindFilter KindFilter { get; set; }
    public bool ShowOnlyProcessable { get; set; }
    public bool ShowOnlyProblems { get; set; }
    public ProjectBrowserSort Sort { get; set; }
    public int ColumnCount { get; }
    public void SetColumnCount(int columns);
    public bool RevealProject(string projectKey, bool clearBlockingFilters);
}
```

- [ ] RED：0/1/130/1000 项、名称/ID搜索、四类筛选、可处理+问题组合、三种稳定排序、3/4/5/6 列分行、最后一行空槽、连续快速设置最终值、无同步文件 I/O 的测试失败。
- [ ] RED：主体当前项与批量选择分离；隐藏选择保留/计数；选择当前匹配只选可处理项；全清作用整个 snapshot；foreground busy 拒绝切换；同源重扫恢复当前 ProjectKey、换源不误恢复；失败/取消旧 snapshot 保持。
- [ ] GREEN：wrapper 只引用共享 card/record；Rows 只引用 wrappers；所有选择动作调用 ScanSession API，不维护第二个 HashSet。
- [ ] GREEN：投影同步纯内存，搜索 trim + OrdinalIgnoreCase；Name 用 CurrentCultureIgnoreCase/ID tie-break，ID 为字符串，Kind 顺序 Package→Video→Website→Other。
- [ ] GREEN：订阅 `ProjectSnapshot`、共享 card、ProblemCenter 变化并在换 snapshot/`Dispose` 时完整解除；问题筛选为 record warnings 或关联 open issue。
- [ ] GREEN：当前项、roving focus identity、选择摘要、隐藏数量、Package/Video 分解与空状态均为可测试属性；不加入 WPF 文件 I/O。
- [ ] 运行 1000 项纯模型五轮测量，确认每项 p95≤200 ms；运行标准验证。
- [ ] Commit: `feat: add project browser projections and selection state`

### Task 3: 添加第四路由、空页面与扫描后入口

**Files:**

- Create: `Views/BrowsePageView.xaml`
- Create: `Views/BrowsePageView.xaml.cs`
- Create: `tests/WallpaperField.SmokeTests/ProjectBrowserNavigationRegressionTests.cs`
- Modify: `Infrastructure/StartupOptions.cs`
- Modify: `ViewModels/ShellViewModel.cs`
- Modify: `Composition/AppComposition.cs`
- Modify: `MainWindow.xaml`
- Modify: `MainWindow.xaml.cs`
- Modify: `Views/ScanPageView.xaml`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`
- Modify: `tests/WallpaperField.SmokeTests/InputValidationRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/UiStructureRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/AccessibilityRegressionTests.cs`

**Route contract:** `01 SCAN / 02 BROWSE / 03 LIBRARY / 04 PROBLEMS`。命名别名保留；数字别名按新编号迁移。

- [ ] RED：reflection/XML/WPF 断言缺 `BrowsePageView`、`NavigateBrowseCommand`、Browse host/nav/Heading、四页可见性/动画/snapshot 委托；`--page browse` 与新数字别名失败。
- [ ] GREEN：Shell 只追加 Browse VM 引用、导航、page code/title/subtitle 和跨页事件；不得转发 Browse 搜索/排序/Rows。
- [ ] GREEN：MainWindow rail 顺序为 Scan/Browse/Library/Problems，数量为 OPERATIONS/04；四页 host、可见性、reduced-motion 动画和 `PositionSnapshotAsync` 路由完整。
- [ ] GREEN：Browse 无 snapshot 时显示说明性空状态、当前路径状态和“前往扫描中心”；无处理按钮/空白网格。
- [ ] GREEN：Scan 成功摘要增加“浏览 N 个项目”主入口和 Browse nav 数量；完成任务不强制跳页。
- [ ] GREEN：StartupOptions 支持 `browse/02`，Library/Problems 数字迁为 03/04；README 更新留到 Task 8，但 parser tests 现在固定兼容。
- [ ] 运行四页 WPF host、空状态、键盘导航、reduced-motion clock 和标准验证。
- [ ] Commit: `feat: add the project browser route`

### Task 4: 建立有界静态缩略图服务和回收控件

**Files:**

- Create: `Models/PreviewThumbnailModels.cs`
- Create: `Services/IPreviewThumbnailDecoder.cs`
- Create: `Services/WpfPreviewThumbnailDecoder.cs`
- Create: `Services/PreviewThumbnailService.cs`
- Create: `Controls/ThumbnailPreviewImage.cs`
- Create: `tests/WallpaperField.SmokeTests/ProjectBrowserPreviewRegressionTests.cs`
- Modify: `Composition/AppComposition.cs`
- Modify: `ViewModels/BrowsePageViewModel.cs`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`
- Modify: `tests/WallpaperField.SmokeTests/PerformanceRegressionTests.cs`

**Exact limits:**

```csharp
public const long MaximumInputBytes = 64L * 1024 * 1024;
public const int MaximumDimension = 4096;
public const long MaximumSourcePixels = 16L * 1024 * 1024;
public const int MaximumConcurrentDecodes = 4;
public const int MaximumEntries = 128;
public const long MaximumDecodedCacheBytes = 128L * 1024 * 1024;
```

- [ ] RED：PNG/JPEG/GIF 首帧、missing/corrupt/64MiB+1/4097 dimension/16M+1 pixels/reparse、目标 bucket、Freeze、文件无锁与 stale completion 测试失败。
- [ ] RED：同 key in-flight 去重、active≤4、cache count/bytes 双驱逐、离屏租约取消、无观察者 pending 清理、失败缓存、generation 丢弃和 metrics 断言失败。
- [ ] GREEN：decoder 的 FileInfo/FileStream/BitmapDecoder 全在 worker；先重验扫描长度/reparse 与编码 envelope，再目标尺寸 decode、OnLoad、Freeze；所有路径释放 stream/decoder/buffer。
- [ ] GREEN：service 用规范路径+scan length+scan mtime+size bucket 作 key；租约引用计数，同 key 共享；最后观察者离开取消排队/解码；取消/驱逐不发布问题。
- [ ] GREEN：`ThumbnailPreviewImage` 只在视口±一行 overscan 请求；Loaded/Unloaded/recycle/source/generation 变化释放旧租约；不直接读取文件系统。
- [ ] GREEN：失败返回稳定占位状态并由 Browse VM 按 ProjectKey+preview version 去重发布接口（实际问题模型接线在 Task 6）；成功重试提供 resolve 信号。
- [ ] 使用 fake decoder 测峰值/队列和实际 WPF decoder 测格式/预算/文件句柄；运行标准验证。
- [ ] Commit: `feat: add bounded project thumbnails`

### Task 5: 完成虚拟化网格、详情、响应式和文件夹动作

**Files:**

- Create: `Models/ProjectFolderTarget.cs`
- Create: `Services/ProjectFolderTargetResolver.cs`
- Create: `tests/WallpaperField.SmokeTests/ProjectBrowserUiRegressionTests.cs`
- Modify: `Views/BrowsePageView.xaml`
- Modify: `Views/BrowsePageView.xaml.cs`
- Modify: `ViewModels/BrowsePageViewModel.cs`
- Modify: `ViewModels/BrowseProjectViewModel.cs`
- Modify: `Themes/Tokens.xaml`
- Modify: `Themes/DomainComponents.xaml`
- Modify: `MainWindow.xaml.cs`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`
- Modify: `tests/WallpaperField.SmokeTests/UiStructureRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/AccessibilityRegressionTests.cs`

- [ ] RED：外层 row ListBox/VirtualizingStackPanel Recycling+Pixel+page cache、行内固定列 UniformGrid、无祖先 ScrollViewer/WrapPanel/BringIntoView/逐卡阴影的结构断言失败。
- [ ] RED：主体 click 仅设当前、checkbox/Space 才选、左右不跨行、上下±columns/最后行最近列、Enter 详情、Escape、Compact 关闭焦点返回、重排按 ProjectKey 恢复的 WPF 测试失败。
- [ ] RED：920/1059/1060/1189/1190/1600 DIP 的 3/4/5–6 列、104 DIP min card、8 DIP gap、常驻/覆盖详情、工具栏/filter layer、72 DIP tray 保留槽和唯一入口断言失败。
- [ ] GREEN：卡片主体 Button 与 40×40 CheckBox 是同层兄弟；checkbox IsTabStop=false 但保留 UIA Toggle；主体/Automation summary 含完整标题、ID、类型、原因、warning、当前和选择。
- [ ] GREEN：16:10 `UniformToFill`、固定边界、两行标题、类型/warning/不可处理文字；hover/focus 只缩放 image layer 1.02，motion=false 固定 1.0；无每卡 shadow。
- [ ] GREEN：Wide/Regular 常驻详情，Compact 内联模态覆盖层；底层 inert、局部 Tab cycle、可见关闭、Escape、离页关闭和原 card 焦点恢复。正文独立滚动，动作固定底部。
- [ ] GREEN：Compact 搜索常驻，类型/状态/排序进入明确筛选层并恢复按钮焦点；页面外层不滚动。
- [ ] GREEN：folder resolver 在 worker 解析并保存“输出存在否则源”；执行只重验并打开显示的同一路径，消失时受控失败且不 fallback。
- [ ] GREEN：Browse 网格与详情都接入 Task 4 静态 thumbnail；placeholder/warning 不改变卡片几何。
- [ ] 运行 WPF runtime geometry、keyboard、HC resource、motion、folder race、thumbnail viewport 和标准验证。
- [ ] Commit: `feat: build the virtualized project browser workspace`

### Task 6: 接入 processing tray、问题关联与跨页定位

**Files:**

- Modify: `Models/AppIssueModels.cs`
- Modify: `Models/WallpaperUnpackModels.cs`
- Modify: `ViewModels/Sessions/ScanSession.cs`
- Modify: `ViewModels/Sessions/UnpackSession.cs`
- Modify: `ViewModels/Sessions/ProblemCenterSession.cs`
- Modify: `ViewModels/BrowsePageViewModel.cs`
- Modify: `ViewModels/ShellViewModel.cs`
- Modify: `Views/BrowsePageView.xaml`
- Modify: `Views/BrowsePageView.xaml.cs`
- Modify: `Views/ProblemCenterView.xaml`
- Modify: `Views/ProblemCenterView.xaml.cs`
- Modify: `Controls/ThumbnailPreviewImage.cs`
- Create: `tests/WallpaperField.SmokeTests/ProjectBrowserProcessingRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/Program.cs`
- Modify: `tests/WallpaperField.SmokeTests/ProblemDiagnosticsRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/SelectionEfficiencyRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/UnpackLifecycleRegressionTests.cs`

- [ ] RED：单项处理意外包含选中队列、隐藏选择未全批、stale snapshot/路径漂移仍启动、运行中可改选择、partial/failed/cancelled 清错选择的测试失败。
- [ ] RED：无选择的单项操作不显示 tray/取消；CommitCritical 文案不真实；完成摘要/清除缺失；Package/Video 分解与真实逐项结果不一致。
- [ ] RED：缺 `AppIssueSource.Browse` 和可选 `ProjectKey`；preview/folder 问题不去重/resolve；Browse→Problems 和 Problems→精确 ProjectKey 跳转失败或会串同 ID 不同源。
- [ ] GREEN：UnpackSession 暴露一份最近冻结范围/逐项摘要，processing tray 对选择、单项/批量运行、CommitCritical、取消和完成共用该事实；提供清除完成摘要。
- [ ] GREEN：Browse 当前动作只冻结 Current ProjectKey；批量只冻结整个共享选择；运行前双重 identity gate；重复触发仍由唯一 coordinator 拒绝。
- [ ] GREEN：保持 existing `ItemResultsAvailable -> ScanSession.ApplyItemResults`，只有 `Succeeded + Committed` 清选；失败、跳过、取消、未提交和迟到旧 operation 保留/忽略正确。
- [ ] GREEN：AppIssue 追加 `Browse` 与可选 ProjectKey；Scan/Unpack/Browse 项目问题填同一 key，搜索包含 Workshop ID；完整详情仍只在 ProblemCenter。
- [ ] GREEN：详情“查看问题中心”按 ProjectKey 选择精确问题；Problem 卡提供“在项目浏览中查看”。Reveal 自动清阻挡 Browse 条件，只滚网格；旧 ProjectKey 显示过期而不误跳。
- [ ] GREEN：preview 失败按 ProjectKey+preview version 聚合一次，成功重试 resolve；取消/驱逐不发 issue；folder target 消失发 Browse warning。
- [ ] 运行 single/batch mixed Package+Video、partial failure、cancel、CommitCritical、late result、selection、problem copy/export 和原事务/关闭回归；运行标准验证。
- [ ] Commit: `feat: connect safe project processing and problem navigation`

### Task 7: 完成性能、响应式、无障碍与视觉门禁

**Files:**

- Modify: `tests/WallpaperField.SmokeTests/PerformanceRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/AccessibilityRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/UiStructureRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/ProjectBrowserPreviewRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/ProjectBrowserUiRegressionTests.cs`
- Modify as measured: only Browse files and theme resources already introduced by Tasks 2–6
- Create in ignored evidence area: `D:\CSC Project\GUI-for-RePKG\temp\agent-work\20260829-0115-v1-3-full-implementation\evidence\`
- Create tracked user image after verified capture: `docs/images/project-browser.png`

- [ ] 建立 1000 项固定 fixture，包含 PNG/JPEG/GIF、missing、corrupt、over-budget、长标题、Package/Video/Website/Other、warnings 与同 ID 不同源；不在测量区间创建 fixture 文件。
- [ ] 每项先 warm-up，再至少五轮测名称/ID搜索、组合筛选、三种排序、3↔4↔5↔6 分行；记录 samples 与 p95，硬门禁≤200 ms。
- [ ] 连续滚动至少 30 step、五轮，记录 frame-time p95≤33.3 ms；断言 realized row/container 为可见+有界 cache，而非宽松的总数比例。
- [ ] 断言 UI Dispatcher filesystem calls=0、active decode≤4、cache≤128/128MiB、pending/lease 有界、全库往返后 cache/handle 不持续增长。
- [ ] WPF 几何矩阵：920×680、1059×680、1060×760、1189×800、1190×800、1600×1000；覆盖 empty、1000项、filters、hidden selection、details、tray、running/cancel/CommitCritical。
- [ ] A11Y：Browse Heading Level1、Nav AutomationName、roving focus、Space/Enter/Escape、UIA Toggle、live region 节流、双层焦点、HC resource whitelist、reduced-motion 无动画 clocks。
- [ ] 用真实 WPF CLI/fixture 生成受控 screenshot 到 evidence；确认 readiness 后选择 1600×1000 图复制为 `docs/images/project-browser.png`。HTML/静态 XAML 不算运行证据。
- [ ] 仅在实测超门槛时修最小根因；保存 before/after、机器/DPI/运行数。记录无法完成的真实 HC/Narrator/物理 DPI/低端硬件。
- [ ] 运行标准验证和故意失败入口。
- [ ] Commit: `perf: verify the project browser experience`

### Task 8: 统一 v1.3.0 版本、文档与本地发布合同

**Files:**

- Create: `docs/releases/v1.3.0.md`
- Modify: `WallpaperField.csproj`
- Modify: `app.manifest`
- Modify: `build-release.ps1`
- Modify: `tests/WallpaperField.SmokeTests/ReleaseContractTests.cs`
- Modify: `README.md`
- Modify: `docs/EXTENDING.md`

**Version contract:** `Version=1.3.0`、`FileVersion=1.3.0.0`、`AssemblyVersion=1.0.0.0`；publish 的 InformationalVersion 为 `1.3.0+<40-hex-clean-commit>`。

- [ ] RED：先把 ReleaseContract 期望改为 1.3.0/1.3.0.0 并运行 Smoke，确认唯一新增失败准确捕获当前 1.2.2。
- [ ] GREEN：同步 csproj、manifest、release script、release notes path、ZIP 名、QA 标题和候选启动页；AssemblyVersion 不变。
- [ ] GREEN：README 增加 BROWSE 使用、类型优先级、网站/失效 video 限制、当前/选择分离、隐藏选择、键盘、Compact、预览预算、问题跳转、CLI `--page browse` 和截图。
- [ ] GREEN：EXTENDING 记录 ProjectSnapshot/FrozenRequest/ProjectKey/preview adapter 边界，明确不能绕过安全 request 或扩大 preview 预算。
- [ ] GREEN：release notes 列用户行为、兼容/限制、A11Y/perf 证据、unsigned、本地 RC 和未验证项；无 TODO/TBD。
- [ ] 运行 restore、非增量 Release build、完整 Smoke、故意失败入口、PowerShell parser、release contract、许可/notices 路径、`git diff --check`。
- [ ] 不在本任务提交内运行 `-UpdateTrackedExecutable`；实际 clean RC 在整分支审阅和合并后由 controller 执行。
- [ ] Commit: `release: prepare Wallpaper Field v1.3.0`

## Final Branch Review and Merge Procedure

实施任务全部逐项 review clean 后：

1. 从 `ab5123d..HEAD` 生成整分支 review package；使用最强可用 reviewer 对照本 Spec、所有 task reports、ledger rulings、调用者、取消/失败/磁盘事实、预览预算、A11Y、性能和发布完整性。
2. 若有 finding，只允许一个集中 fix wave 和一个 scoped re-review；Critical/Important/Blocker/Major 必须为零，残余项必须有显式 ruling。
3. 在 feature branch 运行 restore、非增量 Release build、完整 Smoke、故意失败出口、`dotnet test` 发现限制、PowerShell parser、依赖 vulnerability/deprecated/outdated、RePKG compile-surface、许可、最终截图与 `git diff --check`。
4. 确认 feature worktree clean，主工作树仍只有用户最初 EXE/AGENTS 状态；用非破坏性 `git merge --no-ff codex/v1.3.0-project-browser` 合并本地 main，不 rebase/reset/push/tag。
5. 从 merge commit 创建 clean 隔离验证 worktree，重新运行完整门禁。
6. 在 clean merged-main worktree 运行 `build-release.ps1` 到 `D:\CSC Project\GUI-for-RePKG\temp\maintenance-audit\v1.3.0-rc`，绝不传 `-UpdateTrackedExecutable`。
7. 验证 ZIP 精确包含 EXE、v1.3.0 release notes、根 LICENSE/notices、RePKG license/notices/patches、manifest、SBOM/dependencies 与 SHA-256；核对 FileVersion/ProductVersion/commit 和诚实 `NotSigned`。
8. 最终核对 merge commit、main status、用户文件 hash/身份、RC、完整测试摘要和未验证项。

## Final Acceptance

- 本地 `main` 包含 v1.3.0 merge commit；功能提交没有根 EXE、AGENTS、temp、bin/obj 或目录重组。
- 四页路由、ProjectSnapshot、共享选择、虚拟化网格、详情、processing tray、问题跳转和 preview budget 全部有 fresh tests。
- SafePackageReader、事务、取消/关闭、Committed-only 清选和旧三页回归保持。
- 非增量 Release build 0 warning/0 error；Smoke 正数断言 exit 0；故意失败 exit 非零。
- 1000 项交互/滚动、UI I/O、decode/cache/queue 指标全部在预算内；自动化视觉矩阵无 Blocker/Major。
- 版本、manifest、README、release notes、截图、RC ZIP/SBOM/licenses/hash 与 merge commit 一致；根历史 EXE 未改变。
- 最终 review Critical/Important/Blocker/Major=0；真实环境未验证项和全部 rulings 明确交付。
