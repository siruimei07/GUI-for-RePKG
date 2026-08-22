# Wallpaper Field v1.2.2 Repository Reorganization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reorganize the complete Wallpaper Field repository into the approved FECS/ecomfe functional layout, keep v1.2.2 current, make the maintained v1.3.0 Project Browser route prominent, and preserve all runtime, test, and release contracts.

**Architecture:** Move the product, support surfaces, and documentation in four reviewable commits. Keep C# namespaces and vendored contents stable, centralize generated .NET output through a root `Directory.Build.props`, use one smoke-test repository-path boundary, and update every caller instead of retaining compatibility directories.

**Tech Stack:** Windows PowerShell 7/Windows PowerShell, Git, .NET 10 SDK/MSBuild, WPF/XAML, C# console SmokeTests, GitHub Actions YAML, Markdown.

**Spec:** `doc/plan/2026-08-22-project-directory-reorganization-design.md`

## Global Constraints

- Current product version remains `1.2.2`; `FileVersion` remains `1.2.2.0`; `AssemblyVersion` remains `1.0.0.0`.
- Principal root directories are exactly `.github`, `src`, `test`, `doc`, `dep`, and `tool`; ignored `output` and `temp` are functional runtime/work directories.
- First-party directories use lowercase singular names; `.github` internals, required root files, and vendored `dep/repkg` internals are explicit exceptions.
- C# namespaces, XAML `x:Class` values, public types, user behavior, PKG/TEX safety, transaction, cancellation, and UI semantics do not change.
- `dep/repkg` moves as an immutable tracked tree. Do not edit `RePKG.Core` or `RePKG.Application` source/project contents.
- Internal source paths change to `dep/repkg`, but published paths remain exactly `ThirdParty/RePKG/LICENSE.txt`, `ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt`, and `ThirdParty/RePKG/UPSTREAM-PATCHES.md`.
- Never stage, overwrite, move, or attribute the pre-existing tracked modification `GUI_for_RePKG.exe` to this task. Never pass `-UpdateTrackedExecutable`.
- Keep `temp/` and the user-supplied v1.3.0 source design. Task/audit records stay ignored and outside Git.
- Keep the untracked status of `AGENTS.md`; update only durable path semantics and never stage it.
- The console SmokeTests and their positive machine summary remain the real gate. `dotnet test` is executed only to document the known zero-test limitation.
- Regression IDs carried through every relevant gate are `DOC-001`, `REL-001`, `RELEASE-002`, `UP-003`, and `TEST-001`.
- Use `apply_patch` for text edits. For bulk moves use one PowerShell process, resolved absolute paths, `-LiteralPath`, exact maps, and no globs.
- Before every commit, assert the staged set excludes `GUI_for_RePKG.exe` and `AGENTS.md`, run `git diff --cached --check`, and review the cached diff.
- Before its first task mutation, every implementing agent must satisfy the complete root `AGENTS.md` research gate itself. A new/subagent reads every Markdown file enumerated at `temp/maintenance-audit/` root to EOF and cannot reuse another agent's manifest; the continuing primary agent may use its own trusted manifest only after recomputing every path/hash and fully reading each new or changed document.

## File Responsibility Map

| File or tree | Final responsibility |
|---|---|
| `Directory.Build.props` | Route all SDK build/intermediate/publish artifacts to ignored `output/`, separated by project and pivot. |
| `WallpaperField.slnx` | Root solution entry for RePKG Core/Application, product, and smoke projects at final paths. |
| `src/wallpaper-field/` | Complete first-party WPF product and its project/manifest; physical directories are lowercase singular. |
| `src/wallpaper-field/WallpaperField.csproj` | Product/version metadata, external RePKG project reference, and linked public legal files. |
| `test/smoke/RepositoryPaths.cs` | Single test-only repository/product path resolver anchored by root `WallpaperField.slnx`. |
| `test/smoke/` | Existing console Smoke harness and source-inspection regressions, with no framework migration. |
| `dep/repkg/` | Unchanged vendored RePKG tree; its v1.2.2-era patch-record paths remain historical evidence, not current commands. |
| `tool/release.ps1` / `release.cmd` | Safe isolated release workflow rooted at the repository parent of `tool/`. |
| `tool/verify-repkg-compile-surface.ps1` | Evaluated RePKG compile-surface gate at the new vendored path. |
| `.github/workflows/ci.yml` | Final paths, compile-surface gate, layout contract, Smoke machine summary, package audits, and isolated release. |
| `doc/README.md` | Unique documentation index. |
| `doc/release/v1.2.2.md` | Current release facts. |
| `doc/plan/v1.3.0.md` | Sole active future route, preserving the complete approved Project Browser specification. |
| `doc/archive/` | v1.2.1 release and completed v1.2.2 planning/reorganization records. |
| `README.md` | User entry for v1.2.2 plus prominent v1.3.0 link and executable current commands. |
| `THIRD-PARTY-NOTICES.md` | Current repository-source legal locations; release layout remains documented separately. |
| `AGENTS.md` | Untracked durable repository rules with final paths. |
| `temp/maintenance-audit/{01,04,05}-*.md` | Dated untracked architecture, verification, and roadmap addenda after final evidence exists. |

---

### Task 1: Move the WPF product under `src` and centralize generated output

**Files:**

- Create: `Directory.Build.props`
- Create: `tests/WallpaperField.SmokeTests/RepositoryPaths.cs` (moves to `test/smoke/` in Task 2)
- Move: `Application/` → `src/wallpaper-field/application/`
- Move: `Composition/` → `src/wallpaper-field/composition/`
- Move: `Contracts/` → `src/wallpaper-field/contract/`
- Move: `Controls/` → `src/wallpaper-field/control/`
- Move: `Infrastructure/` → `src/wallpaper-field/infrastructure/`
- Move: `Models/` → `src/wallpaper-field/model/`
- Move: `Properties/` → `src/wallpaper-field/property/`
- Move: `Services/` → `src/wallpaper-field/service/`
- Move: `Themes/` → `src/wallpaper-field/theme/`
- Move: `ViewModels/` → `src/wallpaper-field/viewmodel/`, then `Sessions/` → `session/`
- Move: `Views/` → `src/wallpaper-field/view/`
- Move: `App.xaml`, `App.xaml.cs`, `GlobalUsings.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `WallpaperField.csproj`, `app.manifest` → `src/wallpaper-field/`
- Modify: `.gitignore`
- Modify: `WallpaperField.slnx`
- Modify: `src/wallpaper-field/App.xaml`
- Modify: `src/wallpaper-field/WallpaperField.csproj`
- Modify: `tests/WallpaperField.SmokeTests/WallpaperField.SmokeTests.csproj`
- Modify: `tests/WallpaperField.SmokeTests/AccessibilityRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/PerformanceRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/ReleaseContractTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/SelectionEfficiencyRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/UiStructureRegressionTests.cs`
- Modify: `tests/WallpaperField.SmokeTests/UpstreamBoundaryRegressionTests.cs`
- Delete after exact validation: root `artifacts/`, root `bin/`, root `obj/`, empty `Converters/`, smoke `bin/obj`, RePKG.Application `bin/obj`, RePKG.Core `bin/obj`

**Interfaces:**

- Consumes: product v1.2.2 baseline at `5b2aefa9fa50b5b18708110f4f8402fc2ef125f8`, plus the committed design/implementation-plan HEAD; current RePKG paths remain in place during this task.
- Produces: `src/wallpaper-field/WallpaperField.csproj`; centralized `output/`; `RepositoryPaths.RepositoryRoot`, `RepositoryPaths.ProductRoot`, `RepositoryPaths.RepositoryFile(params string[])`, and `RepositoryPaths.ProductFile(params string[])` for Task 2.

- [ ] **Step 1: Re-establish the execution baseline and record user-owned state**

Run from the repository root:

```powershell
Get-Content -Raw .\AGENTS.md
Get-Content -Raw .\temp\agent-work\20260822-1745-directory-doc-reorganization\task_plan.md
Get-Content -Raw .\temp\agent-work\20260822-1745-directory-doc-reorganization\findings.md
Get-Content -Raw .\temp\agent-work\20260822-1745-directory-doc-reorganization\progress.md
Get-Content -Raw .\temp\agent-work\20260822-1745-directory-doc-reorganization\research-manifest.md
git status --short --branch
git rev-parse HEAD
Get-FileHash -LiteralPath .\GUI_for_RePKG.exe -Algorithm SHA256
```

Expected: only the pre-existing `M GUI_for_RePKG.exe` and `?? AGENTS.md` are outside the committed plan/spec baseline. Record the observed HEAD and EXE SHA-256 in `progress.md` with `apply_patch` before continuing.

- [ ] **Step 2: Run the fresh pre-migration behavior baseline**

```powershell
dotnet restore .\WallpaperField.slnx --configfile .\NuGet.Config
dotnet build .\WallpaperField.slnx -c Release --no-restore --no-incremental
dotnet run --project .\tests\WallpaperField.SmokeTests\WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore
pwsh -NoProfile -File .\scripts\verify-repkg-compile-surface.ps1 -Mode Verify
```

Expected: every command exits 0; Smoke output ends with exactly one `SMOKE_RESULT` whose `tests` and `assertions` are positive and whose `failed` value is 0; compile-surface missing/unexpected counts are 0.

Run the intentional failure as a probe that itself exits successfully only when the harness fails:

```powershell
$output = @(& dotnet run --project .\tests\WallpaperField.SmokeTests\WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore -- --verify-failure-exit 2>&1)
$probeExit = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($probeExit -eq 0) { throw 'Intentional Smoke failure returned exit code 0.' }
Write-Host "Intentional Smoke failure returned expected nonzero exit code $probeExit."
exit 0
```

- [ ] **Step 3: Add the centralized .NET artifacts contract**

Create `Directory.Build.props` with `apply_patch`:

```xml
<Project>
  <PropertyGroup>
    <ArtifactsPath>$(MSBuildThisFileDirectory)output</ArtifactsPath>
  </PropertyGroup>
</Project>
```

Replace `.gitignore` with this exact task-relevant layout while retaining the existing IDE/user ignores:

```gitignore
bin/
obj/
/output/
.vs/
*.user
*.suo

# Local research, diagnostics, and other files that must not be committed
/temp/
```

- [ ] **Step 4: Delete only the inventoried rebuildable generated directories**

Before the command, tell the user that these exact generated/empty paths are being removed and that `temp/` and the root EXE are excluded. Then run this single PowerShell block:

```powershell
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Get-Location).Path).TrimEnd([char[]]@('\', '/'))
$workspacePrefix = $workspaceRoot + [IO.Path]::DirectorySeparatorChar
$targets = @(
    'artifacts',
    'bin',
    'obj',
    'Converters',
    'tests/WallpaperField.SmokeTests/bin',
    'tests/WallpaperField.SmokeTests/obj',
    'ThirdParty/RePKG/Source/RePKG.Application/bin',
    'ThirdParty/RePKG/Source/RePKG.Application/obj',
    'ThirdParty/RePKG/Source/RePKG.Core/bin',
    'ThirdParty/RePKG/Source/RePKG.Core/obj'
)
$validatedTargets = [Collections.Generic.List[object]]::new()
foreach ($relative in $targets) {
    $full = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $relative))
    if (-not $full.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Deletion target escaped the workspace: $full"
    }
    $tracked = @(& git ls-files -- $relative "$relative/**")
    if ($LASTEXITCODE -ne 0 -or $tracked.Count -ne 0) {
        throw "Deletion target has tracked files or could not be checked: $relative"
    }
    $exists = Test-Path -LiteralPath $full
    if ($exists) {
        $item = Get-Item -LiteralPath $full -Force
        if (-not $item.PSIsContainer) {
            throw "Approved deletion target is no longer a directory: $full"
        }
        $treeItems = @($item)
        if ($item.PSIsContainer) {
            $treeItems += @(Get-ChildItem -LiteralPath $full -Recurse -Force -ErrorAction Stop)
        }
        $reparseItems = @($treeItems | Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        })
        if ($reparseItems.Count -ne 0) {
            throw "Deletion tree contains a reparse point: $($reparseItems.FullName -join ', ')"
        }
    }
    $validatedTargets.Add([pscustomobject]@{
        Relative = $relative
        Full = $full
        Exists = $exists
    }) | Out-Null
}
foreach ($target in $validatedTargets) {
    if ($target.Exists) {
        Remove-Item -LiteralPath $target.Full -Recurse -Force
    }
}
foreach ($target in $validatedTargets) {
    if (Test-Path -LiteralPath $target.Full) {
        throw "Validated deletion target still exists: $($target.Relative)"
    }
}
```

Expected: all listed paths are absent; `GUI_for_RePKG.exe` hash and all of `temp/` are unchanged. Record that the removed material was untracked and rebuildable.

- [ ] **Step 5: Move the complete product using an exact absolute-path map**

```powershell
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Get-Location).Path).TrimEnd([char[]]@('\', '/'))
$workspacePrefix = $workspaceRoot + [IO.Path]::DirectorySeparatorChar
function Assert-NoReparseTree {
    param([string]$LiteralPath)
    $rootItem = Get-Item -LiteralPath $LiteralPath -Force
    $treeItems = @($rootItem)
    if ($rootItem.PSIsContainer) {
        $treeItems += @(Get-ChildItem -LiteralPath $LiteralPath -Recurse -Force -ErrorAction Stop)
    }
    $reparseItems = @($treeItems | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    })
    if ($reparseItems.Count -ne 0) {
        throw "Move source tree contains a reparse point: $($reparseItems.FullName -join ', ')"
    }
}
$moves = [ordered]@{
    'Application' = 'src/wallpaper-field/application'
    'Composition' = 'src/wallpaper-field/composition'
    'Contracts' = 'src/wallpaper-field/contract'
    'Controls' = 'src/wallpaper-field/control'
    'Infrastructure' = 'src/wallpaper-field/infrastructure'
    'Models' = 'src/wallpaper-field/model'
    'Properties' = 'src/wallpaper-field/property'
    'Services' = 'src/wallpaper-field/service'
    'Themes' = 'src/wallpaper-field/theme'
    'ViewModels' = 'src/wallpaper-field/viewmodel'
    'Views' = 'src/wallpaper-field/view'
    'App.xaml' = 'src/wallpaper-field/App.xaml'
    'App.xaml.cs' = 'src/wallpaper-field/App.xaml.cs'
    'GlobalUsings.cs' = 'src/wallpaper-field/GlobalUsings.cs'
    'MainWindow.xaml' = 'src/wallpaper-field/MainWindow.xaml'
    'MainWindow.xaml.cs' = 'src/wallpaper-field/MainWindow.xaml.cs'
    'WallpaperField.csproj' = 'src/wallpaper-field/WallpaperField.csproj'
    'app.manifest' = 'src/wallpaper-field/app.manifest'
}
foreach ($entry in $moves.GetEnumerator()) {
    $source = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $entry.Key))
    $destination = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $entry.Value))
    foreach ($path in @($source, $destination)) {
        if (-not $path.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Move path escaped the workspace: $path"
        }
    }
    if (-not (Test-Path -LiteralPath $source) -or (Test-Path -LiteralPath $destination)) {
        throw "Move precondition failed: $source -> $destination"
    }
    Assert-NoReparseTree -LiteralPath $source
}
foreach ($entry in $moves.GetEnumerator()) {
    $source = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $entry.Key))
    $destination = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $entry.Value))
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
    Move-Item -LiteralPath $source -Destination $destination
}
$sessions = Join-Path $workspaceRoot 'src/wallpaper-field/viewmodel/Sessions'
$sessionRename = Join-Path $workspaceRoot 'src/wallpaper-field/viewmodel/session.__case_rename__'
$session = Join-Path $workspaceRoot 'src/wallpaper-field/viewmodel/session'
if (-not (Test-Path -LiteralPath $sessions -PathType Container) -or (Test-Path -LiteralPath $sessionRename)) {
    throw 'Session directory rename precondition failed.'
}
$sessionsItem = Get-Item -LiteralPath $sessions -Force
if ($sessionsItem.Name -cne 'Sessions') {
    throw "Session source does not have the expected exact casing: $($sessionsItem.Name)"
}
Assert-NoReparseTree -LiteralPath $sessions
Move-Item -LiteralPath $sessions -Destination $sessionRename
try {
    if (Test-Path -LiteralPath $session) {
        throw 'Lowercase session destination unexpectedly exists after the temporary rename.'
    }
    Move-Item -LiteralPath $sessionRename -Destination $session
}
catch {
    if ((Test-Path -LiteralPath $sessionRename) -and -not (Test-Path -LiteralPath $sessions)) {
        Move-Item -LiteralPath $sessionRename -Destination $sessions
    }
    throw
}
$sessionItem = Get-Item -LiteralPath $session -Force
if ($sessionItem.Name -cne 'session') {
    throw "Session destination does not have the required lowercase casing: $($sessionItem.Name)"
}
```

- [ ] **Step 6: Make the moved product project self-contained at its new root**

Replace `src/wallpaper-field/WallpaperField.csproj` with the following Task 1 form. RePKG intentionally remains at its old physical path until Task 2; the `Link` metadata already preserves the public publish paths.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <DefaultItemExcludes>$(DefaultItemExcludes);..\..\temp\**</DefaultItemExcludes>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>WallpaperField</AssemblyName>
    <RootNamespace>WallpaperField</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <ApplicationTitle>Wallpaper Field</ApplicationTitle>
    <Product>Wallpaper Field</Product>
    <Description>Wallpaper Engine workshop catalog and safe scene.pkg extractor</Description>
    <Version>1.2.2</Version>
    <FileVersion>1.2.2.0</FileVersion>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="XamlAnimatedGif" Version="2.3.2" />
    <ProjectReference Include="..\..\ThirdParty\RePKG\Source\RePKG.Application\RePKG.Application.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Include="..\..\THIRD-PARTY-NOTICES.md" Link="THIRD-PARTY-NOTICES.md" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
    <None Include="..\..\ThirdParty\RePKG\LICENSE.txt" Link="ThirdParty\RePKG\LICENSE.txt" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
    <None Include="..\..\ThirdParty\RePKG\THIRD-PARTY-NOTICES.txt" Link="ThirdParty\RePKG\THIRD-PARTY-NOTICES.txt" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
    <None Include="..\..\ThirdParty\RePKG\UPSTREAM-PATCHES.md" Link="ThirdParty\RePKG\UPSTREAM-PATCHES.md" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

Update the four merged dictionary sources in `src/wallpaper-field/App.xaml` to `theme/Tokens.xaml`, `theme/AccessibilityMotion.xaml`, `theme/BaseControls.xaml`, and `theme/DomainComponents.xaml`, preserving their order.

- [ ] **Step 7: Point the solution and still-unmoved smoke project at the product**

Use this Task 1 `WallpaperField.slnx`:

```xml
<Solution>
  <Project Path="ThirdParty\RePKG\Source\RePKG.Core\RePKG.Core.csproj" />
  <Project Path="ThirdParty\RePKG\Source\RePKG.Application\RePKG.Application.csproj" />
  <Project Path="src\wallpaper-field\WallpaperField.csproj" />
  <Project Path="tests\WallpaperField.SmokeTests\WallpaperField.SmokeTests.csproj" />
</Solution>
```

Change the smoke project reference to:

```xml
<ProjectReference Include="..\..\src\wallpaper-field\WallpaperField.csproj" />
```

- [ ] **Step 8: Add one test-only repository/product path boundary**

Create `tests/WallpaperField.SmokeTests/RepositoryPaths.cs`:

```csharp
using System.IO;

internal static class RepositoryPaths
{
    private static readonly Lazy<string> RepositoryRootValue = new(FindRepositoryRoot);

    internal static string RepositoryRoot => RepositoryRootValue.Value;

    internal static string ProductRoot => Path.Combine(
        RepositoryRoot,
        "src",
        "wallpaper-field");

    internal static string RepositoryFile(params string[] pathSegments)
        => RequireFile(RepositoryRoot, pathSegments);

    internal static string ProductFile(params string[] pathSegments)
        => RequireFile(ProductRoot, pathSegments);

    private static string RequireFile(string root, IReadOnlyList<string> pathSegments)
    {
        var candidate = root;
        foreach (var segment in pathSegments)
        {
            candidate = Path.Combine(candidate, segment);
        }

        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"Could not locate repository file {candidate}.");
        }

        return candidate;
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "WallpaperField.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Wallpaper Field repository root.");
    }
}
```

- [ ] **Step 9: Update every source-inspection test to the shared boundary**

Apply these exact semantic replacements and remove each private `FindRepositoryFile`/`FindRepositoryRoot` implementation:

| Test | Required replacement |
|---|---|
| `AccessibilityRegressionTests.cs` | Use `RepositoryPaths.ProductFile`; change directory segments `Themes`→`theme`, `Views`→`view`; expected app sources become `theme/<file>`; runtime URI becomes `/WallpaperField;component/theme/EndfieldTheme.xaml`. |
| `PerformanceRegressionTests.cs` | Use `RepositoryPaths.ProductFile`; map `Models`→`model`, `Views`→`view`, `Controls`→`control`, `ViewModels`→`viewmodel`. |
| `SelectionEfficiencyRegressionTests.cs` | Use `RepositoryPaths.ProductFile`; map `Themes`→`theme`, `Views`→`view`; root XAML code-behind calls remain file names under `ProductRoot`. |
| `UiStructureRegressionTests.cs` | Use `RepositoryPaths.ProductFile`; map every `Views`→`view` and `Themes`→`theme`; do not change CLR type names such as `WallpaperField.Views.ProblemCenterView`. |
| `ReleaseContractTests.cs` | Load the manifest with `RepositoryPaths.ProductFile("app.manifest")`; remove its root finder. |
| `UpstreamBoundaryRegressionTests.cs` | Use `RepositoryPaths.RepositoryRoot` and recursively enumerate `RepositoryPaths.ProductRoot`; read the safe unpack service with `ProductFile("service", "RePkgWallpaperUnpackService.cs")`; for Task 1 load RePKG.Application with `RepositoryFile("ThirdParty", "RePKG", "Source", "RePKG.Application", "RePKG.Application.csproj")`; keep the expected Compile rule tuples unchanged. |

Run:

```powershell
$legacyMatches = @(& rg -n 'FindRepositoryFile|FindRepositoryRoot|Path.Combine\("(Themes|Views|Models|Controls|ViewModels|Services)' .\tests\WallpaperField.SmokeTests)
$legacyExit = $LASTEXITCODE
$legacyMatches | ForEach-Object { Write-Host $_ }
if ($legacyExit -gt 1) { throw "rg failed with exit code $legacyExit." }
if ($legacyMatches.Count -ne 0) { throw 'Obsolete test helper or product-directory references remain.' }
```

Expected: no obsolete private helper or old product-directory match.

- [ ] **Step 10: Restore and verify the Task 1 product move**

```powershell
dotnet restore .\WallpaperField.slnx --configfile .\NuGet.Config
dotnet build .\WallpaperField.slnx -c Release --no-restore --no-incremental
dotnet run --project .\tests\WallpaperField.SmokeTests\WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore
pwsh -NoProfile -File .\scripts\verify-repkg-compile-surface.ps1 -Mode Verify
if (-not (Test-Path -LiteralPath .\output\bin\WallpaperField\release_net10.0-windows\WallpaperField.exe -PathType Leaf)) {
    throw 'Centralized Release executable is missing.'
}
$legacyProductPaths = @(
    'Application', 'Composition', 'Contracts', 'Controls', 'Infrastructure',
    'Models', 'Properties', 'Services', 'Themes', 'ViewModels', 'Views',
    'App.xaml', 'App.xaml.cs', 'GlobalUsings.cs', 'MainWindow.xaml',
    'MainWindow.xaml.cs', 'WallpaperField.csproj', 'app.manifest',
    'artifacts', 'bin', 'obj', 'Converters'
)
$remainingLegacy = @($legacyProductPaths | Where-Object { Test-Path -LiteralPath $_ })
if ($remainingLegacy.Count -ne 0) {
    throw "Legacy product/generated paths remain: $($remainingLegacy -join ', ')"
}
$unexpectedGenerated = @(Get-ChildItem -LiteralPath .\src, .\tests, .\ThirdParty -Directory -Recurse -Force -ErrorAction Stop |
    Where-Object { $_.Name -in @('bin', 'obj') })
if ($unexpectedGenerated.Count -ne 0) {
    throw "Generated bin/obj directories escaped output/: $($unexpectedGenerated.FullName -join ', ')"
}
```

Expected: all gates and structural assertions pass; old root product directories/files are absent, and no `bin/obj/artifacts` path is recreated outside `output/`.

- [ ] **Step 11: Commit only the product/output migration**

```powershell
git add -- .gitignore Directory.Build.props WallpaperField.slnx
git add -A -- Application Composition Contracts Controls Infrastructure Models Properties Services Themes ViewModels Views App.xaml App.xaml.cs GlobalUsings.cs MainWindow.xaml MainWindow.xaml.cs WallpaperField.csproj app.manifest src
git add -- tests/WallpaperField.SmokeTests/WallpaperField.SmokeTests.csproj tests/WallpaperField.SmokeTests/RepositoryPaths.cs tests/WallpaperField.SmokeTests/AccessibilityRegressionTests.cs tests/WallpaperField.SmokeTests/PerformanceRegressionTests.cs tests/WallpaperField.SmokeTests/ReleaseContractTests.cs tests/WallpaperField.SmokeTests/SelectionEfficiencyRegressionTests.cs tests/WallpaperField.SmokeTests/UiStructureRegressionTests.cs tests/WallpaperField.SmokeTests/UpstreamBoundaryRegressionTests.cs
$staged = @(git diff --cached --name-only)
if ($staged -contains 'GUI_for_RePKG.exe' -or $staged -contains 'AGENTS.md') { throw 'User-owned path entered the index.' }
git diff --cached --check
git diff --cached --stat
git commit -m "refactor: move application source under src"
```

---

### Task 2: Move RePKG, SmokeTests, and build tools to their final support paths

**Files:**

- Move: `ThirdParty/RePKG/` → `dep/repkg/` without tracked content edits
- Move: `tests/WallpaperField.SmokeTests/` → `test/smoke/`
- Move: `scripts/verify-repkg-compile-surface.ps1` → `tool/verify-repkg-compile-surface.ps1`
- Move/rename: `build-release.ps1` → `tool/release.ps1`
- Move/rename: `build-release.cmd` → `tool/release.cmd`
- Modify: `src/wallpaper-field/WallpaperField.csproj`
- Modify: `WallpaperField.slnx`
- Modify: `test/smoke/UpstreamBoundaryRegressionTests.cs`
- Modify: `tool/verify-repkg-compile-surface.ps1`
- Modify: `tool/release.ps1`
- Modify: `tool/release.cmd`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**

- Consumes: `RepositoryPaths` and `src/wallpaper-field` from Task 1.
- Produces: final app/test/dep/tool paths, unchanged `dep/repkg` tracked bytes, and a release tool whose internal source paths differ from its public ZIP paths.

- [ ] **Step 1: Verify the Task 1 checkpoint and calculate the RePKG tracked-tree fingerprint**

```powershell
Get-Content -Raw .\temp\agent-work\20260822-1745-directory-doc-reorganization\task_plan.md
Get-Content -Raw .\AGENTS.md
git status --short --branch
git log -1 --oneline
$repkgPaths = @(& git ls-files -- 'ThirdParty/RePKG/**')
if ($LASTEXITCODE -ne 0 -or $repkgPaths.Count -eq 0) { throw 'Could not enumerate tracked RePKG files.' }
$beforeManifest = @($repkgPaths | ForEach-Object {
    $relative = $_.Substring('ThirdParty/RePKG/'.Length)
    $file = Get-Item -LiteralPath $_
    $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    "$relative|$($file.Length)|$hash"
} | Sort-Object)
```

Expected: the only dirty user-owned paths remain the root EXE and untracked `AGENTS.md`; Task 1 is committed.

- [ ] **Step 2: Move the three support trees and two release wrappers with exact paths**

Run in the same PowerShell process that holds `$beforeManifest`:

```powershell
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Get-Location).Path).TrimEnd([char[]]@('\', '/'))
$workspacePrefix = $workspaceRoot + [IO.Path]::DirectorySeparatorChar
function Assert-NoReparseTree {
    param([string]$LiteralPath)
    $rootItem = Get-Item -LiteralPath $LiteralPath -Force
    $treeItems = @($rootItem)
    if ($rootItem.PSIsContainer) {
        $treeItems += @(Get-ChildItem -LiteralPath $LiteralPath -Recurse -Force -ErrorAction Stop)
    }
    $reparseItems = @($treeItems | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    })
    if ($reparseItems.Count -ne 0) {
        throw "Move source tree contains a reparse point: $($reparseItems.FullName -join ', ')"
    }
}
$moves = [ordered]@{
    'ThirdParty/RePKG' = 'dep/repkg'
    'tests/WallpaperField.SmokeTests' = 'test/smoke'
    'scripts/verify-repkg-compile-surface.ps1' = 'tool/verify-repkg-compile-surface.ps1'
    'build-release.ps1' = 'tool/release.ps1'
    'build-release.cmd' = 'tool/release.cmd'
}
foreach ($entry in $moves.GetEnumerator()) {
    $source = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $entry.Key))
    $destination = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $entry.Value))
    foreach ($path in @($source, $destination)) {
        if (-not $path.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Move path escaped the workspace: $path"
        }
    }
    if (-not (Test-Path -LiteralPath $source) -or (Test-Path -LiteralPath $destination)) {
        throw "Move precondition failed: $source -> $destination"
    }
    Assert-NoReparseTree -LiteralPath $source
}
foreach ($entry in $moves.GetEnumerator()) {
    $source = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $entry.Key))
    $destination = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $entry.Value))
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
    Move-Item -LiteralPath $source -Destination $destination
}
foreach ($emptyRoot in @('ThirdParty', 'tests', 'scripts')) {
    $full = Join-Path $workspaceRoot $emptyRoot
    if ((Get-ChildItem -LiteralPath $full -Force).Count -ne 0) {
        throw "Legacy root is not empty after its exact move: $full"
    }
    Remove-Item -LiteralPath $full
}
$afterManifest = @($beforeManifest | ForEach-Object {
    $parts = $_ -split '\|', 3
    $relative = $parts[0]
    $path = Join-Path (Join-Path $workspaceRoot 'dep/repkg') $relative
    $file = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    "$relative|$($file.Length)|$hash"
} | Sort-Object)
$difference = @(Compare-Object -ReferenceObject $beforeManifest -DifferenceObject $afterManifest -CaseSensitive)
if ($difference.Count -ne 0) { throw "RePKG tracked-tree bytes changed: $($difference | Out-String)" }
```

- [ ] **Step 3: Change app and solution references to `dep/test/src` final paths**

In `src/wallpaper-field/WallpaperField.csproj`, replace only the four external source paths:

```xml
<ProjectReference Include="..\..\dep\repkg\Source\RePKG.Application\RePKG.Application.csproj" />
<None Include="..\..\dep\repkg\LICENSE.txt" Link="ThirdParty\RePKG\LICENSE.txt" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
<None Include="..\..\dep\repkg\THIRD-PARTY-NOTICES.txt" Link="ThirdParty\RePKG\THIRD-PARTY-NOTICES.txt" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
<None Include="..\..\dep\repkg\UPSTREAM-PATCHES.md" Link="ThirdParty\RePKG\UPSTREAM-PATCHES.md" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
```

Keep the root `THIRD-PARTY-NOTICES.md` `None Include` unchanged. Replace `WallpaperField.slnx` with:

```xml
<Solution>
  <Project Path="dep\repkg\Source\RePKG.Core\RePKG.Core.csproj" />
  <Project Path="dep\repkg\Source\RePKG.Application\RePKG.Application.csproj" />
  <Project Path="src\wallpaper-field\WallpaperField.csproj" />
  <Project Path="test\smoke\WallpaperField.SmokeTests.csproj" />
</Solution>
```

In `test/smoke/UpstreamBoundaryRegressionTests.cs`, replace the RePKG project call with:

```csharp
RepositoryPaths.RepositoryFile(
    "dep",
    "repkg",
    "Source",
    "RePKG.Application",
    "RePKG.Application.csproj")
```

- [ ] **Step 4: Re-root and rename the compile-surface verifier**

In `tool/verify-repkg-compile-surface.ps1`, retain:

```powershell
$repositoryRoot = Split-Path -Parent $PSScriptRoot
```

and set the default project to:

```powershell
$ProjectPath = Join-Path $repositoryRoot 'dep/repkg/Source/RePKG.Application/RePKG.Application.csproj'
```

Do not alter whitelist roles, output fields, or verify conditions.

- [ ] **Step 5: Re-root `tool/release.ps1` while preserving its distribution contract**

Replace the former `$projectRoot = Get-NormalizedPath $PSScriptRoot` block with:

```powershell
$repositoryRoot = Get-NormalizedPath (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repositoryRoot 'src\wallpaper-field\WallpaperField.csproj'
$solutionPath = Join-Path $repositoryRoot 'WallpaperField.slnx'
$nugetConfigPath = Join-Path $repositoryRoot 'NuGet.Config'
$rootExecutable = Join-Path $repositoryRoot 'GUI_for_RePKG.exe'
$releaseNotesPath = Join-Path $repositoryRoot 'docs\releases\v1.2.2.md'
```

Mechanically rename every remaining internal `$projectRoot` variable use to `$repositoryRoot`. Replace the in-repository output allowance with `temp` or `output`:

```powershell
$allowedTemp = Join-Path $repositoryRoot 'temp'
$allowedOutput = Join-Path $repositoryRoot 'output'
$isAllowedTemp = Test-SameOrChildPath -Child $outputPath -Parent $allowedTemp
$isAllowedOutput = Test-SameOrChildPath -Child $outputPath -Parent $allowedOutput
if (-not $isAllowedTemp -and -not $isAllowedOutput)
{
    throw 'In-repository release output is allowed only below temp or output.'
}
```

Change every release-source lookup from `ThirdParty\RePKG\...` under `$repositoryRoot` to `dep\repkg\...`. Do not change any of these public contract strings:

```text
ThirdParty/RePKG/LICENSE.txt
ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt
ThirdParty/RePKG/UPSTREAM-PATCHES.md
```

Replace `tool/release.cmd` with:

```bat
@echo off
setlocal
if "%~1"=="" (
  echo Usage: release.cmd ^<OutputDirectory^> [-UpdateTrackedExecutable]
  exit /b 2
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" %*
set "releaseExitCode=%ERRORLEVEL%"
endlocal & exit /b %releaseExitCode%
```

- [ ] **Step 6: Update CI callers and retain TEST-001 safeguards**

In `.github/workflows/ci.yml`:

- app analyzer path: `.\src\wallpaper-field\WallpaperField.csproj`
- smoke analyzer/run paths: `.\test\smoke\WallpaperField.SmokeTests.csproj`
- release call: `.\tool\release.ps1`
- release temp label: `WallpaperField-v1.2.2-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT`
- keep the positive `SMOKE_RESULT` parser unchanged
- keep the intentional-failure step's explicit `exit 0`

Add this step immediately after solution build:

```yaml
      - name: Verify RePKG compile surface
        shell: pwsh
        run: .\tool\verify-repkg-compile-surface.ps1 -Mode Verify
```

- [ ] **Step 7: Parse scripts and run final-path build/Smoke/compile gates**

```powershell
$scriptPaths = @('.\tool\release.ps1', '.\tool\verify-repkg-compile-surface.ps1')
foreach ($scriptPath in $scriptPaths) {
    $tokens = $null
    $errors = $null
    [Management.Automation.Language.Parser]::ParseFile(
        (Resolve-Path -LiteralPath $scriptPath),
        [ref]$tokens,
        [ref]$errors) | Out-Null
    if ($errors.Count -ne 0) { throw "$scriptPath parse errors: $($errors | Out-String)" }
}
dotnet restore .\WallpaperField.slnx --configfile .\NuGet.Config
dotnet build .\WallpaperField.slnx -c Release --no-restore --no-incremental
dotnet run --project .\test\smoke\WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore
pwsh -NoProfile -File .\tool\verify-repkg-compile-surface.ps1 -Mode Verify
```

Run the final-path intentional-failure probe:

```powershell
$output = @(& dotnet run --project .\test\smoke\WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore -- --verify-failure-exit 2>&1)
$probeExit = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($probeExit -eq 0) { throw 'Intentional Smoke failure returned exit code 0.' }
Write-Host "Intentional Smoke failure returned expected nonzero exit code $probeExit."
exit 0
```

Expected: the wrapper exits 0 only after the child returns nonzero.

- [ ] **Step 8: Exercise the moved release tool in an isolated task directory**

```powershell
$candidate = Join-Path (Resolve-Path .\temp\agent-work\20260822-1745-directory-doc-reorganization).Path ("support-candidate-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$rootHashBefore = (Get-FileHash -LiteralPath .\GUI_for_RePKG.exe -Algorithm SHA256).Hash
pwsh -NoProfile -File .\tool\release.ps1 -OutputDirectory $candidate
if ($LASTEXITCODE -ne 0) { throw "Release tool exited with $LASTEXITCODE." }
$rootHashAfter = (Get-FileHash -LiteralPath .\GUI_for_RePKG.exe -Algorithm SHA256).Hash
if ($rootHashBefore -ne $rootHashAfter) { throw 'Default release changed the user-owned root EXE.' }
Get-ChildItem -LiteralPath $candidate | Select-Object Name, Length
```

Expected: `RELEASE_RESULT` is emitted, candidate has exactly five files, ZIP has the script-enforced nine entries, and the root EXE hash is unchanged. This candidate is test evidence, not a formal release.

- [ ] **Step 9: Commit only the support-surface migration**

```powershell
git add -A -- ThirdParty tests scripts build-release.ps1 build-release.cmd dep test tool
git add -- src/wallpaper-field/WallpaperField.csproj WallpaperField.slnx .github/workflows/ci.yml
$staged = @(git diff --cached --name-only)
if ($staged -contains 'GUI_for_RePKG.exe' -or $staged -contains 'AGENTS.md') { throw 'User-owned path entered the index.' }
git diff --cached --check
git diff --cached --stat
git commit -m "refactor: move dependencies tests and tools"
```

---

### Task 3: Establish the current/future/archive documentation lifecycle

**Files:**

- Create: `doc/README.md`
- Create from approved source data: `doc/plan/v1.3.0.md`
- Move: `docs/images/*.png` → `doc/img/*.png`
- Move: `docs/EXTENDING.md` → `doc/extension.md`
- Move: `docs/releases/v1.2.2.md` → `doc/release/v1.2.2.md`
- Move: `docs/releases/v1.2.1.md` → `doc/archive/release/v1.2.1.md`
- Move: completed v1.2.2 design → `doc/archive/v1.2.2/roadmap-design.md`
- Move: completed v1.2.2 plan → `doc/archive/v1.2.2/roadmap-implementation.md`
- Modify: `README.md`
- Modify: `THIRD-PARTY-NOTICES.md`
- Modify: `doc/extension.md`
- Modify: `doc/release/v1.2.2.md`
- Modify: two archived v1.2.2 records (banner/current pointers only)
- Modify: `tool/release.ps1`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**

- Consumes: final `src/test/dep/tool` paths and the user source design under ignored `temp/agent-work/20260821-1655-v1-3-gallery-plan/`.
- Produces: unique `doc/README.md`, current v1.2.2 release record, complete maintained `doc/plan/v1.3.0.md`, historical archive, current image links, and a CI-enforced tracked-directory contract.

Before Step 1, re-read `AGENTS.md` and the current task `task_plan.md`; if this task is assigned to a new agent, complete the global full-research gate before moving any document or build configuration.

- [ ] **Step 1: Move every tracked legacy document/image to its exact lifecycle path**

```powershell
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Get-Location).Path).TrimEnd([char[]]@('\', '/'))
$workspacePrefix = $workspaceRoot + [IO.Path]::DirectorySeparatorChar
$moves = [ordered]@{
    'docs/images/scan-center.png' = 'doc/img/scan-center.png'
    'docs/images/output-library.png' = 'doc/img/output-library.png'
    'docs/images/problem-center.png' = 'doc/img/problem-center.png'
    'docs/EXTENDING.md' = 'doc/extension.md'
    'docs/releases/v1.2.2.md' = 'doc/release/v1.2.2.md'
    'docs/releases/v1.2.1.md' = 'doc/archive/release/v1.2.1.md'
    'docs/superpowers/specs/2026-08-21-wallpaper-field-v1-2-2-roadmap-design.md' = 'doc/archive/v1.2.2/roadmap-design.md'
    'docs/superpowers/plans/2026-08-21-wallpaper-field-v1-2-2-roadmap.md' = 'doc/archive/v1.2.2/roadmap-implementation.md'
}
$beforeHashes = @{}
foreach ($entry in $moves.GetEnumerator()) {
    $source = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $entry.Key))
    $destination = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $entry.Value))
    foreach ($path in @($source, $destination)) {
        if (-not $path.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Document move escaped the workspace: $path"
        }
    }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Test-Path -LiteralPath $destination)) {
        throw "Document move precondition failed: $source -> $destination"
    }
    $sourceItem = Get-Item -LiteralPath $source -Force
    if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Document move source is a reparse point: $source"
    }
    $beforeHashes[$entry.Key] = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
}
foreach ($entry in $moves.GetEnumerator()) {
    $source = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $entry.Key))
    $destination = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $entry.Value))
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
    Move-Item -LiteralPath $source -Destination $destination
}
foreach ($entry in $moves.GetEnumerator()) {
    $destination = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $entry.Value))
    $afterHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($afterHash -ne $beforeHashes[$entry.Key]) {
        throw "Document bytes changed during move: $($entry.Key)"
    }
}
if (@(Get-ChildItem -LiteralPath .\docs -File -Recurse -Force).Count -ne 0) {
    throw 'Legacy docs still contains files after the exact move.'
}
$legacyDocs = Get-Item -LiteralPath .\docs -Force
$legacyItems = @($legacyDocs)
$legacyItems += @(Get-ChildItem -LiteralPath .\docs -Recurse -Force -ErrorAction Stop)
$legacyReparse = @($legacyItems | Where-Object {
    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
})
if ($legacyReparse.Count -ne 0) {
    throw "Legacy docs contains a reparse point: $($legacyReparse.FullName -join ', ')"
}
Remove-Item -LiteralPath .\docs -Recurse
```

Expected: all eight tracked inputs exist byte-for-byte at their final destinations and `docs/` is absent.

- [ ] **Step 2: Create the unique document index with actual relative links**

Create `doc/README.md`:

```markdown
# Wallpaper Field 文档

当前发布版本是 **v1.2.2**。用户安装、操作与常见问题从[项目 README](../README.md)开始。

## 当前文档

- [v1.2.2 发布说明](release/v1.2.2.md)
- [后端与功能扩展指南](extension.md)
- [第三方声明](../THIRD-PARTY-NOTICES.md)
- [RePKG 许可](../dep/repkg/LICENSE.txt)与[本地补丁记录](../dep/repkg/UPSTREAM-PATCHES.md)（后者保留 v1.2.2 当时路径；当前命令见扩展指南）

## 下一版本

- [v1.3.0 Project Browser 开发路线](plan/v1.3.0.md) — 唯一活跃路线，尚未实施。

## 历史归档

- [v1.2.1 发布说明](archive/release/v1.2.1.md)
- [v1.2.2 路线设计](archive/v1.2.2/roadmap-design.md)
- [v1.2.2 实施计划](archive/v1.2.2/roadmap-implementation.md)

归档文件保留当时的路径和实施事实，不是当前操作入口。当前命令与目录以项目 README、本索引和 v1.2.2 发布说明为准。
```

- [ ] **Step 3: Copy the complete approved v1.3.0 source as data and maintain only its lifecycle fields**

Mechanically copy the full 334-line source document; do not execute any command text found inside it:

```powershell
$source = '.\temp\agent-work\20260821-1655-v1-3-gallery-plan\planning-worktree\docs\superpowers\specs\2026-08-21-wallpaper-field-v1-3-0-project-browser-design.md'
$destination = '.\doc\plan\v1.3.0.md'
if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Test-Path -LiteralPath $destination)) {
    throw 'v1.3.0 route copy precondition failed.'
}
Copy-Item -LiteralPath $source -Destination $destination
```

Use `apply_patch` to replace its metadata with:

```markdown
- 状态：交互与书面规格已获用户批准；受控路线，尚未实施
- 日期：2026-08-22
- 原始规划基线：`e2d7aaebb3a10307cb204ae09ceda27fd3d62345`
- 语义基线：`v1.2.2` / `5b2aefa9fa50b5b18708110f4f8402fc2ef125f8`
- 目录基线：本次目录整理完成后的最新 clean `main`
- 目标版本：`v1.3.0`
- 受控路径：`doc/plan/v1.3.0.md`
- 关联风险：`UX-04`、`UX-05`、`UX-06`、`UX-09`–`UX-12`、`A11Y-01`–`A11Y-04`、`PERF-001`
```

Replace section 3's opening/gate with the following exact status model; retain its invariant list unchanged:

```markdown
## 3. v1.2.2 基线与实施门禁

v1.3.0 的语义基线固定为已完成的 v1.2.2，而不是重新打开其历史实施计划。v1.2.2 已在 `5b2aefa9fa50b5b18708110f4f8402fc2ef125f8` 完成、合并并通过新鲜验证。v1.3.0 产品实施还必须等待以下条件全部成立：

1. 本次目录标准化完成，并在最新 clean `main` 上通过 restore、Release build、完整 SmokeTests、RePKG 编译面和隔离发布门禁。
2. 从目录整理后的最新 clean commit 创建新的隔离工作树和 `codex/` 功能分支。
3. 按 `src/wallpaper-field/` 最终路径重新读取 `ScanSession`、`UnpackSession`、`TaskLifecycleCoordinator`、页面和主题源码，核对实际公开契约与本规格的集成假设。
4. 重新执行 restore、Release build 和完整 SmokeTests，确认功能分支基线干净。
5. 若最终名称或文件布局继续演进，只调整 v1.3.0 接入点和计划文件路径，不反向修改已完成的 v1.2.2 用户语义。
```

Apply these additional exact lifecycle edits:

- Section 2.2 final bullet becomes “本路线文档不实现产品代码，也不自动授权修改 v1.2.2 行为或启动 v1.3.0 功能开发。”
- Smoke command becomes `dotnet run --project test/smoke/WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore`.
- After the `dotnet test` limitation, state that standard test-runner migration is an independent v1.3.0 prerequisite work package and does not enter Project Browser feature commits.
- Section 15 begins from “目录整理后的最新 clean `main` commit”.
- Active vendored-source wording becomes `dep/repkg`; do not change the product namespace `WallpaperField.ThirdParty.RePKG`.
- Section 17 first bullet requires v1.2.2 and directory standardization to be completed and freshly verified.
- Final paragraph becomes: “本规格没有未决产品决定。Project Browser 尚未实施；只有用户明确授权功能开发后，才基于最终源码编写新的逐任务实施计划。”

- [ ] **Step 4: Add historical banners without rewriting old facts**

Prepend `doc/archive/v1.2.2/roadmap-design.md` with:

```markdown
> [!NOTE]
> 这是 v1.2.2 已完成路线的历史设计记录。当前版本入口见 [v1.2.2 发布说明](../../release/v1.2.2.md)，未来路线见 [v1.3.0 Project Browser](../../plan/v1.3.0.md)。正文中的旧路径保留为当时实施证据。

```

Prepend `doc/archive/v1.2.2/roadmap-implementation.md` with:

```markdown
> [!NOTE]
> 这是 v1.2.2 已执行实施计划的历史记录。当前命令与路径见 [文档索引](../../README.md)；正文中的任务勾选和旧路径保持原始计划状态，不代表当前待办。

```

Change its `**Spec:**` path to `doc/archive/v1.2.2/roadmap-design.md`. Do not mechanically rewrite historical task paths.

- [ ] **Step 5: Update all current user/developer/legal paths**

Apply these exact current-document changes:

- Root README release link → `doc/release/v1.2.2.md`.
- Immediately after the current-version line add: `> **下一版本路线：** [v1.3.0 Project Browser](doc/plan/v1.3.0.md) — 独立第四浏览页，尚未实施。`
- Root README image links → `doc/img/scan-center.png`, `output-library.png`, `problem-center.png`.
- Root README built executable → `.\output\bin\WallpaperField\release_net10.0-windows\WallpaperField.exe`.
- Root README release examples → `.\tool\release.ps1` / `tool\release.cmd` with `output\WallpaperField-v1.2.2-local`.
- All root README smoke commands → `.\test\smoke\WallpaperField.SmokeTests.csproj`.
- Root README source architecture → `src/wallpaper-field/contract`, `service`, `composition`.
- Root README extension link → `doc/extension.md`.
- Root README RePKG repository links → `dep/repkg/...`.
- Label `dep/repkg/UPSTREAM-PATCHES.md` as v1.2.2 historical evidence where the root README links it, and point current verification commands to `tool/verify-repkg-compile-surface.ps1`; do not edit the vendored record itself.
- `doc/extension.md`: map source dirs to lowercase singular product paths; verifier to `.\tool\verify-repkg-compile-surface.ps1`; Smoke to `.\test\smoke\WallpaperField.SmokeTests.csproj`.
- `doc/release/v1.2.2.md`: update both smoke commands to `test/smoke`.
- Root `THIRD-PARTY-NOTICES.md`: replace the repository-source paragraph with the following explicit source/distribution distinction:

```markdown
RePKG is licensed under the MIT License. The source-repository copies are kept at
`dep/repkg/LICENSE.txt`, `dep/repkg/THIRD-PARTY-NOTICES.txt`, and
`dep/repkg/UPSTREAM-PATCHES.md`. The verified release ZIP preserves its public
layout and distributes the same files at `ThirdParty/RePKG/LICENSE.txt`,
`ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt`, and
`ThirdParty/RePKG/UPSTREAM-PATCHES.md`.
```

Also change the later XamlAnimatedGif/ImageSharp license-location sentence so it names `dep/repkg/THIRD-PARTY-NOTICES.txt` as the repository copy and `ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt` as the release copy.
- `tool/release.ps1`: release notes source becomes `doc\release\v1.2.2.md`.

- [ ] **Step 6: Add a permanent tracked-directory contract to CI**

Insert immediately after checkout in `.github/workflows/ci.yml`:

```yaml
      - name: Verify repository directory contract
        shell: pwsh
        run: |
          function Assert-ExactSet {
            param([string[]]$Actual, [string[]]$Expected, [string]$Name)
            $difference = @(Compare-Object -ReferenceObject @($Expected | Sort-Object) -DifferenceObject @($Actual | Sort-Object) -CaseSensitive)
            if ($difference.Count -ne 0) {
              throw "$Name differs from the approved directory contract: $($difference | Out-String)"
            }
          }

          $tracked = @(& git ls-files)
          if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) {
            throw 'Could not enumerate tracked files.'
          }
          $tracked = @($tracked | ForEach-Object { $_.Replace('\', '/') })
          $top = @($tracked | ForEach-Object {
            $slash = $_.IndexOf('/')
            if ($slash -ge 0) { $_.Substring(0, $slash) }
          } | Sort-Object -Unique)

          function Get-TrackedDirectories {
            param([string]$Prefix)
            $directories = @($tracked | Where-Object { $_.StartsWith("$Prefix/", [StringComparison]::Ordinal) } | ForEach-Object {
              $relative = $_.Substring($Prefix.Length + 1)
              $parts = $relative.Split('/')
              for ($index = 1; $index -lt $parts.Length; $index++) {
                $parts[0..($index - 1)] -join '/'
              }
            } | Sort-Object -Unique)
            return $directories
          }

          Assert-ExactSet $top @('.github', 'dep', 'doc', 'src', 'test', 'tool') 'Tracked root directories'
          Assert-ExactSet (Get-TrackedDirectories 'src/wallpaper-field') @(
            'application', 'composition', 'contract', 'control', 'infrastructure',
            'model', 'property', 'service', 'theme', 'view', 'viewmodel', 'viewmodel/session'
          ) 'First-party product directories'
          Assert-ExactSet (Get-TrackedDirectories 'test') @('smoke') 'First-party test directories'
          Assert-ExactSet (Get-TrackedDirectories 'doc') @(
            'archive', 'archive/release', 'archive/v1.2.2', 'img', 'plan', 'release'
          ) 'Documentation directories'
```

- [ ] **Step 7: Run local layout, link, and current-path checks**

Stage only the intended Task 3 paths first so index-based layout/link checks evaluate the proposed tree rather than the pre-move index:

```powershell
git add -A -- docs doc README.md THIRD-PARTY-NOTICES.md
git add -- tool/release.ps1 .github/workflows/ci.yml
$staged = @(git diff --cached --name-only)
if ($staged -contains 'GUI_for_RePKG.exe' -or $staged -contains 'AGENTS.md') {
    throw 'User-owned path entered the index.'
}
```

Run this exact local equivalent of the YAML guard against the staged target layout:

```powershell
function Assert-ExactSet {
    param([string[]]$Actual, [string[]]$Expected, [string]$Name)
    $difference = @(Compare-Object -ReferenceObject @($Expected | Sort-Object) -DifferenceObject @($Actual | Sort-Object) -CaseSensitive)
    if ($difference.Count -ne 0) {
        throw "$Name differs from the approved directory contract: $($difference | Out-String)"
    }
}

$tracked = @(& git ls-files)
if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) {
    throw 'Could not enumerate tracked files.'
}
$tracked = @($tracked | ForEach-Object { $_.Replace('\', '/') })
$top = @($tracked | ForEach-Object {
    $slash = $_.IndexOf('/')
    if ($slash -ge 0) { $_.Substring(0, $slash) }
} | Sort-Object -Unique)

function Get-TrackedDirectories {
    param([string]$Prefix)
    return @($tracked | Where-Object { $_.StartsWith("$Prefix/", [StringComparison]::Ordinal) } | ForEach-Object {
        $relative = $_.Substring($Prefix.Length + 1)
        $parts = $relative.Split('/')
        for ($index = 1; $index -lt $parts.Length; $index++) {
            $parts[0..($index - 1)] -join '/'
        }
    } | Sort-Object -Unique)
}

Assert-ExactSet $top @('.github', 'dep', 'doc', 'src', 'test', 'tool') 'Tracked root directories'
Assert-ExactSet (Get-TrackedDirectories 'src/wallpaper-field') @(
    'application', 'composition', 'contract', 'control', 'infrastructure',
    'model', 'property', 'service', 'theme', 'view', 'viewmodel', 'viewmodel/session'
) 'First-party product directories'
Assert-ExactSet (Get-TrackedDirectories 'test') @('smoke') 'First-party test directories'
Assert-ExactSet (Get-TrackedDirectories 'doc') @(
    'archive', 'archive/release', 'archive/v1.2.2', 'img', 'plan', 'release'
) 'Documentation directories'
```

Expected: all four exact-set assertions pass.

Run this local Markdown-link verifier:

```powershell
$failures = [Collections.Generic.List[string]]::new()
$pattern = [regex]'!?\[[^\]]*\]\((?<target>[^)]+)\)'
foreach ($path in @(& git ls-files '*.md')) {
    $content = Get-Content -Raw -LiteralPath $path
    foreach ($match in $pattern.Matches($content)) {
        $target = $match.Groups['target'].Value.Trim().Trim('<', '>')
        if ($target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:' -or $target.StartsWith('#')) { continue }
        $target = [Uri]::UnescapeDataString(($target -split '#', 2)[0])
        if ([string]::IsNullOrWhiteSpace($target)) { continue }
        $base = Split-Path -Parent ([IO.Path]::GetFullPath($path))
        $resolved = [IO.Path]::GetFullPath((Join-Path $base $target))
        if (-not (Test-Path -LiteralPath $resolved)) { $failures.Add("$path -> $target") }
    }
}
if ($failures.Count -ne 0) { throw "Broken local Markdown links: $($failures -join '; ')" }
```

Scan only current surfaces for legacy authoritative paths:

```powershell
$currentSurfaces = @(
    'README.md', 'THIRD-PARTY-NOTICES.md', '.github', 'src', 'test', 'tool',
    'doc/README.md', 'doc/extension.md', 'doc/release', 'doc/plan/v1.3.0.md',
    'WallpaperField.slnx', 'Directory.Build.props'
)
$legacySourceMatches = @(& rg -n 'docs[/\\]|tests[/\\]WallpaperField\.SmokeTests|scripts[/\\]verify-repkg|build-release\.(ps1|cmd)' @currentSurfaces)
$legacySourceExit = $LASTEXITCODE
$legacySourceMatches | ForEach-Object { Write-Host $_ }
if ($legacySourceExit -gt 1) { throw "Legacy-source rg failed with exit code $legacySourceExit." }
if ($legacySourceMatches.Count -ne 0) { throw 'Legacy current-source/document paths remain.' }

$distributionMatches = @(& rg -n 'ThirdParty[/\\]RePKG' @currentSurfaces)
$distributionExit = $LASTEXITCODE
$distributionMatches | ForEach-Object { Write-Host $_ }
if ($distributionExit -gt 1) { throw "Distribution-path rg failed with exit code $distributionExit." }
```

Expected: no old source/current-document paths. Inspect every `ThirdParty/RePKG/...` match in README/notices/tools and confirm it is explicitly a public publish/ZIP contract, never a repository-source lookup. The immutable `dep/repkg/UPSTREAM-PATCHES.md` is outside this current-path scan; its v1.2.2-era script path remains historical evidence rather than a current command.

- [ ] **Step 8: Re-run behavior and release checks with current documents**

```powershell
dotnet restore .\WallpaperField.slnx --configfile .\NuGet.Config
dotnet build .\WallpaperField.slnx -c Release --no-restore --no-incremental
dotnet run --project .\test\smoke\WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore
pwsh -NoProfile -File .\tool\verify-repkg-compile-surface.ps1 -Mode Verify
$candidate = Join-Path (Resolve-Path .\temp\agent-work\20260822-1745-directory-doc-reorganization).Path ("doc-candidate-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
pwsh -NoProfile -File .\tool\release.ps1 -OutputDirectory $candidate
```

Expected: all commands pass, release notes are copied from `doc/release/v1.2.2.md`, and the ZIP public names remain unchanged.

- [ ] **Step 9: Commit the documentation lifecycle and directory guard**

```powershell
git add -A -- docs doc README.md THIRD-PARTY-NOTICES.md
git add -- tool/release.ps1 .github/workflows/ci.yml
$staged = @(git diff --cached --name-only)
if ($staged -contains 'GUI_for_RePKG.exe' -or $staged -contains 'AGENTS.md') { throw 'User-owned path entered the index.' }
git diff --cached --check
git diff --cached --stat
git commit -m "docs: organize current roadmap and archive"
```

---

### Task 4: Archive the completed reorganization records and run the final repository gates

**Files:**

- Modify but do not stage: `AGENTS.md`
- Move: `doc/plan/2026-08-22-project-directory-reorganization-design.md` → `doc/archive/v1.2.2/project-directory-reorganization-design.md`
- Move: `doc/plan/2026-08-22-project-directory-reorganization.md` → `doc/archive/v1.2.2/project-directory-reorganization-implementation.md`
- Modify: `doc/README.md`
- Modify: archived reorganization design/plan status and cross-reference
- Modify but do not stage: `temp/maintenance-audit/01-architecture-and-evolution.md`
- Modify but do not stage: `temp/maintenance-audit/04-verification-report.md`
- Modify but do not stage: `temp/maintenance-audit/05-maintenance-roadmap.md`
- Modify but do not stage: all four current task records under `temp/agent-work/20260822-1745-directory-doc-reorganization/`

**Interfaces:**

- Consumes: committed product/support/document layout from Tasks 1–3.
- Produces: final clean tracked HEAD with only `doc/plan/v1.3.0.md` active, post-reorganization release evidence tied to that HEAD, updated untracked repository instructions/audit, and no task-owned working-tree changes.

- [ ] **Step 1: Update only the four durable old-path semantics in untracked `AGENTS.md`**

Use `apply_patch` so the relevant statements become:

```markdown
- 主项目位于 `src/wallpaper-field/`；SDK 默认 glob 只覆盖产品目录，`WallpaperField.csproj` 的 `DefaultItemExcludes` 必须持续排除 `..\..\temp\**`。
```

Replace both policy uses of `ThirdParty/RePKG` with `dep/repkg`, including the section heading, and replace the baseline smoke command with:

```powershell
dotnet run --project test/smoke/WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore
```

Run `git status --short AGENTS.md`; expected: it remains exactly untracked (`??`), never staged.

- [ ] **Step 2: Run the fast pre-archive gates**

```powershell
dotnet build .\WallpaperField.slnx -c Release --no-restore --no-incremental
dotnet run --project .\test\smoke\WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore
pwsh -NoProfile -File .\tool\verify-repkg-compile-surface.ps1 -Mode Verify
git diff --check
```

Expected: build 0 warnings/errors, positive Smoke summary, compile-surface missing/unexpected 0, and clean task diff.

- [ ] **Step 3: Move the approved design and this plan into the completed v1.2.2 archive**

With `apply_patch`, set the design status to `已实施并归档（2026-08-22）`. In this implementation plan, change the `**Spec:**` path to `doc/archive/v1.2.2/project-directory-reorganization-design.md` and prepend this historical note after the title/header block:

```markdown
> **历史状态（2026-08-22）：** 本计划对应的目录与文档整理已执行；正文复现原始实施步骤，当前活跃路线仅为 `doc/plan/v1.3.0.md`。
```

Then move the two files with exact paths:

```powershell
Move-Item -LiteralPath .\doc\plan\2026-08-22-project-directory-reorganization-design.md -Destination .\doc\archive\v1.2.2\project-directory-reorganization-design.md
Move-Item -LiteralPath .\doc\plan\2026-08-22-project-directory-reorganization.md -Destination .\doc\archive\v1.2.2\project-directory-reorganization-implementation.md
```

Add these links to the historical archive section of `doc/README.md`:

```markdown
- [v1.2.2 项目目录整理设计](archive/v1.2.2/project-directory-reorganization-design.md)
- [v1.2.2 项目目录整理实施计划](archive/v1.2.2/project-directory-reorganization-implementation.md)
```

Confirm `doc/plan/` now contains only `v1.3.0.md`.

- [ ] **Step 4: Commit only the archive transition before final identity verification**

```powershell
git add -A -- doc/plan/2026-08-22-project-directory-reorganization-design.md doc/plan/2026-08-22-project-directory-reorganization.md doc/archive/v1.2.2 doc/README.md
$staged = @(git diff --cached --name-only)
if ($staged -contains 'GUI_for_RePKG.exe' -or $staged -contains 'AGENTS.md') { throw 'User-owned path entered the index.' }
git diff --cached --check
git diff --cached --stat
git commit -m "docs: archive completed reorganization records"
```

- [ ] **Step 5: Re-run the exact final directory contract and Markdown-link verifier**

Run this complete local directory check:

```powershell
function Assert-ExactSet {
    param([string[]]$Actual, [string[]]$Expected, [string]$Name)
    $difference = @(Compare-Object -ReferenceObject @($Expected | Sort-Object) -DifferenceObject @($Actual | Sort-Object) -CaseSensitive)
    if ($difference.Count -ne 0) { throw "$Name differs: $($difference | Out-String)" }
}
$tracked = @(& git ls-files | ForEach-Object { $_.Replace('\', '/') })
$top = @($tracked | ForEach-Object { $slash = $_.IndexOf('/'); if ($slash -ge 0) { $_.Substring(0, $slash) } } | Sort-Object -Unique)
function Get-TrackedDirectories {
    param([string]$Prefix)
    return @($tracked | Where-Object { $_.StartsWith("$Prefix/", [StringComparison]::Ordinal) } | ForEach-Object {
        $relative = $_.Substring($Prefix.Length + 1)
        $parts = $relative.Split('/')
        for ($index = 1; $index -lt $parts.Length; $index++) { $parts[0..($index - 1)] -join '/' }
    } | Sort-Object -Unique)
}
Assert-ExactSet $top @('.github', 'dep', 'doc', 'src', 'test', 'tool') 'Tracked root directories'
Assert-ExactSet (Get-TrackedDirectories 'src/wallpaper-field') @('application', 'composition', 'contract', 'control', 'infrastructure', 'model', 'property', 'service', 'theme', 'view', 'viewmodel', 'viewmodel/session') 'Product directories'
Assert-ExactSet (Get-TrackedDirectories 'test') @('smoke') 'Test directories'
Assert-ExactSet (Get-TrackedDirectories 'doc') @('archive', 'archive/release', 'archive/v1.2.2', 'img', 'plan', 'release') 'Document directories'
$legacyRoots = @(
    'Application', 'Composition', 'Contracts', 'Controls', 'Converters',
    'Infrastructure', 'Models', 'Properties', 'Services', 'Themes',
    'ThirdParty', 'ViewModels', 'Views', 'artifacts', 'bin', 'docs', 'obj',
    'scripts', 'tests'
)
$remainingLegacyRoots = @($legacyRoots | Where-Object { Test-Path -LiteralPath $_ })
if ($remainingLegacyRoots.Count -ne 0) {
    throw "Legacy root paths remain: $($remainingLegacyRoots -join ', ')"
}
```

Run the complete Markdown-link verifier against the archived final paths:

```powershell
$failures = [Collections.Generic.List[string]]::new()
$pattern = [regex]'!?\[[^\]]*\]\((?<target>[^)]+)\)'
foreach ($path in @(& git ls-files '*.md')) {
    $content = Get-Content -Raw -LiteralPath $path
    foreach ($match in $pattern.Matches($content)) {
        $target = $match.Groups['target'].Value.Trim().Trim('<', '>')
        if ($target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:' -or $target.StartsWith('#')) { continue }
        $target = [Uri]::UnescapeDataString(($target -split '#', 2)[0])
        if ([string]::IsNullOrWhiteSpace($target)) { continue }
        $base = Split-Path -Parent ([IO.Path]::GetFullPath($path))
        $resolved = [IO.Path]::GetFullPath((Join-Path $base $target))
        if (-not (Test-Path -LiteralPath $resolved)) { $failures.Add("$path -> $target") }
    }
}
if ($failures.Count -ne 0) { throw "Broken local Markdown links: $($failures -join '; ')" }
```

Expected: zero failures.

- [ ] **Step 6: Run full final build, analyzer, Smoke, compile-surface, and TEST-001 evidence**

```powershell
dotnet restore .\WallpaperField.slnx --configfile .\NuGet.Config
dotnet build .\WallpaperField.slnx -c Release --no-restore --no-incremental
dotnet build .\src\wallpaper-field\WallpaperField.csproj -c Release --no-restore --no-dependencies -p:EnableNETAnalyzers=true -p:AnalysisMode=Recommended -p:TreatWarningsAsErrors=true
dotnet build .\test\smoke\WallpaperField.SmokeTests.csproj -c Release --no-restore --no-dependencies -p:EnableNETAnalyzers=true -p:AnalysisMode=Recommended -p:TreatWarningsAsErrors=true
dotnet run --project .\test\smoke\WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore
pwsh -NoProfile -File .\tool\verify-repkg-compile-surface.ps1 -Mode Verify
dotnet list .\WallpaperField.slnx package --vulnerable --include-transitive --format json
dotnet list .\WallpaperField.slnx package --deprecated --include-transitive
dotnet list .\WallpaperField.slnx package --outdated --include-transitive
$unexpectedGenerated = @(Get-ChildItem -LiteralPath .\src, .\test, .\dep -Directory -Recurse -Force -ErrorAction Stop |
    Where-Object { $_.Name -in @('bin', 'obj') })
if ($unexpectedGenerated.Count -ne 0) {
    throw "Generated bin/obj directories escaped output/: $($unexpectedGenerated.FullName -join ', ')"
}
```

Run the final intentional-failure probe:

```powershell
$failureOutput = @(& dotnet run --project .\test\smoke\WallpaperField.SmokeTests.csproj -c Release --no-build --no-restore -- --verify-failure-exit 2>&1)
$failureExit = $LASTEXITCODE
$failureOutput | ForEach-Object { Write-Host $_ }
if ($failureExit -eq 0) { throw 'Intentional Smoke failure returned exit code 0.' }
Write-Host "Intentional Smoke failure returned expected nonzero exit code $failureExit."
```

Then capture, but do not promote, `dotnet test`:

```powershell
$testOutput = @(& dotnet test .\WallpaperField.slnx -c Release --no-build --no-restore 2>&1)
$testExit = $LASTEXITCODE
$testOutput | ForEach-Object { Write-Host $_ }
if ($testExit -ne 0) { throw "dotnet test exited with $testExit." }
Write-Host "TEST001_LIMITATION exit=$testExit outputLines=$($testOutput.Count); this is not the Smoke gate."
```

Expected: build/analyzers/Smoke/compile surface pass; intentional failure is nonzero; vulnerability JSON contains projects and no vulnerable package entries; `dotnet test` remains limitation evidence rather than a completion claim.

- [ ] **Step 7: Build and independently inspect the final isolated release candidate**

```powershell
$taskRoot = (Resolve-Path .\temp\agent-work\20260822-1745-directory-doc-reorganization).Path
$candidate = Join-Path $taskRoot ("final-candidate-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$rootHashBefore = (Get-FileHash -LiteralPath .\GUI_for_RePKG.exe -Algorithm SHA256).Hash
$head = (& git rev-parse HEAD).Trim()
pwsh -NoProfile -File .\tool\release.ps1 -OutputDirectory $candidate
if ($LASTEXITCODE -ne 0) { throw "Final release verification exited with $LASTEXITCODE." }
$rootHashAfter = (Get-FileHash -LiteralPath .\GUI_for_RePKG.exe -Algorithm SHA256).Hash
if ($rootHashBefore -ne $rootHashAfter) { throw 'Final release verification changed the root EXE.' }

$expectedCandidate = @('WallpaperField.exe', 'WallpaperField-v1.2.2-win-x64.zip', 'release-manifest.json', 'dependencies.json', 'SHA256SUMS')
$actualCandidate = @(Get-ChildItem -LiteralPath $candidate -File | Select-Object -ExpandProperty Name)
Assert-ExactSet $actualCandidate $expectedCandidate 'Final candidate files'
$manifest = Get-Content -Raw -LiteralPath (Join-Path $candidate 'release-manifest.json') | ConvertFrom-Json
if ($manifest.sourceCommit -ne $head -or $manifest.version -ne '1.2.2') { throw 'Final release identity does not match HEAD/v1.2.2.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $candidate 'WallpaperField-v1.2.2-win-x64.zip'
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $actualZip = @($archive.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\', '/') })
    $expectedZip = @('WallpaperField.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'RELEASE-NOTES.md', 'release-manifest.json', 'dependencies.json', 'ThirdParty/RePKG/LICENSE.txt', 'ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt', 'ThirdParty/RePKG/UPSTREAM-PATCHES.md')
    Assert-ExactSet $actualZip $expectedZip 'Final ZIP entries'
}
finally {
    $archive.Dispose()
}
```

Expected: final candidate identity points to the archive commit, candidate has five exact files, ZIP has nine exact public entries, controlled launch succeeds inside the release script, and root EXE bytes remain unchanged.

- [ ] **Step 8: Append dated untracked audit evidence and refresh recovery records**

Using `apply_patch`, append only dated sections; preserve all historical text:

- `temp/maintenance-audit/01-architecture-and-evolution.md`: final `src/test/doc/dep/tool` map, `Directory.Build.props`/`output` behavior, unchanged namespaces and release layout.
- `temp/maintenance-audit/05-maintenance-roadmap.md`: v1.2.2 remains current; old structural work is complete; the sole controlled v1.3.0 mainline is `doc/plan/v1.3.0.md` Project Browser; standard test-runner migration and ImageSharp upgrade stay separate.
- `temp/maintenance-audit/04-verification-report.md`: every exact command actually executed in Steps 5–7, its observed exit code, Smoke counts, compile-surface counts, release/ZIP counts, final HEAD, root EXE preservation, and the `dotnet test` limitation.

Update the current task `task_plan.md`, `findings.md`, and `progress.md` after each evidence group. Recompute bytes/SHA-256/mtime for the three changed audit files and replace their rows in `research-manifest.md`; record the current full-read/write completion time.

- [ ] **Step 9: Perform the final diff/status/user-state audit**

```powershell
git diff --check
git diff --stat 5b2aefa9fa50b5b18708110f4f8402fc2ef125f8..HEAD
git status --short --branch
git log --oneline --decorate -6
Get-FileHash -LiteralPath .\GUI_for_RePKG.exe -Algorithm SHA256
```

Expected: no task-owned tracked changes remain; the only working-tree entries are the original user-owned `M GUI_for_RePKG.exe` and untracked `AGENTS.md`; all task phases in the ignored task plan are `complete`; `Next Step` is `None`; final response lists actual commands/results, unsigned/physical-device limitations, and deleted generated material recovery as rebuild-only.
