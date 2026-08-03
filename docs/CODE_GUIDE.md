# FIELD STATION 逐文件代码说明

本文解释每个手写文件为什么存在、依赖谁，以及修改它会影响什么。

## 1. 总体依赖方向

```text
Contracts
    ↑
Backend implementation
    ↑
ViewModels ← MVVM primitives
    ↑
Views ← Theme / Controls / Motion policy
    ↑
Shell / App / Composition
```

依赖只能大体向上流动：`Contracts` 不知道 WPF；View 不知道 Demo backend；只有组合根知道具体实现。

运行时数据流：

```text
用户点击 Button
  → ICommand
  → Page ViewModel
  → IOperationsBackend
  → SnapshotChanged
  → UiThread
  → ViewModel 更新
  → XAML Binding 更新 UI
  → SmoothProgressBar 将数值变化动画化
```

## 2. 根目录文件

### `FieldStation.Maximal.csproj`

- 定义这是一个 `net10.0-windows` WPF 可执行程序。
- `UseWPF=true` 让 MSBuild 编译 XAML/BAML。
- `Nullable` 和 `ImplicitUsings` 减少空引用风险和样板代码。
- 如果更换框架版本、程序集名或发布模型，从这里改。

### `FieldStation.Maximal.slnx`

- 解决方案入口，目前只包含一个 UI 项目。
- 后续可以加入 `FieldStation.Core`、`Infrastructure` 和测试项目。
- 它不包含业务逻辑。

### `NuGet.Config`

- 清空包源，因为当前工程没有第三方包。
- 保证离线 restore 不尝试访问网络。
- 以后加入 NuGet 包时，需要重新配置可信源。

### `app.manifest`

- 声明 Windows 应用兼容性。
- 使用 `asInvoker`，不会自动要求管理员权限。
- 只有确有系统权限需求时才应更改。

### `.gitignore`

- 忽略 `bin/`、`obj/`、`.vs/` 和用户设置。
- 不忽略源码、文档、QA 图片或配置。

### `App.xaml`

- 合并 `MaximalTheme.xaml`。
- 用 `DataTemplate` 建立 ViewModel → View 映射。
- 它负责类型映射，不负责创建后端。
- 增加内置页面类型时在这里增加映射。

### `App.xaml.cs`

- WPF 程序启动入口。
- 依次执行组合、QA 状态种子、动效偏好、窗口创建和截图。
- `--snapshot`、`--page`、`--state`、`--width`、`--height` 在这里协调。
- 不应把页面业务逻辑写入这里。

### `MainWindow.xaml`

- 全局壳层的唯一视觉所有者。
- 包含桌面 rail、标题栏、全局背景层、页面容器、过渡遮罩、紧凑底栏和状态基线。
- `PageContent` 只承载当前页面，不知道页面内部布局。
- 全局工程网格、校准环和状态 ticker 在此处，因为它们跨页面持续存在。

### `MainWindow.xaml.cs`

- 创建 `ShellViewModel`。
- 监听 `CurrentPage` 变化并启动页面擦除。
- 小于 1000px 时隐藏桌面 rail、显示底部导航。
- 管理无边框窗口拖动、最小化、最大化和关闭。
- `PrepareQaState` 只用于非持久化视觉验证。

## 3. Contracts

### `Contracts/IOperationsBackend.cs`

这是整个重构中最重要的文件。

- `IOperationsBackend`：前端可调用的后端能力。
- `OperationsSnapshot`：某一时刻完整且不可变的前端读模型。
- `WorkUnit`：总控队列拥有的数据。
- `RouteNode`：拓扑图和节点 dossier 的数据。
- `AssetRecord`：资产档案的数据。
- `ReportPoint`：报告图表的真实数值。
- `OperationResult`：命令执行结果。

这些类型都是 record，便于原子替换状态、测试和线程间传递。这里不引用 `System.Windows`。

## 4. Composition

### `Composition/AppComposition.cs`

- 唯一知道“现在使用 Demo backend 还是真实 backend”的文件。
- 唯一推荐注册 Region 和整页扩展的位置。
- `CreateBackend()` 是替换后端时通常唯一需要修改的方法。
- 不应在 View 或 ViewModel 中直接 `new ProductionBackend()`。

## 5. Extensibility

### `Extensibility/RegionRegistry.cs`

- 保存 `RegionKey → FrameworkElement 工厂`。
- 使用工厂而不是单例控件，因为同一个 WPF Visual 不能有两个父级。
- 适合插入图表、文件树、编辑器或配置控件。

### `Extensibility/PageRegistry.cs`

- `PageContribution` 描述导航 key、序号、中英文标签和页面工厂。
- `PageRegistry` 检查重复 key。
- 适合增加完整业务页面。
- 新页面会由 `ShellViewModel` 自动加入两套导航。

## 6. Services

### `Services/DemoOperationsBackend.cs`

- 内存中的 `IOperationsBackend` 示例。
- `StartCycleAsync()` 每 72ms 推进一次进度并发出 `SnapshotChanged`。
- `StopCycleAsync()` 通过 `CancellationTokenSource` 停止流程。
- 不读取、不写入、不删除文件。
- `SetQaRunningState()` 只生成确定性的视觉测试状态。
- 接入生产环境后可以保留它用于设计时和测试。

### `Services/MotionSettings.cs`

- 所有动画共用的单一策略开关。
- 初值遵循 Windows 的客户端动画设置。
- 用户点击“动态模式/静态模式”时修改它。
- 新动画必须先检查这个类，不能自己绕开减少动态效果。

### `Services/MotionDirector.cs`

- `Reveal()`：页面进入时的位移和透明度揭示。
- `PageTransition()`：信号黄方向擦除和页面切换。
- `StartAmbient()`：网格漂移、校准环旋转、在线信标呼吸、ticker 漂移。
- `StopAmbient()`：移除持续动画并恢复清晰静态状态。
- 集中管理时长和 easing，避免每页出现互不协调的 Storyboard。

### `Services/VisualSnapshotService.cs`

- 解析 QA 命令行参数。
- 使用 `RenderTargetBitmap` 渲染窗口，不依赖屏幕截图坐标。
- 强制输出确定尺寸 PNG。
- 不调用写盘型业务流程；唯一写入是用户指定的 PNG。

## 7. Controls

### `Controls/RegionHost.xaml`

- 未注册扩展时显示清晰的标题、用途和 Region key。
- 让扩展空间是产品的一部分，而不是不可见的注释。
- 默认适合深色背景。

### `Controls/RegionHost.xaml.cs`

- 定义 `RegionKey`、`Title`、`Description` 三个依赖属性。
- Loaded 时向 `RegionRegistry` 请求控件。
- 有控件就显示控件，没有就显示占位说明。

### `Controls/SmoothProgressBar.cs`

- 增加 `TargetValue` 依赖属性。
- 数据变化时用 360ms ease-out 平滑更新 `Value`。
- 减少动态效果模式下直接跳到最终值。
- 它只负责数值反馈，不负责业务进度计算。

## 8. Theme

### `Themes/MaximalTheme.xaml`

包含整个视觉系统：

- Family tokens：炭黑、纸白、信号黄、在线绿和错误红。
- 字体角色：UI、Display、Mono。
- 文本层级：`MicroLabel`、`PageTitle`、`HeroTitle`。
- 面板：`DarkPanel`、`PaperPanel`。
- 操作：`PrimaryButton`、`OutlineButton`、`UtilityButton`。
- 导航：`RailItem`、`BottomNavItem`。
- 输入、进度条和滚动条。
- 直接交互的 hover/focus/pressed 状态。

重做颜色、字体、按钮和公共几何时先改这个文件。页面不应大量硬编码新的品牌颜色。

## 9. ViewModels 基础设施

### `ViewModels/ObservableObject.cs`

- 实现 `INotifyPropertyChanged`。
- `SetProperty` 只在值真正变化时通知 UI。

### `ViewModels/Commands.cs`

- `RelayCommand` 包装同步操作。
- `AsyncRelayCommand` 包装异步操作并防止重复执行。
- `RaiseCanExecuteChanged` 让按钮在运行状态变化时重新判断可用性。

### `ViewModels/UiThread.cs`

- 后端可能从工作线程发出快照。
- 此类把 ObservableCollection 和绑定属性修改调度回 WPF UI 线程。
- 真实后端不得假设事件一定在 UI 线程发出。

### `ViewModels/UiModels.cs`

- 保存纯 UI 形状所需的数据。
- 例如 `ReportBar.Height` 是报告值转换后的像素高度。
- 它们不替代后端 record，而是页面级显示模型。

## 10. 页面 ViewModels

### `CommandCenterViewModel.cs`

- 拥有计划选择、开始、停止、总体进度、指标和工作单元。
- 订阅后端快照并同时更新总体与单元状态。
- 不负责绘制圆环、网格或按钮。

### `TopologyViewModel.cs`

- 将后端节点映射到画布位置。
- `SelectedNode` 是图与 dossier 之间唯一的选择状态。
- 完整机制或编辑器应放进 `topology.detail`，不要复制到每个节点。

### `AssetArchiveViewModel.cs`

- 保存搜索词、分类筛选和选中资产。
- `_all` 是完整源，`Assets` 是过滤后的可观察集合。

### `ReportsViewModel.cs`

- 将真实 `ReportPoint` 转换为柱高和目标线位置。
- 计算平均值、峰值和达标率。
- 没有制造额外仪表或虚构数据。

### `ExtensionsViewModel.cs`

- 描述六个扩展入口的 key、类型和推荐用途。
- 本身不加载插件。

### `ShellViewModel.cs`

- 创建五个内置页面 ViewModel。
- 合并 `PageRegistry` 的外部整页贡献。
- 管理当前导航、工作区、全局模式、紧凑状态和动效偏好。
- 它不处理任何页面内部决策。

## 11. Views

每个 `*.xaml.cs` 只调用 `MotionDirector.Reveal()`，没有业务代码。

### `Views/CommandCenterView.xaml(.cs)`

- 构筑/总控模式的独立舞台。
- 主舞台拥有执行决定和总体进度。
- 计划 dock 拥有执行参数与最近结果。
- 工作单元列表拥有每个单元的状态。
- `command.secondary` 预留次级仪表。

### `Views/TopologyView.xaml(.cs)`

- 探索/路由模式的独立舞台。
- 图拥有节点选择和路径关系。
- 右侧 dossier 只拥有选中节点的额外信息。
- 通过 ListBox 保留键盘选择能力。

### `Views/AssetArchiveView.xaml(.cs)`

- 资产模式反转为浅色主舞台和深色记录矩阵。
- 选中资产拥有大标题、状态、版本和更新时间。
- 搜索和分类改变右侧记录集合。

### `Views/ReportsView.xaml(.cs)`

- 报告模式让浅色图表成为主内容。
- 图表拥有逐日比较；右侧 dossier 只拥有汇总。
- `reports.annotation` 预留审批、导出和解释。

### `Views/ExtensionsView.xaml(.cs)`

- 展示三条扩展边界及所有命名插槽。
- `extensions.canvas` 可被完整业务工具接管。

## 12. artifacts、bin 和 obj

### `artifacts/*.png`

- 视觉验收产物，不参与编译。
- 用于比较桌面、紧凑、运行和过渡状态。

### `bin/`

- 最终 exe、dll、pdb 和运行配置。
- 每次构建可重新生成，不要手动修改。

### `obj/`

- NuGet 资产、生成的 XAML C#、BAML 和临时项目。
- 不属于源码，不要复制到新项目。
