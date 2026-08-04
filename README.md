# Wallpaper Field

一款面向 Windows 的 Wallpaper Engine 本地壁纸整理与 `scene.pkg` 解包工具。程序使用 C# / WPF 编写，可以扫描 Workshop 壁纸目录、读取标题和 Workshop ID、集中保存预览图，并通过内置的 RePKG 解包带有 `scene.pkg` 的项目。

> [!NOTE]
> 这是一个非官方社区项目，与 Wallpaper Engine、Arknights: Endfield、Hypergryph 或其关联公司无隶属或背书关系。界面采用原创的 Endfield-inspired 技术终端风格，不包含官方徽标、角色图、宣传素材或字体。

![扫描中心：选择目录、扫描项目并识别 scene.pkg](docs/images/scan-center.png)

_扫描中心：显示 preview、标题、Workshop ID，以及每个项目是否可以解包。_

![输出壁纸库：浏览已保存的壁纸记录](docs/images/output-library.png)

_输出壁纸库：重新读取输出目录；点击卡片即可在 Windows 文件资源管理器中打开对应文件夹。_

## 功能概览

- 扫描 Wallpaper Engine Workshop 根目录下的所有直接子文件夹。
- 从 `project.json` 读取 `title` 和 `workshopid`，字符串或数字形式的值均可识别。
- 识别 `preview.png`、`preview.jpg`、`preview.jpeg` 和 `preview.gif`，并复制到独立的输出项目目录。
- 在扫描阶段识别每个项目是否包含 `scene.pkg`；没有 PKG 的项目在批量解包时会直接跳过。
- 使用内置 RePKG 解包 PKG，并执行默认的 TEX 图片转换与 `.tex-json` 元数据生成。
- 生成便于程序或后端继续处理的 `metadata.json`、`wallpaper-index.json` 和 `workshop-ids.txt`。
- 从输出目录重建壁纸库；点击任意卡片可定位到对应目录。
- 大型壁纸库使用回收式列表虚拟化和异步图片解码，深度滚动时不会反复创建全部卡片。
- 扫描与解包均有进度、取消、逐项错误隔离和安全路径检查。
- 发布版是 Windows x64 自包含单文件 EXE，不需要另外安装 .NET 或 RePKG。

## 运行要求

### 普通用户

- Windows 10 或 Windows 11，64 位系统。
- Wallpaper Engine 的本地 Workshop 内容，或拥有相同目录结构的离线副本。
- 足够的输出磁盘空间。解包后的体积通常会明显大于原始 `scene.pkg`。

根目录中的 `GUI_for_RePKG.exe` 已包含 .NET 运行时和所需的 RePKG 代码。正常使用不需要管理员权限，也不需要单独下载或启动 `RePKG.exe`。

### 从源码构建

- Windows 10/11。
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

## 快速开始

1. 下载 Windows x64 发布包并完整解压；如果直接获取了仓库，运行根目录中的 `GUI_for_RePKG.exe`。
2. 在左侧打开“扫描中心”。
3. 选择 Wallpaper Engine 壁纸根目录。
4. 选择一个专门的输出目录。
5. 点击“开始扫描”，等待卡片和统计信息出现。
6. 如需解包，点击“开始解包”。程序只处理标记为 `PKG READY` 的项目。
7. 打开左侧“输出壁纸库”，刷新并浏览整理后的内容。

下面是每一步的详细说明。

## 使用教程

### 1. 找到 Wallpaper Engine Workshop 目录

Wallpaper Engine 的 Steam App ID 是 `431960`。默认 Steam 库常见路径为：

```text
C:\Program Files (x86)\Steam\steamapps\workshop\content\431960
```

如果 Steam 库位于其他磁盘，路径通常为：

```text
<SteamLibrary>\steamapps\workshop\content\431960
```

例如：

```text
E:\SteamLibrary\steamapps\workshop\content\431960
```

选择的是 `431960` 这一层，而不是某一个具体数字 ID 的文件夹。程序只扫描它的直接子文件夹，例如：

```text
431960\
├─ 884307090\
├─ 1864604777\
└─ 2390303351\
```

你可以直接在输入框中粘贴路径，也可以点击右侧的目录选择按钮。

### 2. 选择输出目录

输出目录用于保存索引、元数据、preview 副本和解包文件。建议新建一个专用目录，例如：

```text
D:\WallpaperFieldOutput
```

> [!IMPORTANT]
> 源目录和输出目录不能相同，也不能互相包含。请不要把输出目录放进 `431960`，也不要把 `431960` 放进输出目录；这样可以避免输出文件被再次当成扫描源。

扫描和解包只读取源壁纸目录；所有新文件都写入你指定的输出目录。若输出内容重要，仍建议定期备份。

### 3. 开始扫描

确认两个路径后，点击“开始扫描”。程序会逐个检查 `431960` 下的直接子文件夹，并执行以下操作：

1. 查找并读取 `project.json`。
2. 获取其中的 `title` 与 `workshopid`。
3. 查找 preview 图片并复制到输出目录。
4. 检查项目根目录是否存在 `scene.pkg`。
5. 写入单项元数据、总索引和 ID 清单。
6. 在扫描中心生成对应卡片。

卡片会显示：

- preview 图片；没有可用图片时显示“无信息”。
- `title`；无法读取时使用文件夹名作为回退标题。
- `workshopid`；缺失或无效时使用文件夹名作为回退识别码，并显示警告。
- `PKG READY` 或无 PKG 状态。
- 元数据缺失、preview 缺失等非致命警告。

单个项目损坏不会终止整个扫描。完成后请查看页面顶部的提示区域，确认是否有项目需要手动检查。

如果多个源文件夹最终得到相同的 Workshop ID，程序会保留先处理的项目，并把后续重复项记录为失败，避免不同壁纸覆盖到同一个输出目录。

### 4. 理解输入文件规则

每个壁纸项目的典型结构如下：

```text
884307090\
├─ project.json
├─ preview.png
├─ scene.pkg
└─ ...其他 Wallpaper Engine 文件
```

| 文件 | 是否必需 | 读取方式 |
| --- | --- | --- |
| `project.json` | 建议存在 | 文件名和字段名不区分大小写；读取 `title` 与 `workshopid` |
| `preview.*` | 可选 | 只查找项目根目录；支持 PNG、JPG、JPEG、GIF |
| `scene.pkg` | 解包时必需 | 只识别项目根目录中的同名文件，文件名不区分大小写 |

preview 的选择优先级为 PNG → JPG → JPEG → GIF。GIF 会原样复制；为了保持列表流畅，WPF 卡片使用其首帧作为静态预览。

一个最小的 `project.json` 示例：

```json
{
  "title": "My Wallpaper",
  "workshopid": "884307090"
}
```

`workshopid` 也可以是 JSON 数字。输出目录以最终识别到的 Workshop ID 命名，因此它也是项目在本地索引中的唯一识别码。

### 5. 解包 `scene.pkg`

扫描完成后，如果至少有一个项目显示 `PKG READY`，“开始解包”按钮会变为可用。点击后：

- 只处理本次扫描时发现 `scene.pkg` 的项目。
- 没有 `scene.pkg` 的项目直接跳过，不会浪费时间尝试打开不存在的文件。
- 每个包解压到 `<输出目录>\<workshopid>\unpacked\`。
- 不同 Workshop ID 的内容互不混合。
- 某一个包失败时会记录错误并继续处理后续项目。
- 可以在操作过程中取消；临时 staging 目录会被清理。

如果扫描完成后移动、删除或替换了源目录中的 `scene.pkg`，请重新扫描再解包。如果更改了输出目录，也应重新扫描，以确保卡片记录和目标目录一致。

解包资格来自当前运行会话中的扫描结果。重新启动程序，或只在“输出壁纸库”中加载旧记录，并不会自动恢复可解包队列；此时请返回“扫描中心”重新扫描源目录。

#### 为什么解包后仍然有 `.tex` 文件？

这是正常行为，不是把文件误识别成了 LaTeX。Wallpaper Engine 的 `.tex` 是纹理资源，Windows 可能会把同名扩展显示为“LaTeX 源文件”。本程序与 RePKG 默认 `extract` 流程一致：

- 保留包内原始 `.tex` 文件。
- 尝试额外生成可查看的图片或媒体文件。
- 生成相应的 `.tex-json` 元数据。

根据纹理内容，转换结果可能是 `.png`、`.jpg`、`.gif` 或 `.mp4`。如果某个纹理无法转换，原始 `.tex` 仍会保留，并在完成信息中给出警告；这不会阻止其他文件继续解包。

### 6. 浏览输出壁纸库

打开左侧“输出壁纸库”页面。页面会读取所选输出目录中的 `metadata.json`，恢复标题、Workshop ID、preview 和文件夹位置。

- 点击“刷新输出库”可重新读取磁盘内容。
- 点击“更换目录”可浏览另一套输出库。
- 点击卡片或卡片右侧箭头，会在 Windows 文件资源管理器中打开该项目的输出文件夹。
- 输出库不会进入 `unpacked` 等解包产物目录查找元数据，因此包内同名 JSON 不会被误当成壁纸记录。

## 输出目录结构

扫描并解包后的典型结构如下：

```text
用户选择的输出目录\
├─ wallpaper-index.json
├─ workshop-ids.txt
├─ 884307090\
│  ├─ metadata.json
│  ├─ preview.png
│  └─ unpacked\
│     ├─ .wallpaper-field-unpack.json
│     ├─ scene.json
│     ├─ materials\example.tex
│     ├─ materials\example.png
│     ├─ materials\example.tex-json
│     └─ ...包内其他文件与转换产物
└─ 1864604777\
   ├─ metadata.json
   └─ preview.jpg
```

主要文件的用途：

| 文件 | 用途 |
| --- | --- |
| `wallpaper-index.json` | 本次扫描的完整记录，适合后续程序批量读取 |
| `workshop-ids.txt` | 所有识别到的 ID，每行一个 |
| `<id>/metadata.json` | 单个项目的标题、路径、preview、PKG 状态与警告 |
| `<id>/preview.*` | 从源项目复制的预览图 |
| `<id>/unpacked/` | `scene.pkg` 的原始文件和 TEX 转换产物 |
| `.wallpaper-field-unpack.json` | 本次解包结果的应用清单 |

`metadata.json` 和根索引被设计为可扩展的数据契约。后续接入数据库、HTTP API、队列或自己的后端时，可以直接消费这些 JSON，也可以替换程序中的服务实现。

> [!WARNING]
> 重复扫描和重复解包属于更新/覆盖操作，不是严格的目录镜像清理。源项目已删除的旧输出文件、旧 preview，或新包中已不存在的旧解包文件可能继续保留。需要完全干净、可比对的结果时，请选择一个新的空输出目录。

## 常见问题

### “开始扫描”按钮不可用

请确认源目录和输出目录都已填写、路径存在且没有互相包含，并确认程序当前没有执行其他扫描或解包任务。输出目录不存在时程序会尝试创建；若当前账户没有写入权限，请换到个人可写目录。

### “开始解包”按钮不可用

当前会话必须先成功完成一次扫描，并且至少发现一个 `scene.pkg`。重启程序、扫描后更改输出目录，或只加载旧输出库时，都需要重新扫描。

### 扫描到了文件夹，但没有标题或 Workshop ID

通常是 `project.json` 缺失、JSON 损坏，或对应字段为空。程序会使用文件夹名作为回退值，并在顶部提示区域列出原因。修复源文件后重新扫描即可。

### 卡片显示“无信息”

项目根目录中没有名为 `preview` 的受支持图片，或者图片已损坏。请检查文件是否为 `preview.png`、`preview.jpg`、`preview.jpeg` 或 `preview.gif`。其他文件名不会被自动选作 preview。

### 某些项目没有被解包

只有扫描时检测到项目根目录存在 `scene.pkg` 的记录才会进入解包队列。没有 PKG 的项目会被明确跳过。如果后来才把 PKG 放入目录，请先重新扫描。

### `.tex` 双击后被文本或 LaTeX 程序打开

这是 Windows 的文件关联，不代表解包错误。请查看同目录生成的 PNG/JPG/GIF/MP4；原始 `.tex` 被保留用于完整性和后续处理。

### GIF 为什么没有在卡片中播放？

GIF 文件会完整复制到输出目录，但列表卡片只显示首帧，以避免大量动态图片同时播放造成滚动卡顿。

### Windows SmartScreen 提示未知发布者

当前社区构建可能未使用商业代码签名证书。请只从可信发布页下载，核对仓库来源和发布附件；不确定时可以从源码自行构建。不要运行来源不明的同名 EXE。

## 从源码构建

在项目根目录执行：

```powershell
dotnet restore .\WallpaperField.slnx --configfile .\NuGet.Config
dotnet build .\WallpaperField.slnx --configuration Release
.\bin\Release\net10.0-windows\WallpaperField.exe
```

生成 Windows x64 自包含单文件版本：

```powershell
.\build-release.cmd
.\GUI_for_RePKG.exe
```

`build-release.cmd` 会调用 `build-release.ps1`，将发布结果复制为仓库根目录的 `GUI_for_RePKG.exe`。RePKG 和 .NET 运行时会链接到该 EXE 中。

> [!IMPORTANT]
> 对外分发时，请同时提供本项目的 `LICENSE`、`THIRD-PARTY-NOTICES.md`、`ThirdParty/RePKG/LICENSE.txt` 和 `ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt`。最简单的做法是把 EXE 与这些文件一起放入发布 ZIP，而不是只上传一个孤立的 EXE。

## 测试

运行不依赖第三方测试框架的端到端烟雾测试：

```powershell
dotnet run --project .\tests\WallpaperField.SmokeTests\WallpaperField.SmokeTests.csproj --configuration Release
```

使用真实 `scene.pkg` 做抽样解包：

```powershell
dotnet run --project .\tests\WallpaperField.SmokeTests\WallpaperField.SmokeTests.csproj --configuration Release -- "D:\path\to\scene.pkg" "D:\test-output"
```

也可以传入 Wallpaper Engine 内容根目录，对其中的 PKG 做只读兼容性检查：

```powershell
dotnet run --project .\tests\WallpaperField.SmokeTests\WallpaperField.SmokeTests.csproj --configuration Release -- "E:\SteamLibrary\steamapps\workshop\content\431960"
```

程序还保留了用于自动视觉验收的非持久化启动参数：

```text
--source <目录> --output <目录> --scan
--page scan|library
--snapshot <png路径> --width <像素> --height <像素>
--scroll-index <记录索引>
--reduced-motion
```

这些参数只用于截图和回归测试，不会改变普通用户的使用流程。

## 二次开发

界面状态、文件系统逻辑和系统交互通过 `Contracts/` 下的接口分离。默认实现集中在 `Services/`，依赖组合入口位于 `Composition/AppComposition.cs`。你可以替换扫描、输出库、目录选择、文件管理器或解包服务，而不必重写页面 XAML。

更完整的扩展说明见 [docs/EXTENDING.md](docs/EXTENDING.md)。新增后端时建议：

- 保持现有 JSON 字段向后兼容，必要时提高索引的 `schemaVersion`。
- 不要把密码、访问令牌或用户隐私信息写入公开的 `metadata.json`。
- 将耗时 I/O 保持为异步操作，并传递取消令牌与进度。
- 继续保留逐项错误隔离、输出边界检查和路径穿越防护。

欢迎提交 Issue、Pull Request，或 fork 后改造成适合自己工作流的版本。

## 许可证

Wallpaper Field 自行创作的代码以 [MIT License](LICENSE) 发布。你可以使用、复制、修改、合并、发布和再分发，但必须保留 MIT 版权与许可声明。

第三方代码和依赖仍适用其各自的许可证。RePKG 的完整许可和依赖声明位于 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)、[ThirdParty/RePKG/LICENSE.txt](ThirdParty/RePKG/LICENSE.txt) 与 [ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt](ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt)。本项目的 MIT 许可证不会替代这些第三方条款。

## 感谢

- 感谢 [notscuffed/RePKG](https://github.com/notscuffed/repkg) 提供 MIT 许可的 Wallpaper Engine PKG 读取与 TEX 转换实现。本项目在保留上游许可和版权声明的前提下，将相关核心能力集成到 C# 桌面程序中。
- 感谢 [Brandon030722/ark-ui-skill](https://github.com/Brandon030722/ark-ui-skill) 提供 clean-room 的终末地风格 UI 设计方法与 `endfield / maximal` 工作流参考，帮助确定了界面的信息层级、色彩、网格、动效和交互方向。该仓库仅作为设计过程参考，不是本程序的运行时依赖，本项目也未打包官方游戏素材。

也感谢所有愿意测试、报告问题和继续改造本项目的人。基于 MIT 许可证，欢迎自由 fork、修改和再发布；请在传播衍生版本时保留必要的版权、许可与第三方致谢。
