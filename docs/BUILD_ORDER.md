# 从零编写同类 C# 软件前端的顺序

## 先回答“第一个文件是什么”

- 从空目录实际创建工程：先写 `FieldStation.Maximal.csproj`。
- 开始设计软件架构：先设计 `Contracts/IOperationsBackend.cs`。
- 只重做视觉：先改 `Themes/MaximalTheme.xaml`。
- 不推荐从 `App.xaml` 或某个 `View.xaml` 开始，它们会被数据边界倒逼返工。

下面是推荐的严格书写顺序。每一阶段都应先构建通过，再进入下一阶段。

## 阶段 0：建立可编译的空 WPF 工程

### 第 1 步：`FieldStation.Maximal.csproj`

写入：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

### 第 2 步：`App.xaml` 与 `App.xaml.cs`

先保持最小，不加载复杂资源。

### 第 3 步：空 `MainWindow.xaml` 与 `MainWindow.xaml.cs`

只显示一个普通窗口。

### 第 4 步：`app.manifest`、`NuGet.Config`、`.gitignore`

检查点：

```powershell
dotnet restore .\FieldStation.Maximal.csproj --configfile .\NuGet.Config
dotnet build .\FieldStation.Maximal.csproj
```

必须先得到 0 错误的空窗口。

## 阶段 1：先定义前端与后端的语言

### 第 5 步：`Contracts/IOperationsBackend.cs`

按页面决策定义数据：

1. 主屏需要哪些总体状态？
2. 拓扑节点必须显示什么？
3. 资产可以搜索哪些字段？
4. 报告的真实数值是什么？
5. 用户可以执行哪些命令？

不要把数据库实体或 RePKG 内部对象直接暴露给 View。

### 第 6 步：先写一个最小 `DemoOperationsBackend`

最初只需要：

```csharp
public Task<OperationsSnapshot> GetSnapshotAsync(...)
    => Task.FromResult(sampleSnapshot);
```

检查点：单独确认所有 record 都可构造，后端无需 WPF。

## 阶段 2：建立 MVVM 基础

依次写：

7. `ViewModels/ObservableObject.cs`
8. `ViewModels/Commands.cs`
9. `ViewModels/UiThread.cs`
10. `ViewModels/UiModels.cs`

检查点：创建一个临时 ViewModel，属性改变能够通知 UI；异步命令不能重复执行。

## 阶段 3：建立三条扩展边界

依次写：

11. `Extensibility/RegionRegistry.cs`
12. `Extensibility/PageRegistry.cs`
13. `Composition/AppComposition.cs`

此时应形成三个明确入口：

```text
IOperationsBackend → 更换业务
RegionRegistry     → 加局部控件
PageRegistry       → 加完整页面
```

检查点：View 或 ViewModel 中不能出现 `new DemoOperationsBackend()`。

## 阶段 4：先做设计系统，不要先画页面

### 第 14 步：`Themes/MaximalTheme.xaml`

按下面顺序定义：

1. 语义颜色：Ink、Paper、Signal、Online、Danger。
2. 字体角色：UI、Display、Mono。
3. 文本层级。
4. DarkPanel 与 PaperPanel。
5. Primary、Outline、Utility 按钮。
6. 焦点、hover、pressed、disabled。
7. 输入、进度、滚动条。
8. 桌面和底部导航样式。

检查点：创建一张临时控件陈列页，确认键盘焦点可见、文本对比可读。

## 阶段 5：建立动效规则

依次写：

15. `Services/MotionSettings.cs`
16. `Services/MotionDirector.cs`
17. `Controls/SmoothProgressBar.cs`

先定义动效语义：

```text
Reveal          页面层级揭示
PageTransition 页面切换
Ambient         持续状态
Direct feedback 按钮和进度反馈
```

每种动画都必须有减少动态效果的最终静态状态。

不要让每个页面自己发明时长和 easing。

## 阶段 6：只完成一个纵向切片

依次写：

18. `CommandCenterViewModel.cs`
19. `CommandCenterView.xaml`
20. `CommandCenterView.xaml.cs`
21. 在 `App.xaml` 添加 DataTemplate

这一条切片必须完整贯通：

```text
Button
→ AsyncRelayCommand
→ IOperationsBackend.StartCycleAsync
→ SnapshotChanged
→ ViewModel
→ ProgressBar
```

检查点：点击开始后，总体和单元进度都必须改变；点击停止后命令可以取消。

## 阶段 7：增加其余业务模式

按一页一个检查点完成：

22. `TopologyViewModel.cs`
23. `TopologyView.xaml/.cs`
24. `AssetArchiveViewModel.cs`
25. `AssetArchiveView.xaml/.cs`
26. `ReportsViewModel.cs`
27. `ReportsView.xaml/.cs`
28. `ExtensionsViewModel.cs`
29. `ExtensionsView.xaml/.cs`

每一页必须有不同的主内容所有者：

- 总控：执行决定。
- 拓扑：路由图。
- 资产：选中记录。
- 报告：数据比较。
- 扩展：能力边界。

不要复制同一个 Dashboard Grid 后只换标题。

## 阶段 8：最后组装全局 Shell

依次写：

30. `ShellViewModel.cs`
31. `MainWindow.xaml`
32. `MainWindow.xaml.cs`

顺序原因：只有页面、导航描述和动效语义稳定后，才能判断 Shell 需要承载什么。

先完成桌面 rail，再增加底部导航。小窗口应重排导航，不只是缩小 rail。

检查点：

- 桌面 1600×960。
- 紧凑 900×900。
- 键盘可以选择导航和节点。
- 页面切换不遮挡主操作。
- 静态模式仍然清楚。

## 阶段 9：接入 RegionHost

依次写：

33. `Controls/RegionHost.xaml`
34. `Controls/RegionHost.xaml.cs`
35. 把 RegionHost 放到各页面的次级区域。

检查点：注册一个测试控件，确认它替换占位内容；不要让一个控件实例进入两个父级。

## 阶段 10：QA、文档和 Release

依次写：

36. `Services/VisualSnapshotService.cs`
37. `README.md`
38. `docs/CODE_GUIDE.md`
39. `docs/BUILD_ORDER.md`

执行：

```powershell
dotnet build .\FieldStation.Maximal.slnx --configuration Release
```

至少验证：

1. 五个页面的桌面截图。
2. 900px 紧凑截图。
3. `--state running`。
4. `--state transition`。
5. 正常模式下持续动画可以运行。
6. 静态模式下持续动画停止。

## MSBuild 自己的实际顺序

以上是人类最适合的书写顺序。编译器并不会逐个按文件名编译 C#，而是：

1. 读取 `.slnx` 和 `.csproj`。
2. restore 并生成 `project.assets.json`。
3. 编译 XAML，生成临时 C# 和 BAML。
4. 把所有手写 C# 与生成代码作为一个项目编译。
5. 输出 exe、dll、pdb、deps.json、runtimeconfig.json。

因此，当编译错误指向 `*_wpftmp.csproj` 时，通常仍然是你的 XAML 或它引用的 C# 类型有问题。

## 不同重构目标从哪里开始

| 目标 | 起点 |
|---|---|
| 完全从空目录重写 | `FieldStation.Maximal.csproj` |
| 重新设计业务边界 | `Contracts/IOperationsBackend.cs` |
| 接入真实后端 | `IOperationsBackend` → `AppComposition` |
| 更换整套视觉 | `MaximalTheme.xaml` → `MainWindow.xaml` → 各 Views |
| 改页面布局 | 对应 ViewModel → 对应 View |
| 增加局部工具 | `RegionRegistry.Register(...)` |
| 增加完整页面 | `PageRegistry.Register(...)` |
| 改所有动效 | `MotionSettings` → `MotionDirector` |

如果你是第一次写 WPF，建议先实现阶段 0～6，只做一个完整总控页面。确认数据、命令、绑定和线程都正确后，再追求 maximal 的页面级编排。
