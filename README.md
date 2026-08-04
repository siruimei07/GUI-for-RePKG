# Wallpaper Field

一个纯 C# / WPF 的 Wallpaper Engine 本地壁纸索引工具。界面采用原创的 Endfield-inspired 白、炭黑、信号黄技术现场语言；没有打包官方徽标、角色图、字体或生产资源。

## 已实现

- 两页桌面前端：`扫描中心` 与 `输出壁纸库`。
- 壁纸根目录、输出目录均可直接输入或通过系统目录选择器选择。
- 扫描壁纸根目录的所有直接子文件夹。
- 大小写不敏感地读取 `project.json` 的 `title`、`workshopid`；支持字符串或数字 ID。
- 识别 `preview.png`、`.jpg`、`.jpeg`、`.gif`，并复制到独立的 `<输出目录>/<workshopid>/`。
- 缺少或损坏的元数据会安全回退并在卡片/提示栏显示警告；单项失败不终止整个扫描。
- 生成逐项 `metadata.json`、根级 `wallpaper-index.json` 与 `workshop-ids.txt`。
- 扫描时大小写不敏感地检测每个项目直接子级的 `scene.pkg`，并把可解包状态与完整路径写入元数据和根索引。
- 输出库递归读取 `metadata.json`，但不会进入解包产物目录；点击卡片在文件管理器中打开对应输出目录。
- “开始解包”会仅处理扫描时标记了 `scene.pkg` 的项目，其余项目直接跳过；每个包写入 `<workshopid>/unpacked/`。
- 解包后端基于 MIT 许可的 RePKG 0.4.0 PKG 格式读取源码，加入流式复制、取消、原子 staging、范围校验、路径穿越与重解析点防护。
- 大图库使用 WPF 内置回收式纵向虚拟化和限宽异步解码；卡片回收不再重复执行透明度归零动画。
- 支持扫描取消、路径重叠保护、原子写入、键盘焦点、减弱环境动效和紧凑窗口重排。

## 构建与运行

要求 Windows 与 .NET 10 SDK。

```powershell
dotnet build .\WallpaperField.csproj --configuration Release
.\bin\Release\net10.0-windows\WallpaperField.exe
```

项目不依赖第三方 NuGet 包，`NuGet.Config` 已禁用外部包源。

RePKG 归属与 MIT 许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 与
`ThirdParty/RePKG/LICENSE.txt`。本项目执行原始 PKG 解包，保留包内目录和
`.tex` 文件；不包含 RePKG 的 TEX→图片转换依赖。

运行无第三方测试框架的端到端烟雾测试：

```powershell
dotnet run --project .\tests\WallpaperField.SmokeTests\WallpaperField.SmokeTests.csproj --configuration Release
```

可选地传入一个真实 `scene.pkg` 做抽样解包，或传入 Workshop 内容根目录做所有
PKG 的只读兼容性校验：

```powershell
dotnet run --project .\tests\WallpaperField.SmokeTests\WallpaperField.SmokeTests.csproj --configuration Release -- "E:\...\3223796894\scene.pkg"
dotnet run --project .\tests\WallpaperField.SmokeTests\WallpaperField.SmokeTests.csproj --configuration Release -- "E:\...\content\431960"
```

## 输出结构

```text
用户选择的输出目录/
├─ wallpaper-index.json
├─ workshop-ids.txt
├─ 884307090/
│  ├─ metadata.json
│  ├─ preview.png
│  └─ unpacked/
│     ├─ .wallpaper-field-unpack.json
│     ├─ scene.json
│     └─ ...包内原始文件
└─ 1864604777/
   ├─ metadata.json
   └─ preview.jpg
```

源目录和输出目录不能相同，也不能互相包含，避免扫描源被输出文件污染。GIF 会原样复制；WPF 卡片使用其静态首帧作为轻量预览。

## 后端扩展

所有系统能力均通过 `Contracts/` 下的接口注入。替换实现只需要在 `Composition/AppComposition.cs` 修改一次组合关系；无需改动页面 XAML。具体扩展说明见 [docs/EXTENDING.md](docs/EXTENDING.md)。

## QA 启动参数

程序保留了非持久化的视觉验证入口：

```text
--source <目录> --output <目录> --scan
--page scan|library
--snapshot <png路径> --width <像素> --height <像素>
--scroll-index <记录索引>
--reduced-motion
```

这些参数仅便于自动验收，不改变正常用户流程。
