# 后端与功能扩展指南

Wallpaper Field 把界面状态、文件系统逻辑和系统交互分开，后续接入自己的后端时不需要重写主窗口。

## 组合入口

所有默认实现都在 `Composition/AppComposition.cs` 创建。新的数据库、HTTP API、队列或 RePKG 解包实现完成后，在这里替换对应对象即可。

## 可替换接口

- `IWallpaperScanService`：接管源目录发现、元数据读取与预览源路径解析。默认实现只读扫描并返回内存中的 `ScanResult`，通过 `IProgress<ScanProgress>` 把真实进度送回 UI。
- `IWallpaperLibraryService`：接管输出图库页的数据来源。可以从 SQLite、远端 API 或混合缓存返回 `WallpaperLibraryResult`。
- `IWallpaperUnpackService`：当前注入 `RePkgWallpaperUnpackService`，负责安全流式解包 `scene.pkg`、复制视频壁纸，并在成功后写入单项 metadata；场景包同时调用内置 RePKG TEX 转图链路。如需接入远端队列或其他转换器，保留取消令牌、逐项错误隔离和进度回调即可替换。
- `IPreviewThumbnailDecoder`：项目浏览器预览的唯一解码 worker 边界。实现只能返回冻结的静态首帧或结构化失败，必须继续服从 `PreviewThumbnailLimits`，不能因后端更快或图片来自远端就放宽输入、尺寸、像素、并发或缓存预算。
- `IFolderPickerService`：替换目录选择体验，例如加入最近目录或企业存储位置。
- `ISystemFolderService`：替换卡片点击行为，例如打开应用内详情、调用自定义文件浏览器或记录审计事件。

## RePKG 信任边界

产品代码只能通过 `WallpaperField.ThirdParty.RePKG` 下的第一方适配器进入内置 RePKG：PKG 读取必须走 `SafePackageReader`，TEX 转换和派生输出规划走 `RePkgTextureConverter`。不要从服务、ViewModel 或 UI 直接引用 `RePKG.Application` / `RePKG.Core`，也不要回退到 eager `PackageReader` / `PackageWriter`。

`RePKG.Application.csproj` 使用按职责描述的编译白名单，保留 TEX 读取/转换链并排除未使用的 Package 与 Texture Writer 角色；完整上游源码和许可证仍保留在仓库中。新增或调整上游文件后先运行：

```powershell
pwsh -NoProfile -File .\scripts\verify-repkg-compile-surface.ps1 -Mode Verify
dotnet run --project .\tests\WallpaperField.SmokeTests\WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore
```

验证脚本直接比较 MSBuild 实际 `Compile` 集与角色规则；不要另建需要人工同步的逐文件清单。`RePKG.Core` 在 v1.2.2 仍完整编译，后续若要收窄必须作为新的隔离实验重新验证程序集身份、固定 PKG/TEX fixture、发布文件和许可集合。

## 数据契约

核心记录为 `Models/WallpaperRecord.cs`：

- `WorkshopId`：稳定唯一识别码。
- `Title`：用户可读标题。
- `SourceDirectory` / `OutputDirectory`：来源与输出位置。
- `PreviewPath` / `PreviewFileName`：卡片媒体。
- `HasScenePackage` / `ScenePackagePath`：扫描时确认的解包资格与源包位置。
- `WallpaperType` / `HasVideoFile` / `VideoFilePath` / `VideoRelativePath`：视频壁纸类型、源文件与安全的相对输出位置。
- `Warnings`：非致命降级原因；卡片会自动显示琥珀色提示徽标。
- `ProjectKey`：`WorkshopId` 加规范化源项目路径 SHA-256 指纹形成的跨页关联键。问题定位、浏览当前项和处理结果使用它消除同 ID 不同源歧义；不要改成原始路径，也不要只使用 Workshop ID。

扩展字段时建议保持现有字段兼容；需要改变持久化结构时提高对应 metadata 或处理清单的 `SchemaVersion`。不要把密码、访问令牌或用户隐私信息写入公开的 `metadata.json`。

### 浏览快照与冻结处理请求

`ScanProjectSnapshot` 是成功扫描后一次发布的浏览事实，包含 `ScanSnapshotIdentity`、单调 revision 和同一组卡片引用。浏览、筛选和问题跳转只消费这份快照；失败或取消不得用半成品替换上一份成功快照。扩展扫描后端时，不要分开发送 identity、revision 和集合通知，也不要在 UI getter 中重新探测文件。

所有单项/批量处理必须先由 `ScanSession` 生成 `FrozenWallpaperProcessRequest`。该请求冻结 snapshot identity、revision、输出目录和精确成员；`UnpackSession` 在进入唯一前台任务槽前仍会复核身份、成员和输出边界。新页面、命令、API 或队列不能直接把 `WallpaperRecord` 列表送给解包服务来绕过此请求，也不能把失败的 Website/Other 项强制标记为可处理。

项目浏览器预览通过 `IPreviewThumbnailDecoder` 和应用级预览服务进入后台 worker，UI 只持有可取消租约。`PreviewThumbnailLimits` 是安全合同：输入最大 64 MiB、宽高各 4096、源画布 16M 像素、同时解码 4、缓存 128 项且估算解码字节 128 MiB。扩展格式或远端来源必须在分配/解码前接受同等检查，并保留 reparse、源文件变化、取消和静态首帧语义。

### 输出图库发现契约

图库会在输出根下递归发现名为 `metadata.json` 的文件，以兼容用户已有的嵌套目录；它不是只接受 `<WorkshopId>/metadata.json`。遍历不会进入 reparse-point 目录，也会跳过条目目录内的 `unpacked`、staging 和 backup 工作树。候选先按规范化相对路径稳定排序；同一 `WorkshopId`（忽略大小写）出现多次时，整组不会任选一个展示，而是作为包含全部候选路径的 `LibraryConflict` 交给问题中心。

自定义后端若替换 `IWallpaperLibraryService`，应保留上述递归兼容、稳定排序、显式冲突、逐项解析错误与取消语义；若要限制发现范围，必须作为可见的 schema 迁移处理，不能静默隐藏现有嵌套图库。

## UI 自定义区域

- 稳定颜色、字体、间距、圆角、密度与工程纹理：`Themes/Tokens.xaml`。
- High Contrast 系统颜色 seam、双层焦点和 motion 语义：`Themes/AccessibilityMotion.xaml`。
- 通用输入、按钮、滚动条、进度条与 converter：`Themes/BaseControls.xaml`。
- 导航、卡片、问题筛选、状态与领域模板：`Themes/DomainComponents.xaml`。
- `Themes/EndfieldTheme.xaml` 只按上述顺序合并四层，供旧 pack URI 调用者兼容；新代码优先由 `App.xaml` 直接按序加载，不要在 wrapper 中重新定义 key。
- 扫描、项目浏览器、图库和问题中心的完整页面编排分别位于 `Views/ScanPageView.xaml`、`Views/BrowsePageView.xaml`、`Views/LibraryPageView.xaml`、`Views/ProblemCenterView.xaml`。每页拥有自己的固定控制区、虚拟化列表和仅属于该页的 UI 生命周期。
- `MainWindow.xaml` / `.xaml.cs` 是跨页壳层，负责导航、布局档位、窗口关闭、High Contrast/motion 应用和截图协调；不要把领域集合或列表定位状态搬回窗口。
- `ViewModels/Sessions/ScanSession.cs`、`UnpackSession.cs`、`LibrarySession.cs`、`ProblemCenterSession.cs` 与 `Application/TaskLifecycleCoordinator.cs` 是状态/用例所有者。`ViewModels/ShellViewModel.cs` 只保留兼容转发、导航、全局摘要和跨 session 协调。
- 四个页面都使用 WPF `VirtualizingStackPanel` 的 Recycling/Pixel/Page-cache 组合；Browse 以虚拟化行承载行内 `UniformGrid`。不要增加祖先 `ScrollViewer`，不要调用容器 `BringIntoView` 带动页面，也不要在回收容器的 `Loaded` 中把整卡透明度重置为 0。

新增功能时先确定语义所有者：磁盘/网络 I/O 留在服务，任务状态和筛选留在对应 session，跨页事实才进入 Shell，页面只绑定公开投影。不要为单一实现机械增加接口/工厂，也避免在代码隐藏中直接执行文件或网络业务。

## 问题、诊断与取消契约

可恢复失败应发布结构化 `AppIssue`，包含稳定 source/code/context、用户可行动摘要、受限详情和真实 `DiskFact`；不要只追加展示字符串，也不要把攻击者控制内容或完整本地路径写入日志。问题由 `ProblemCenterSession` 单一所有，成功重试只解析同一 source/code/context 的记录。

替换扫描、解包或图库后端时必须继续传播 `CancellationToken`，并通过 `TaskLifecycleCoordinator` 的 operation ID 发布生命周期。进入 commit-critical 后可以记录取消 pending，但只有完成安全提交或回滚才能发布终态；窗口关闭等待 coordinator 真正静止。进度无法计算时使用 indeterminate，不能用项目数伪装单个大包的字节进度。
