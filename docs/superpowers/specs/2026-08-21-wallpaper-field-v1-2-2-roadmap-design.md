# Wallpaper Field v1.2.2 完整维护路线图设计规格

- 状态：五节设计与书面规格均已获用户批准
- 日期：2026-08-21
- 起始基线：`main@61a129c855c17b745c048b6841acb96a449eadc1`
- 目标版本：`v1.2.2`
- 实施分支：`codex/v1.2.2-roadmap`
- 关联工作包：WP5–WP11

## 1. 背景与决策摘要

v1.2.1 已建立安全 PKG reader、完整输出规划、事务提交、TEX 预算、真实 Smoke 入口和可追溯发布候选。本轮把原路线图剩余 WP5–WP11 统一交付为 v1.2.2，同时保持补丁版本兼容性。

本设计采用“行为优先、稳定后拆分”的路径：先以失败测试证明并修复 WP5–WP8，通过稳定性总门禁后，再拆 WP9–WP10，最后以 WP11 收窄上游面和删除有证据的演进残留。这样可以把行为变化与结构移动分开审阅。

用户已经批准以下核心决定：

- 问题中心是“扫描 / 图库 / 问题”中的第三个常驻导航页，不是抽屉或覆盖层。
- `ShellViewModel` 只负责导航、跨页只读投影和窗口级协调。
- 扫描、解包、图库和问题分别拥有唯一 session 状态；任务协调器不保存领域数据。
- 关闭过程等待安全清理，不强杀 commit 或 rollback。
- UI 保留黑、白、Signal Yellow 工程视觉，并系统性补齐响应式、键盘、High Contrast 和 reduced-motion。
- 最终工作必须经过固定起点 diff 审阅、审计修复循环、合并 `main` 和合并后新鲜验证。

## 2. 目标与非目标

### 2.1 用户结果

- 扫描、解包和图库刷新均可见、可键盘取消，关闭期间不会先于安全清理退出。
- 失败或取消保留上一可用快照与旧输出；成功状态严格对应磁盘提交事实。
- 所有问题可筛选、浏览、复制和导出，失败说明磁盘状态与下一步动作。
- 路径、CLI 和 JSON 输入在危险工作前得到统一、可行动的验证。
- 920×680 至宽屏、100%–200% DPI 下不叠字、不遮挡、不丢唯一操作入口。
- 应用状态、页面和主题被拆成可理解、可独立验证的深模块。
- 产品 PKG 路径只经过第一方 adapter 与 `SafePackageReader`。
- 文档、源码版本、FileVersion、ProductVersion、manifest、ZIP 和 hash 统一为 v1.2.2。

### 2.2 非目标

- 不更换 WPF/UI 框架，不重做品牌，不整体重写 MVVM。
- 不改变既有合法 settings/metadata 格式或对外服务契约；新增字段只能是有默认值的 append-only 扩展。
- 不升级 ImageSharp 等跨主版本依赖，不批量整理 ThirdParty 风格或 warning。
- 不移动历史 tag，不 push，不创建远程 release/tag，不使用签名秘密。
- 不覆盖、删除、提交用户在任务开始前已有的根 `GUI_for_RePKG.exe` 修改或未跟踪 `AGENTS.md`。

## 3. 不变量与信任边界

Workshop 路径、`project.json`、`metadata.json`、PKG、TEX、预览、视频、启动参数、现有输出及攻击者可控诊断文本全部是不可信输入。

以下不变量在每个阶段持续回归：

- 产品 PKG 主路径只用 `SafePackageReader`，不回退到 eager reader/writer。
- 扫描只读；解包只通过完整规划、staging、commit/rollback 写盘。
- 取消或失败后旧输出 byte-for-byte 不变，不留下 stage、backup 或混合树。
- 成功只在越过事实提交边界后报告；不确定磁盘状态必须明确标识。
- TEX 的长度、像素、帧、累计预算和 ImageSharp 所有权继续受控。
- 扫描和图库列表继续使用回收虚拟化；GIF 离屏、卸载和 reduced-motion 生命周期不退化。
- 正确性、数据完整性、安全、隐私、取消和无障碍优先于视觉润色与代码减行。

## 4. 系统边界与依赖方向

| 模块 | 唯一职责 | 主要依赖 |
|---|---|---|
| `ShellViewModel` | 导航、当前页面、全局状态摘要、跨页跳转 | 各 session 的只读投影 |
| `ScanSession` | 路径输入与验证、最后成功扫描快照、筛选、选择、扫描生命周期 | `WallpaperScanService`、统一路径验证、问题中心、任务协调器 |
| `UnpackSession` | 冻结批请求、解包进度、取消、提交状态、逐项结果 | `IWallpaperUnpackService`、问题中心、任务协调器 |
| `LibrarySession` | 输出根、最后成功图库快照、筛选、稳定刷新 | `WallpaperLibraryService`、统一路径验证、问题中心、任务协调器 |
| `ProblemCenterSession` | 结构化问题、筛选、选择、复制、导出和诊断动作 | 日志、文件/目录打开 adapter |
| `TaskLifecycleCoordinator` | 单一前台 I/O 槽、任务登记、取消、关闭等待 | 运行中 `Task` 与 CTS；不依赖领域模型 |
| `StartupOptions` | 表驱动解析与不可变解析结果 | 选项描述表、问题模型 |
| 统一输入验证模块 | 路径、reparse、重叠、设备名和 JSON 预算 | 深化现有 `OutputPathPolicy`，不复制策略 |
| 现有 service/安全模块 | 扫描、图库发现、PKG/TEX 解包和事务提交 | 现有契约与安全边界 |

依赖只从 UI/session 指向 service 和安全模块。Service 不引用 ViewModel、窗口或 WPF 控件。单一实现的 session 使用具体深模块，不为形式一致机械增加 interface、factory 或转发层。现有 I/O adapter 继续作为测试缝隙。

三个页面分别为 `ScanPageView`、`LibraryPageView` 和 `ProblemCenterView`。窗口壳只拥有导航、全局任务摘要和页面宿主，不拥有页面内部控件状态。

## 5. 核心契约

### 5.1 生命周期

统一生命周期状态为：

- `Idle`：无任务运行。
- `Running`：任务运行且允许请求取消。
- `CancellationRequested`：取消已接收，等待协作式退出和 `finally`。
- `CommitCritical`：正在执行极短 commit 或 rollback，不允许强制中断。
- `Succeeded`、`Failed`、`Cancelled`：与磁盘事实一致的终态。

协调器保持单一前台 I/O 槽，因此扫描、解包和图库刷新不会并发改变共享路径上下文。导航和问题浏览在任务运行时仍可用；会启动冲突任务的命令禁用。每个运行实例有唯一 operation ID、CTS 和可等待 `Task`。重复取消只设置一次请求，不抛异常，也不改变已经完成的终态。

若取消与自然完成竞态，终态由提交事实决定：已经成功提交的 item 是 `Committed`，不能伪称 `Cancelled`；提交前观察到取消的 item 不写目标。若取消在 `CommitCritical` 到达，记录待关闭/待取消请求，先完成 commit 或 rollback，再发布真实终态。

### 5.2 进度

统一进度投影包含 operation ID、阶段、当前 item、已完成工作量、可选总工作量、工作单位、是否 indeterminate、是否可取消和提交状态。

只有分母可靠时才计算百分比。解包优先使用已规划的 entry/physical bytes；规划前或跨包总量未知时使用真正 indeterminate，同时显示当前项目和阶段。项目数量可以作为批摘要，不能冒充单个大包的字节进度。

### 5.3 解包逐项结果

`WallpaperUnpackResult` 以 append-only 方式增加逐项结果集合。每项至少包含 WorkshopId、输出目标标识、结果类别、提交事实、已完成工作量和关联 issue code。旧汇总、error 和 warning 字段继续保留，现有调用者与序列化格式不被破坏。

解包从启动时冻结选择快照。只有 `Committed` 成功项从 `ScanSession` 选择中移除；失败、跳过、取消和未提交项继续保留以便重试。重复输出目标在开始前被结构化拒绝，不能靠结果映射猜测归属。

### 5.4 结构化问题

不可变 `AppIssue` 包含稳定 issue ID、code、severity、source、operation ID、时间、中文摘要、受控技术细节、磁盘事实、建议动作、可选路径上下文、resolution state 和可选 resolved time。严重度使用 Information、Warning、Error；来源至少覆盖 Startup、Scan、Unpack、Library、Settings 和 Diagnostics。重试成功时，来源 session 以新不可变记录把同 code/context 的旧问题标为 Resolved；“清除已解决问题”只删除这些记录，不删除仍开放的问题。

攻击者可控字符串在进入 UI、日志或导出前转义并截断。单条技术细节最大 4096 个字符。应用会话最多保留 10,000 条可见问题；超限记录按 source/code 聚合，并添加明确的省略数量问题，避免不可信输入造成无界 UI/内存增长。

### 5.5 输入验证

`PathValidationResult` 是命令可用性和输入旁反馈的同一事实来源，包含原始输入标识、规范化路径、严重度、稳定 code、用户消息和验证版本。UI 先同步验证语法，再以 250 ms latest-wins 方式检查存在性、可读性、写入能力预测、source/output 重叠及 existing-path reparse。只读扫描验证不得创建探针文件；输出可写性在 UI 中按现有路径/权限作预测，在解包开始时以事务 staging 创建作为权威检查。命令执行前对未变化的输入使用同一策略重新验证，防止验证后磁盘状态变化。

扫描、解包和图库不得各自复制路径策略。现有 `OutputPathPolicy` 被深化为共享语义所有者，统一处理大小写、尾点/空格、Windows 保留设备名、exact `NUL`、父子重叠和 junction/symlink。

`project.json` 与 `metadata.json` 在打开和解析前统一限制为 4 MiB。边界值允许，超限值逐项产生问题并跳过，不分配大缓冲，也不终止整个批任务。长度预检之后仍通过最大读取量为 4 MiB + 1 byte 的受限流读取；文件在检查后增长时也会受控失败，不能绕过预算。

## 6. 主要数据流

### 6.1 启动

`StartupOptions` 使用单一描述表解析参数。value 选项遇到 EOF 或下一个 `--flag` 时报告缺值且不消费该 flag。第一个合法选项值生效，后续重复项被忽略并产生 Warning；未知 token 只忽略自身，不消费后一项。缺值、非法数值和非法枚举产生 Error 并使用默认值。所有选项保持顺序无关，并由表驱动测试覆盖。

既有合法参数保持兼容，并增加 `--page problems`。宽高必须是有限、正数且不超过 16384；低于窗口最小值继续沿用现有夹紧行为。NaN、正负 Infinity、非数字和超大值产生启动问题并回退默认值，不再让 WPF 构造失败。非法参数不阻止普通 GUI 启动；存在启动 Error 时默认打开问题页。`--scan` 仍需通过统一路径验证才会自动执行。

启动参数不以原文写入日志。解析期间产生的问题暂存在不可变结果中，composition 完成后一次发布到问题中心。

### 6.2 扫描

扫描开始时冻结输入和验证结果，但继续显示最后成功快照。运行中状态覆盖在旧快照上，不清空列表。成功后一次替换 items、来源路径和时间；失败或取消只更新生命周期和问题。

旧快照记录 source/output identity。如果当前输入与快照 identity 不一致，旧内容仍可浏览，但解包命令禁用并解释原因。进度直接使用当前目录/item 身份，失败项不会回退到上一成功标题。

### 6.3 解包

`ScanSession` 生成不可变选中项快照，`UnpackSession` 验证输出目标唯一性后调用现有解包 service。Package planner 在写盘前继续枚举原始和派生输出并执行冲突/预算检查。进度来自实际规划和运行事实。

完成后 `ScanSession.ApplyItemResults` 只处理 operation ID 匹配的结果，避免旧任务迟到覆盖新状态。问题中心接收结构化逐项问题；摘要明确旧输出是否未变、已提交或回滚，以及用户下一步。

### 6.4 图库

图库刷新也保留最后成功快照，成功后原子替换。递归 metadata 发现作为向后兼容扩展规则继续保留，但所有候选先按规范化相对路径稳定排序。重复 WorkshopId 视为显式冲突：同一 ID 的全部候选都不进入图库，不依赖枚举顺序静默 first-wins；每组冲突进入问题中心，合法非冲突项继续加载。

文档明确递归扩展规则、跳过的解包器目录和 reparse 策略。metadata 同样使用 4 MiB JSON 预算并逐项隔离失败。

### 6.5 关闭

首次 `Closing` 将事件取消，设置 `IsClosing`，禁用新任务并请求协调器取消。窗口保留可见并显示“正在安全停止”；若处于 `CommitCritical`，显示“正在完成安全提交”。事件处理异步等待运行 `Task` 和清理，而不是阻塞 Dispatcher 或在 `Closed` 后才取消。

安全等待 30 秒仍未 quiescent 时，本次关闭结束，窗口恢复可操作并导航到问题中心，报告仍在运行的 operation、磁盘事实和可用动作；进程不被强制终止。清理成功后保存设置，再以内部 `closePrepared` 标志触发第二次 Close 并放行。

设置保存失败发布结构化问题并保留窗口，使用户能查看或复制诊断；用户再次主动关闭时可以在没有运行任务的前提下退出，避免设置故障把应用永久锁死。

## 7. 问题中心、日志与诊断

问题中心是完整问题集合的唯一 UI。扫描页和图库页只显示按来源聚合的数量、最高严重度和“查看全部问题”命令。页面提供严重度、来源和文本筛选，支持选中、复制所选、复制全部、清除已解决问题、打开日志目录、打开输出根、导出诊断和 About/版本。

“复制全部”明确复制整个当前问题集合，不受筛选影响；筛选只改变浏览结果。导出采用带 schema version 的 UTF-8 JSON，包含应用/文件版本、commit identity、OS、架构、DPI、High Contrast、motion/density 设置、结构化问题和聚合计数，不包含文件内容、PKG/TEX payload 或预览。导出前显示字段预览；完整本地路径默认排除，只有用户显式勾选后才包含。

日志单文件最大 2 MiB，最多保留 5 份，并删除超过 14 天的文件。轮转在追加前完成，使用锁避免同进程交错。默认把用户目录替换为稳定占位符，并使用短路径指纹关联同一位置；不记录完整 args、未经截断的异常、文件内容或攻击者长文本。日志事件使用 code、operation ID、计数、耗时、异常类型和 HResult 等最小字段。

日志、诊断导出、打开目录和设置保存必须返回可观察结果。失败发布内存问题，不递归尝试写入已经失败的日志目标。启动早期的日志失败先缓存，问题中心构造后再发布。

## 8. 页面、响应式与效率

窗口壳保留黑/白/Signal Yellow 视觉和左侧导航。三个页面均采用固定标题/操作区和独立可滚动列表；不再以页面级 `ScrollViewer` 包裹虚拟化 ListBox。

- 扫描页：路径验证、扫描/解包/取消、筛选与批量选择、进度、问题摘要、虚拟化卡片列表。
- 图库页：输出根、刷新/取消、筛选、统计、问题摘要和虚拟化列表。刷新动作与可隐藏统计彻底解耦。
- 问题页：筛选、复制/导出工具栏、虚拟化问题列表和诊断/About 动作。

响应式只由一个 `ShellLayoutMode` 表达：Wide 为 ≥1190 DIP，Regular 为 1060–1189 DIP，Compact 为 920–1059 DIP。窗口 code-behind 只把实际宽度映射到枚举；各视图通过触发器消费，不逐控件改 Visibility/Height。Compact 只隐藏装饰、次级英文和重复统计，不能隐藏刷新、取消、问题入口或主命令。

进度文字和数值使用独立 Grid 列。程序化列表定位只使用内部列表滚动宿主；页面标题、路径和操作区保持稳定。现有 Recycling、Pixel scrolling 和 page cache 必须保留。

筛选增加“仅可处理”“仅失败/有问题”，并提供“选择当前匹配”“清除选择”。隐藏项的选择继续保留，UI 同时显示选中数与当前匹配数。密度提供 Comfortable 和 Compact；新设置字段有默认值，旧 settings 自动使用 Comfortable。用户操作以中文为主，工程英文仅作为次级标签。

## 9. 无障碍与 motion

页面标题设置 `AutomationProperties.HeadingLevel`，位于正确的自动化阅读顺序中，但普通静态标题不进入 Tab 停靠。图标导航、刷新、取消、列表箭头及无文字按钮都有稳定中文 `AutomationProperties.Name`。Tab 顺序固定为导航、页面首个输入、主操作、筛选、列表、次级动作；可见状态与可执行命令保持一致。

普通主题焦点使用表面感知的深色外环与 Signal Yellow 内环，使至少一层在黑、白和黄色表面达到 3:1。High Contrast 下，文本、背景、边界、焦点、选择和禁用态改用 `SystemColors.WindowBrush`、`WindowTextBrush`、`HighlightBrush`、`HighlightTextBrush`、`GrayTextBrush` 等动态资源，不依赖固定品牌色表达语义。

系统动画设置或 `--reduced-motion` 任一禁用时，统一 motion policy 关闭页面、按钮、导航、卡片、输入、滚动条和进度 Storyboard。系统设置运行时变化会重新计算 policy。GIF 在 reduced-motion 下只解码/显示静态首帧，不必完整启动动画；离屏和卸载时继续释放资源。任务状态必须由文字、图标和 Automation 状态表达，不能只靠颜色、hover、Tooltip 或动画。

## 10. WP9 与 WP10 结构迁移

结构迁移只在 WP5–WP8 总门禁通过后开始。顺序为：

1. 从现有 Shell 提取 `TaskLifecycleCoordinator`，保持公开 ViewModel 行为不变。
2. 提取 `ScanSession`，让现有 Shell 暂时转发属性；特征测试通过后再移除转发。
3. 以同样方式提取 `UnpackSession`、`LibrarySession` 和 `ProblemCenterSession`。
4. 将 `LaunchOptions` 替换为表驱动 `StartupOptions` 结果，同时保留合法参数语义。
5. Shell 收敛到导航、全局摘要和页面实例。
6. 依次提取 Scan、Library、ProblemCenter 三个 UserControl，每次只移动一个页面并验证绑定、命令、滚动、虚拟化和 GIF。
7. 最后拆主题字典，避免同时移动状态和资源。

主题合并顺序固定为 `Tokens → Accessibility/Motion → BaseControls → DomainComponents`。可覆盖颜色使用 `DynamicResource`；稳定结构尺寸可以使用 `StaticResource`。每次字典拆分后运行真实 WPF host，验证启动时没有缺失 key、错误覆盖或 High Contrast 回退。

目标是深模块而非文件数量：session 公开少量用例方法和不可变/可观察投影，内部隐藏状态转换；UserControl 拥有完整页面布局，不把每段 XAML 拆成无语义小控件。

## 11. WP11、图库与性能处置

第一方 adapter 保持 RePKG 的唯一产品入口。通过全仓引用图和固定 PKG/TEX fixture 证明后，删除 `ScanStage.CopyingPreview/SavingMetadata/WritingIndex`、`WallpaperIndex`、旧索引常量和无调用者 helper。删除作为独立小提交，不与功能修复混合。

对 UP-003 进行两个可重复实验：当前 compile 集的引用/产物基线，以及只包含产品所需 RePKG 源文件的 whitelist 构建。比较 build、Smoke、fixture、publish 内容、许可和维护成本。只有 whitelist 减少实际编译输入、所有固定/动态调用与发布检查通过、许可文件不减少，而且清单能由脚本验证而非靠人工逐文件维护时才实施；任一条件不满足就保留当前编译范围，并在审计中记录“不实施”的实验命令、结果和风险。不会为收窄上游面直接重写第三方实现。

PERF-001 先测量后决定。用 1000-card fixture 记录首次呈现、筛选、连续滚动帧时间和 UI 线程文件系统调用。若非首次加载交互 p95 超过 200 ms、连续滚动 frame-time p95 超过 33.3 ms，或 getter/滚动期间发生任何 `File.Exists`/目录遍历，则把预览存在性和缺失统计缓存进扫描/图库快照，并在密集列表状态移除每卡阴影。若没有达到触发条件，不做推测性优化，而以测量证据关闭风险。

LIB-001 通过稳定排序与显式重复冲突关闭。LIB-002 选择兼容方案：保留递归扩展发现并正式文档化，而不是在补丁版本突然限制为 `<id>/metadata.json`。

## 12. 测试与验证策略

每个行为修复遵循 RED → GREEN → REFACTOR：先运行新测试确认因预期缺陷失败，再做最小实现，再运行定向和完整 Smoke。测试沿用现有 console harness 与分文件 `RegressionTests` 组织，不为路线图引入新测试框架依赖。

| 层级 | 必须证据 |
|---|---|
| 生命周期 | 自然完成、取消、重复取消、服务异常、迟到回调、关闭竞态、commit 中关闭、30 秒超时协议 |
| 磁盘 | stage/backup 不残留；失败/取消前后目录 SHA-256 相同；第 N 个 move 故障注入 |
| 扫描/图库 | 旧快照保留、成功原子替换、来源 identity、稳定重复冲突、JSON 边界 |
| 解包 | entry/bytes 与 indeterminate、逐项 commit 事实、成功取消选择、失败保留、重复目标拒绝 |
| CLI/路径 | 缺值、未知、重复、乱序、NaN、Infinity、超大值、重叠、reparse、尾点/空格、大小写和 exact NUL |
| 问题/日志 | 0/1/50/1000/10001 条、筛选、复制、导出、预算聚合、轮转、保留期、隐私占位和 I/O 失败 |
| WPF | 三页绑定、AutomationName、Heading、Tab 顺序、列表内部定位、资源合并、GIF 生命周期 |
| 视觉 | 920×680、1060 断点、1600×1000；100%、150%、200% DPI；普通、High Contrast、reduced-motion |
| 安全回归 | SafePackageReader、输出规划、事务、TEX 预算/LZ4/所有权、固定恶意 fixture |
| 发布 | 版本一致性、ZIP 内容、许可/notices、SBOM、SHA-256、签名状态和 clean commit identity |

1000 条问题的模型加载、筛选和复制格式化各自在基线机器五次运行的最慢值不超过 500 ms；WPF host 在 1 秒内到达首个空闲 Dispatcher。实际耗时写入验证报告，而不是只记录“流畅”。

标准命令为：

```powershell
dotnet restore WallpaperField.slnx --configfile NuGet.Config
dotnet build WallpaperField.slnx -c Release --no-restore
dotnet run --project tests/WallpaperField.SmokeTests/WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore
dotnet test WallpaperField.slnx -c Release --no-build --no-restore
git diff --check
```

`dotnet test` 当前发现 0 项，不能单独作为通过证据。除非本任务以有证据的最小迁移让标准 runner 执行非零测试，否则该限制继续如实报告；真实门禁始终解析 Smoke 机器摘要并要求 tests、assertions 大于零，同时执行 `--verify-failure-exit` 自检。

无法在当前环境真实执行的屏幕阅读器、特定物理 DPI、签名、SmartScreen 或干净 VM 项必须列为未验证，不得从静态检查推断通过。审计 PASS 表示所有本任务可执行门禁通过且 Blocker/Major 为零，不把环境外证据伪装为已通过。

## 13. 文档、版本与本地发布候选

项目 Version 更新为 1.2.2，FileVersion 为 1.2.2.0，AssemblyVersion 继续保持 1.0.0.0 以维持兼容。README、扩展规则、CLI、问题中心、辅助功能、发布说明、截图和审计状态同步更新。DOC-001 使用本版本实际构建生成成功、问题和 Compact 状态截图替换旧 v1.1 图。

正式本地候选从合并后 `main` 的 clean commit 构建，输出到 `temp/` 隔离目录。分发 ZIP 至少包含 EXE、根 LICENSE、ThirdParty notices、RePKG license、版本 manifest、完整依赖/SBOM 和 EXE/ZIP SHA-256。manifest 记录源码 commit、版本、RID、构建命令、文件 hash 和真实签名状态。没有签名时明确写 `NotSigned`。

不调用会覆盖根受控 EXE 的发布选项。用户已有根 EXE 在分支、合并和最终验证前后都以大小、SHA-256、FileVersion/ProductVersion 和 Git 状态核对。

## 14. 审阅、提交与合并协议

所有实现提交位于 `codex/v1.2.2-roadmap`。提交按行为测试、WP5、WP6、WP7、WP8、稳定性门禁、WP9、WP10、WP11、文档/版本分组，避免把大规模移动与行为修复混在同一提交。

最终审阅使用起始 HEAD `61a129c855c17b745c048b6841acb96a449eadc1` 作为固定点，检查需求覆盖、所有调用者、失败/取消路径、磁盘提交边界、隐私、可访问性、ThirdParty 差异、许可、最终 diff 和用户已有改动。发现按 Blocker、Major、Minor 分类；反复修复和新鲜复验，直到 Blocker/Major 为零并生成 `Verdict: PASS` 报告。

完整门禁通过后，把工作分支合并到 `main`。合并不得暂存、提交、覆盖或删除用户已有 `GUI_for_RePKG.exe` 与 `AGENTS.md`。合并后从新 `main` HEAD 创建独立 clean 验证工作树，重新执行 restore、Release build、Smoke、失败退出、静态/依赖/许可、发布候选和 hash 检查。最终交付只报告实际执行结果、明确未验证项和剩余风险。

## 15. 完成判定

以下条件同时成立才算 v1.2.2 路线图完成：

- WP5–WP11 的用户结果和风险 ID 均有代码、测试或明确实验结论。
- 所有行为阶段和结构阶段分别审阅，稳定性总门禁阻止未稳定行为进入 WP9。
- 事务、取消、关闭、问题、CLI、路径、JSON、响应式、键盘、High Contrast、motion、图库稳定性和上游边界均有新鲜证据。
- 文档和版本统一；本地候选来自合并后 clean commit，内容和 hash 可追溯。
- 最终审计报告为 `Verdict: PASS`，Blocker/Major 为零。
- 工作分支已合并到 `main`，合并后验证通过；用户起始 EXE 与 `AGENTS.md` 状态保持原样。
