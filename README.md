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
- 输出库递归读取 `metadata.json`；点击卡片在文件管理器中打开对应输出目录。
- 解包按钮和服务契约已经预留，当前版本明确不执行解包。
- 大图库使用回收式虚拟化布局和限宽解码，不会一次创建、解码全部预览卡片。
- 支持扫描取消、路径重叠保护、原子写入、键盘焦点、减弱环境动效和紧凑窗口重排。

## 构建与运行

要求 Windows 与 .NET 10 SDK。

```powershell
dotnet build .\WallpaperField.csproj --configuration Release
.\bin\Release\net10.0-windows\WallpaperField.exe
```

项目不依赖第三方 NuGet 包，`NuGet.Config` 已禁用外部包源。

运行无第三方测试框架的端到端烟雾测试：

```powershell
dotnet run --project .\tests\WallpaperField.SmokeTests\WallpaperField.SmokeTests.csproj --configuration Release
```

## 输出结构

```text
用户选择的输出目录/
├─ wallpaper-index.json
├─ workshop-ids.txt
├─ 884307090/
│  ├─ metadata.json
│  └─ preview.png
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
--reduced-motion
```

这些参数仅便于自动验收，不改变正常用户流程。
