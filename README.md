# FIELD STATION / Maximal Frontend

一个原创、可离线构建的 C# / .NET 10 / WPF 软件前端。视觉契约为：

- Family：`endfield`
- Depth：`4 / maximal / 极繁`
- Evidence pattern：浅色纵向 rail、炭黑工具坞、信号黄动作、分段舞台、超大索引、方向性擦除
- Product task：选择工作计划、启动或停止流程、检查拓扑、资产和真实报告数据

本工程是 clean-room 的 Hypergryph-inspired 实现，没有复制或分发游戏 Logo、角色、美术、
生产代码、网页资源或专有字体。

## 运行

```powershell
dotnet restore .\FieldStation.Maximal.csproj --configfile .\NuGet.Config
dotnet run --project .\FieldStation.Maximal.csproj
```

没有第三方 NuGet 依赖。`NuGet.Config` 清空外部包源，因此工程可以离线还原和构建。

## 当前功能

- 五个独立导演的模式：总控、拓扑、资产、报告、扩展。
- 1600×960 桌面 rail；小于 1000px 时重排为底部导航。
- 黄色页面擦除、页面方向揭示、网格漂移、校准环旋转、在线信标呼吸、ticker 漂移。
- 一键“静态模式”，同时停止持续动效并将页面揭示降级到最终状态。
- 模拟后端可真实推进任务和单元进度，但不会访问用户文件。
- 搜索、筛选、节点选择、资产选择、计划选择、启动和停止命令。
- `IOperationsBackend`、`RegionRegistry`、`PageRegistry` 三条互相独立的扩展边界。
- 非持久化 QA 入口，可复现稳定、运行中和页面过渡状态。

## 后端接入

实现 [Contracts/IOperationsBackend.cs](Contracts/IOperationsBackend.cs)：

```csharp
public sealed class ProductionBackend : IOperationsBackend
{
    public string ProviderName => "PRODUCTION";
    public event EventHandler<OperationsSnapshot>? SnapshotChanged;

    public Task<OperationsSnapshot> GetSnapshotAsync(CancellationToken token = default)
        => LoadYourSnapshotAsync(token);

    public Task<OperationResult> StartCycleAsync(string planId, CancellationToken token = default)
        => StartYourRealOperationAsync(planId, token);

    public Task<OperationResult> StopCycleAsync(CancellationToken token = default)
        => StopYourRealOperationAsync(token);
}
```

然后只修改 [Composition/AppComposition.cs](Composition/AppComposition.cs)：

```csharp
private static IOperationsBackend CreateBackend()
    => new ProductionBackend();
```

Views 和 ViewModels 不需要知道真实后端的具体类型。

## 局部 UI 扩展

```csharp
RegionRegistry.Default.Register(
    "command.secondary",
    () => new YourHealthDashboard());
```

可用 Region key：

| Key | 位置 |
|---|---|
| `command.secondary` | 总控右下次级仪表 |
| `topology.detail` | 拓扑节点 dossier |
| `archive.preview` | 资产预览器 |
| `reports.annotation` | 报告注释与操作 |
| `extensions.canvas` | 扩展页完整画布 |

## 整页扩展

```csharp
PageRegistry.Default.Register(new PageContribution(
    "database",
    "06",
    "数据库",
    "DATABASE",
    () => new DatabasePage()));
```

注册发生在 `AppComposition.Configure()`，`ShellViewModel` 会自动把页面加入桌面 rail 和底部导航。

## 视觉 QA

稳定截图：

```powershell
.\bin\Debug\net10.0-windows\FieldStation.Maximal.exe `
  --page COMMAND --width 1600 --height 960 `
  --snapshot artifacts\command.png
```

运行状态：

```powershell
.\bin\Debug\net10.0-windows\FieldStation.Maximal.exe `
  --page COMMAND --state running `
  --snapshot artifacts\running.png
```

过渡中间态：

```powershell
.\bin\Debug\net10.0-windows\FieldStation.Maximal.exe `
  --page ARCHIVE --state transition `
  --snapshot artifacts\transition.png
```

快照模式自动启用减少动态效果。它使用纯内存 Demo backend，不执行文件写入、删除或持久化。

## 继续阅读

- [docs/CODE_GUIDE.md](docs/CODE_GUIDE.md)：每一个手写文件和关键代码的含义。
- [docs/BUILD_ORDER.md](docs/BUILD_ORDER.md)：从空目录开始逐步编写同类软件的顺序。
