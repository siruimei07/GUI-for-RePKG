# Wallpaper Field v1.2.2 项目目录与文档整理设计

- 状态：书面设计已获用户批准，等待实施
- 日期：2026-08-22
- 当前版本：v1.2.2
- 基线提交：`5b2aefa9fa50b5b18708110f4f8402fc2ef125f8`
- 后续主线：v1.3.0 Project Browser

## 1. 目标

本次整理同时解决三个问题：仓库根目录长期按技术类型散落、当前与历史文档缺少统一生命周期、v1.3.0 路线没有进入受控且显眼的文档入口。

完成后，仓库以 `src`、`test`、`doc`、`dep`、`tool` 为主要职能目录；根 README 和文档索引明确 v1.2.2 是当前版本，并突出链接唯一的 v1.3.0 Project Browser 路线；有持续价值的旧记录进入 `doc/archive/`，无保留价值的生成目录在精确验证后删除。

这是一次物理路径和文档生命周期迁移，不改变产品功能、C# 命名空间、数据安全边界或发布包的外部布局。

## 2. 规范依据与适用边界

本设计采用 [ecomfe 项目目录结构规范](https://github.com/ecomfe/spec/blob/master/directory.md) 的以下强制要求：

- 根目录按职能划分，不把资源类型或业务逻辑目录直接放在根目录。
- 使用 `src`、`test`、`doc`、`dep`、`tool` 等约定名称。
- 使用约定缩写，目录名不使用复数形式；现有 `docs`、`tests`、`scripts` 因此退出受控目录。
- 第三方依赖置于 `dep`，本次不得修改其中第三方内容。
- `src`、`test`、`tool` 不进入最终发布包。

原规范面向历史前端项目，WPF/.NET 的解决方案文件、项目清单、XAML 资源和 GitHub 工作流没有一一对应条款。项目按下列方式适配，同时保留可验证的规范边界：

- `.github/` 是托管平台元数据；根 README、LICENSE、第三方声明、NuGet 配置、解决方案和用户分发 EXE 是平台、法律或发布入口，可以留在根目录。
- `src/wallpaper-field/` 视为单一产品业务目录；其下现有架构层是稳定的应用职能边界，采用小写单数目录名，但不借本次迁移重写业务或命名空间。
- `dep/repkg/` 以下是 vendored upstream 边界，内部目录名、文件名和内容不套用第一方重命名规则。
- `output/` 是忽略的生成输出目录，`temp/` 是忽略的本地研究与验证目录；两者均为根级职能目录，不进入发布包。
- `doc/img/` 是本地文档图片的唯一约定位置；没有本地图片时不提交空目录。

## 3. 目标目录结构

```text
root/
├─ src/
│  └─ wallpaper-field/
│     ├─ application/
│     ├─ composition/
│     ├─ contract/
│     ├─ control/
│     ├─ infrastructure/
│     ├─ model/
│     ├─ property/
│     ├─ service/
│     ├─ theme/
│     ├─ view/
│     ├─ viewmodel/
│     │  └─ session/
│     ├─ App.xaml
│     ├─ App.xaml.cs
│     ├─ GlobalUsings.cs
│     ├─ MainWindow.xaml
│     ├─ MainWindow.xaml.cs
│     ├─ WallpaperField.csproj
│     └─ app.manifest
├─ test/
│  └─ smoke/
├─ doc/
│  ├─ README.md
│  ├─ extension.md
│  ├─ release/
│  │  └─ v1.2.2.md
│  ├─ plan/
│  │  └─ v1.3.0.md
│  └─ archive/
│     ├─ release/
│     │  └─ v1.2.1.md
│     └─ v1.2.2/
├─ dep/
│  └─ repkg/
├─ tool/
│  ├─ release.cmd
│  ├─ release.ps1
│  └─ verify-repkg-compile-surface.ps1
├─ output/                       # ignored、按需生成
├─ temp/                         # ignored、本地研究与验证
├─ .github/
├─ .gitignore
├─ AGENTS.md
├─ GUI_for_RePKG.exe
├─ LICENSE
├─ NuGet.Config
├─ README.md
├─ THIRD-PARTY-NOTICES.md
└─ WallpaperField.slnx
```

`doc/plan/` 在整理期间还会临时包含本设计和对应实施计划。迁移完成并验证后，这两份已完成记录进入 `doc/archive/v1.2.2/`；`doc/plan/v1.3.0.md` 成为唯一活跃路线文档。

## 4. 路径迁移与编译边界

主要映射如下：

| 当前路径 | 目标路径 | 约束 |
|---|---|---|
| `Application/` | `src/wallpaper-field/application/` | 只改物理路径 |
| `Composition/` | `src/wallpaper-field/composition/` | 只改物理路径 |
| `Contracts/` | `src/wallpaper-field/contract/` | 命名空间保持 `WallpaperField.Contracts` |
| `Controls/` | `src/wallpaper-field/control/` | 更新相关 XAML URI |
| `Infrastructure/` | `src/wallpaper-field/infrastructure/` | 不改变 adapter 行为 |
| `Models/` | `src/wallpaper-field/model/` | 命名空间保持不变 |
| `Properties/` | `src/wallpaper-field/property/` | 保持程序集资源行为 |
| `Services/` | `src/wallpaper-field/service/` | 不改变安全与事务契约 |
| `Themes/` | `src/wallpaper-field/theme/` | 更新 pack/resource URI 并验证运行加载 |
| `ViewModels/` | `src/wallpaper-field/viewmodel/` | 子目录同样使用小写单数 |
| `Views/` | `src/wallpaper-field/view/` | 更新 XAML 页路径，不改页面行为 |
| 根应用/XAML/项目清单 | `src/wallpaper-field/` | 解决方案指向新项目路径 |
| `tests/WallpaperField.SmokeTests/` | `test/smoke/` | 保留 console harness 与断言计数合同 |
| `ThirdParty/RePKG/` | `dep/repkg/` | 整体迁移，内部内容不改 |
| `scripts/verify-repkg-compile-surface.ps1` | `tool/verify-repkg-compile-surface.ps1` | 更新所有调用者 |
| `build-release.ps1` | `tool/release.ps1` | 发布包外部布局保持不变 |
| `build-release.cmd` | `tool/release.cmd` | 仅包装新脚本路径 |

`WallpaperField.csproj` 移到应用目录后，SDK 默认 glob 只覆盖该产品目录。RePKG 源码位于项目目录之外，项目文件必须通过明确的外部 `Compile` 映射保持当前已批准的编译白名单；不得因为移动而重新纳入 eager Package reader/writer 或已排除的 Texture writer。`tool/verify-repkg-compile-surface.ps1` 继续作为 `UP-003` 的机器门禁。

解决方案、SmokeTests、CI 和发布脚本统一引用新的项目路径。WPF `Source`、pack URI、测试 fixture 位置和发布许可来源都按调用关系更新，不用兼容软链接或保留旧目录副本。生成输出集中到忽略的 `output/`；项目文件仍保留对仓库 `temp/` 的防御性排除，避免未来显式 glob 将研究材料编译进产品。

`dep/repkg/UPSTREAM-PATCHES.md` 随 vendored 树保持字节不变；其中记录的 v1.2.2 时代脚本路径属于历史实施证据，不作为当前命令。当前文档索引和扩展指南必须明确指向 `tool/verify-repkg-compile-surface.ps1`，避免把该历史路径误读为兼容入口。

## 5. 文档生命周期

### 5.1 当前入口

- 根 `README.md`：面向使用者，首先说明当前版本 v1.2.2；保留安装、核心功能和当前截图；在首屏可见区域加入“下一版本：v1.3.0 Project Browser”链接。
- `doc/README.md`：唯一文档索引，按“当前使用、当前发布、未来计划、历史归档、第三方与维护”分类。
- `doc/extension.md`：维护扩展和工程边界，不复制 README 的使用说明。
- `doc/release/v1.2.2.md`：当前发布事实，直接可达，不归档。

### 5.2 未来路线

`doc/plan/v1.3.0.md` 以用户提供的 Project Browser 设计为事实来源，维护成唯一活跃路线，基线固定为 v1.2.2 / `5b2aefa9…`。它保留以下主线：独立第四个 `BROWSE` 页面、虚拟化高密度封面网格、当前项与批量选择分离、筛选排序、响应式详情面板、PKG 解包与视频复制、web 项仅展示、批量操作、无障碍和性能预算。

路线同时明确：

- 本次目录标准化是已批准的前置维护工作。
- console harness 迁移为标准 `dotnet test` 项目是独立工作包；迁移前不能把零测试的 `dotnet test` 当作成功门禁。
- 签名、干净 VM、真实 200% DPI、Windows High Contrast 和屏幕阅读器仍是发布级人工门禁，未执行时不得推断通过。
- ImageSharp 2→4 跨主版本升级保持独立，不与 Project Browser 混合。

用户提供的原始设计保留在 `temp/agent-work/20260821-1655-v1-3-gallery-plan/`，不加入 Git、不被删除，也不作为第二份受控路线并行维护。

### 5.3 历史归档与删除

- `docs/releases/v1.2.1.md` 移至 `doc/archive/release/v1.2.1.md`。
- 已完成的 v1.2.2 roadmap 设计和实施计划移至 `doc/archive/v1.2.2/`，增加简短的“历史记录、已完成、当前入口”说明，但不改写当时事实。
- `temp/maintenance-audit/` 继续作为本地证据基线，不纳入 Git。迁移完成后只追加有日期的架构、路线和验证结果，保留原始审计结论。
- 过时但没有独立事实价值的重复索引或失效占位不保留副本。

## 6. 删除与失败处理

删除只针对以下已经清点到的忽略项：根 `artifacts/`、根 `bin/`、根 `obj/`、空 `Converters/`，以及移动前位于 `tests/WallpaperField.SmokeTests/`、`ThirdParty/RePKG/Source/RePKG.Application/`、`ThirdParty/RePKG/Source/RePKG.Core/` 下的六个 `bin/`、`obj/` 构建目录。执行前必须：

1. 分别解析每个目标的绝对路径；
2. 确认目标严格位于仓库根之下且与批准名称完全一致；
3. 确认它不是受控源码、用户 EXE、`temp/` 或迁移目标；
4. 记录目录是否受 Git 跟踪以及删除前用途；
5. 使用同一个 PowerShell 环境和 `-LiteralPath` 删除，不使用通配符或跨 shell 拼接。

上述 `bin/`、`obj/` 和旧 `artifacts/` 都是未受 Git 跟踪、可重新构建的生成内容，`Converters/` 为空；删除后不会提供逐文件恢复。若源提交仍可构建，可以在新 `output/` 结构中重建相应输出。`GUI_for_RePKG.exe` 是用户已有修改，必须原样留在根目录；整个 `temp/` 不参与删除。

迁移按源码/项目、依赖、测试/工具、文档四个可核对批次执行。每批开始前读取任务计划，结束后检查 `git status`、目标存在性和旧路径残留。遇到目标冲突、来源缺失、用户并发改动重叠或行为测试回归时停止当前批次，记录证据并修正映射；不得通过删除冲突文件或回滚用户改动强行继续。

## 7. 验证设计

验证分为四层：

1. **结构层**：扫描受控目录，确认根级职能、第一方小写单数命名、第三方例外、旧 `docs/tests/scripts/ThirdParty` 路径消失以及无意外空目录。
2. **引用层**：检查解决方案、项目引用、Compile/Content/Resource、WPF URI、CI、发布脚本、Markdown 本地链接、版本号和当前文档中的旧路径；扫描未完成标记与互相冲突的路线声明。
3. **行为层**：执行 restore、Release 非增量 build、完整 SmokeTests、故意失败入口和 RePKG 编译面验证；运行受控应用启动检查，证明资源路径没有破坏 WPF 加载。`dotnet test` 只记录 `TEST-001` 的零测试限制。
4. **发布层**：从不覆盖根 EXE的隔离目录生成候选，核对 EXE、许可、notices、补丁记录、release notes、版本 manifest、依赖清单、SHA-256 和 ZIP 精确白名单；确认 `src/test/tool/temp/output` 不进入分发。

主要命令在仓库根执行：

```powershell
dotnet restore WallpaperField.slnx --configfile NuGet.Config
dotnet build WallpaperField.slnx -c Release --no-restore --no-incremental
dotnet run --project test/smoke/WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore
dotnet run --project test/smoke/WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore -- --verify-failure-exit
pwsh -NoProfile -File tool/verify-repkg-compile-surface.ps1 -Mode Verify
dotnet test WallpaperField.slnx -c Release --no-build --no-restore
git diff --check
git status --short --branch
```

实施计划会在读取脚本参数后写出隔离发布命令和所有静态扫描的精确表达式。任何完成声明都以本次迁移后的新鲜输出为准，不复用历史通过结果。

## 8. 风险与完成条件

本次主要防止以下已关闭或已缓解条目因路径迁移回归：

- `DOC-001`：当前截图、版本和链接不能重新陈旧。
- `REL-001`：v1.2.2 版本与提交身份必须保持可验证。
- `RELEASE-002`：发布 ZIP 的完整许可和清单合同必须保持。
- `UP-003`：RePKG 编译面不能因外部源码映射扩大。
- `TEST-001`：SmokeTests 必须继续有正数断言摘要和非零故障入口；零项 `dotnet test` 不能冒充门禁。

只有在目标目录结构、文档生命周期、引用、构建、SmokeTests、编译面、隔离发布和最终差异全部核对后，整理才算完成。签名、远程发布、干净 VM 和实体无障碍设备验证不在本次执行范围，最终交付必须继续列为未验证项。
