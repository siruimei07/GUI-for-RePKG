# 后端与功能扩展指南

Wallpaper Field 把界面状态、文件系统逻辑和系统交互分开，后续接入自己的后端时不需要重写主窗口。

## 组合入口

所有默认实现都在 `Composition/AppComposition.cs` 创建。新的数据库、HTTP API、队列或 RePKG 解包实现完成后，在这里替换对应对象即可。

## 可替换接口

- `IWallpaperScanService`：接管源目录发现、元数据读取、预览处理与索引写入。返回 `ScanResult`，通过 `IProgress<ScanProgress>` 把真实进度送回 UI。
- `IWallpaperLibraryService`：接管第二页的数据来源。可以从 SQLite、远端 API 或混合缓存返回 `WallpaperLibraryResult`。
- `IWallpaperUnpackService`：当前注入 `RePkgWallpaperUnpackService`，负责安全流式解包 `scene.pkg`，并调用内置 RePKG TEX 转图链路。如需接入远端队列或其他转换器，保留取消令牌、逐项错误隔离和进度回调即可替换。
- `IFolderPickerService`：替换目录选择体验，例如加入最近目录或企业存储位置。
- `ISystemFolderService`：替换卡片点击行为，例如打开应用内详情、调用自定义文件浏览器或记录审计事件。

## 数据契约

核心记录为 `Models/WallpaperRecord.cs`：

- `WorkshopId`：稳定唯一识别码。
- `Title`：用户可读标题。
- `SourceDirectory` / `OutputDirectory`：来源与输出位置。
- `PreviewPath` / `PreviewFileName`：卡片媒体。
- `HasScenePackage` / `ScenePackagePath`：扫描时确认的解包资格与源包位置。
- `Warnings`：非致命降级原因；卡片会自动显示黄色提示徽标。

扩展字段时建议保持现有字段兼容，并提高 `WallpaperIndex.SchemaVersion`。不要把密码、访问令牌或用户隐私信息写入公开的 `metadata.json`。

## UI 自定义区域

- 全局颜色、圆角、动画、输入框、滚动条、卡片与按钮模板：`Themes/EndfieldTheme.xaml`。
- 两个页面的编排与卡片模板：`MainWindow.xaml`。
- 页面切换、环境动效、紧凑布局和截图 QA：`MainWindow.xaml.cs`。
- 页面状态、命令、进度、错误与集合：`ViewModels/ShellViewModel.cs`。
- 大图库布局：`MainWindow.xaml` 中使用 WPF 内置 `VirtualizingStackPanel`；不要在回收容器的 `Loaded` 中把整卡透明度重置为 0。

新增功能时优先扩充 ViewModel 与服务契约，再绑定到 XAML；避免在代码隐藏中直接执行文件或网络业务。
