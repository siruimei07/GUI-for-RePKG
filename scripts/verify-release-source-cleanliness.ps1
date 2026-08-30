[CmdletBinding()]
param(
    [string] $ReleaseScriptPath,

    [string] $WorkRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($ReleaseScriptPath))
{
    $ReleaseScriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'build-release.ps1'
}
if ([string]::IsNullOrWhiteSpace($WorkRoot))
{
    $WorkRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'WallpaperField-release-source-cleanliness'
}

$resolvedReleaseScript = (Resolve-Path -LiteralPath $ReleaseScriptPath).Path
$sourceRepositoryPath = Split-Path -Parent $resolvedReleaseScript
$sourceProjectPath = Join-Path $sourceRepositoryPath 'WallpaperField.csproj'
$sourceIgnorePath = Join-Path $sourceRepositoryPath '.gitignore'
if (-not (Test-Path -LiteralPath $sourceProjectPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $sourceIgnorePath -PathType Leaf))
{
    throw 'Release input-boundary verification requires WallpaperField.csproj and .gitignore beside the release script.'
}
$resolvedWorkRoot = [System.IO.Path]::GetFullPath($WorkRoot)
$realGit = @(Get-Command git -CommandType Application -ErrorAction Stop)[0].Source
$realDotnet = @(Get-Command dotnet -CommandType Application -ErrorAction Stop)[0].Source
$pwsh = @(Get-Command pwsh -CommandType Application -ErrorAction Stop)[0].Source
$windowsPowerShell = @(Get-Command powershell.exe -CommandType Application -ErrorAction Stop)[0].Source
$frameworkCscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$frameworkCsc = @($frameworkCscCandidates | Where-Object {
    Test-Path -LiteralPath $_ -PathType Leaf
} | Select-Object -First 1)
if ($frameworkCsc.Count -ne 1)
{
    throw 'Release compatibility verification requires the Windows .NET Framework C# compiler.'
}
$frameworkCsc = $frameworkCsc[0]
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Invoke-GitChecked
{
    param(
        [Parameter(Mandatory = $true)][string] $RepositoryPath,
        [Parameter(Mandatory = $true)][string[]] $Arguments
    )

    $output = @(& $realGit -C $RepositoryPath @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "Fixture Git command failed (exit $LASTEXITCODE): git $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }

    return $output
}

function New-FakePublishedExecutable
{
    param(
        [Parameter(Mandatory = $true)][string] $CaseRoot,
        [Parameter(Mandatory = $true)][string] $SourceCommit
    )

    if ($SourceCommit -notmatch '^[0-9a-fA-F]{40}$')
    {
        throw "Cannot compile a fake published executable for an invalid commit: $SourceCommit"
    }

    $sourcePath = Join-Path $CaseRoot 'fake-published-executable.cs'
    $outputPath = Join-Path $CaseRoot 'fake-published-executable.exe'
    $source = @"
using System;
using System.IO;
using System.Reflection;

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]
[assembly: AssemblyInformationalVersion("1.3.0+$SourceCommit")]

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], "--snapshot", StringComparison.OrdinalIgnoreCase))
            {
                string parent = Path.GetDirectoryName(args[index + 1]);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                File.WriteAllBytes(args[index + 1], new byte[] { 137, 80, 78, 71 });
                break;
            }
        }
        return 0;
    }
}
"@
    [System.IO.File]::WriteAllText($sourcePath, $source, $utf8NoBom)
    $compileOutput = @(& $frameworkCsc /nologo /target:winexe "/out:$outputPath" $sourcePath 2>&1)
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputPath -PathType Leaf))
    {
        throw "Could not compile the fake published executable (exit $LASTEXITCODE): $($compileOutput -join [Environment]::NewLine)"
    }

    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($outputPath)
    if ($version.FileVersion -ne '1.3.0.0' -or $version.ProductVersion -ne "1.3.0+$SourceCommit")
    {
        throw "Fake published executable version mismatch: FileVersion=$($version.FileVersion), ProductVersion=$($version.ProductVersion)"
    }

    return $outputPath
}

function Invoke-PowerShell51AstProbe
{
    param(
        [Parameter(Mandatory = $true)][string] $CaseRoot
    )

    [System.IO.Directory]::CreateDirectory($CaseRoot) | Out-Null
    $probePath = Join-Path $CaseRoot 'parse-release.ps1'
    $probe = @'
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $TargetPath)
$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $TargetPath,
    [ref] $tokens,
    [ref] $errors) | Out-Null
if (@($errors).Count -ne 0)
{
    $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }
    exit 1
}
exit 0
'@
    [System.IO.File]::WriteAllText($probePath, $probe, $utf8NoBom)
    $parseOutput = @(& $windowsPowerShell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $probePath `
        -TargetPath $resolvedReleaseScript 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "Windows PowerShell 5.1 could not parse build-release.ps1: $($parseOutput -join [Environment]::NewLine)"
    }

    Write-Output 'RELEASE_POWERSHELL51_AST result=PASS'
}

function Remove-VerifiedTestRoot
{
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Parent
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    $prefix = $fullParent + [System.IO.Path]::DirectorySeparatorChar
    $leaf = [System.IO.Path]::GetFileName($fullPath)
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        $leaf -notmatch '^rsc-[0-9a-f]{32}$')
    {
        throw "Refusing to remove an unverified test root: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath)
    {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function Test-SameOrChildPath
{
    param(
        [Parameter(Mandatory = $true)][string] $Child,
        [Parameter(Mandatory = $true)][string] $Parent
    )

    $fullChild = [System.IO.Path]::GetFullPath($Child).TrimEnd('\', '/')
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    if ($fullChild.Equals($fullParent, [StringComparison]::OrdinalIgnoreCase))
    {
        return $true
    }

    return $fullChild.StartsWith(
        $fullParent + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)
}

function Invoke-AncestorBuildImportProbe
{
    param(
        [Parameter(Mandatory = $true)][string] $CaseRoot
    )

    $attackerParent = Join-Path $CaseRoot 'attacker-parent'
    $repositoryPath = Join-Path $attackerParent 'repository'
    $publishOutputPath = Join-Path $CaseRoot 'publish-output'
    [System.IO.Directory]::CreateDirectory($attackerParent) | Out-Null

    $cloneOutput = @(& $realGit -c core.longpaths=true clone --quiet --no-hardlinks $sourceRepositoryPath $repositoryPath 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "Ancestor-import clone failed (exit $LASTEXITCODE): $($cloneOutput -join [Environment]::NewLine)"
    }
    Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('config', 'core.longpaths', 'true') | Out-Null

    $ancestorProps = @'
<Project>
  <PropertyGroup>
    <ReleaseIntegrityAncestorProps>RELEASE_ANCESTOR_PROPS_CONSUMED</ReleaseIntegrityAncestorProps>
  </PropertyGroup>
</Project>
'@
    $ancestorTargets = @'
<Project>
  <Target Name="ReleaseIntegrityAncestorPublishAttack"
          BeforeTargets="PrepareForBuild"
          Condition="'$(ReleaseIntegrityAncestorAttack)' == 'true'">
    <Error Text="$(ReleaseIntegrityAncestorProps) $(ReleaseIntegrityAncestorPackages) RELEASE_ANCESTOR_TARGETS_CONSUMED" />
  </Target>
</Project>
'@
    $ancestorPackages = @'
<Project>
  <PropertyGroup>
    <ReleaseIntegrityAncestorPackages>RELEASE_ANCESTOR_PACKAGES_CONSUMED</ReleaseIntegrityAncestorPackages>
  </PropertyGroup>
</Project>
'@
    [System.IO.File]::WriteAllText((Join-Path $attackerParent 'Directory.Build.props'), $ancestorProps, $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $attackerParent 'Directory.Build.targets'), $ancestorTargets, $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $attackerParent 'Directory.Packages.props'), $ancestorPackages, $utf8NoBom)

    $cleanBefore = @(Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('status', '--porcelain=v1', '--untracked-files=all'))
    if ($cleanBefore.Count -ne 0)
    {
        throw "Ancestor-import repository was not porcelain-clean: $($cleanBefore -join ', ')"
    }

    $publishOutput = @(& $realDotnet publish (Join-Path $repositoryPath 'WallpaperField.csproj') `
        --configuration Release `
        --no-restore `
        --output $publishOutputPath `
        '-p:ReleaseIntegrityAncestorAttack=true' 2>&1)
    $publishExitCode = $LASTEXITCODE
    $publishText = $publishOutput -join [Environment]::NewLine
    if ($publishExitCode -eq 0 -or
        $publishText -notlike '*RELEASE_ANCESTOR_PROPS_CONSUMED*' -or
        $publishText -notlike '*RELEASE_ANCESTOR_PACKAGES_CONSUMED*' -or
        $publishText -notlike '*RELEASE_ANCESTOR_TARGETS_CONSUMED*')
    {
        throw "Real dotnet publish did not consume all three ancestor build/package inputs (exit $publishExitCode): $publishText"
    }

    $cleanAfter = @(Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('status', '--porcelain=v1', '--untracked-files=all'))
    if ($cleanAfter.Count -ne 0)
    {
        throw "Ancestor-import characterization made tracked source dirty: $($cleanAfter -join ', ')"
    }

    Write-Output 'RELEASE_ANCESTOR_IMPORT props=True targets=True packages=True git_clean=True publish_consumed=True result=OBSERVED'
}

function Invoke-ReleaseItemEvaluationProbe
{
    param(
        [Parameter(Mandatory = $true)][string] $CaseRoot
    )

    $repositoryPath = Join-Path $CaseRoot 'repository'
    $probeRelativePaths = @(
        'artifacts/release-integrity-root-artifacts.cs',
        'nested/artifacts/release-integrity-nested-artifacts.cs',
        'bin/release-integrity-root-bin.cs',
        'nested/bin/release-integrity-nested-bin.cs',
        'obj/release-integrity-root-obj.cs',
        'nested/obj/release-integrity-nested-obj.cs'
    )
    $userProjectRelativePath = 'WallpaperField.csproj.user'
    $userProjectPath = Join-Path $repositoryPath $userProjectRelativePath
    $userCompileSentinel = 'release-integrity-user-import-sentinel.cs'
    [System.IO.Directory]::CreateDirectory($CaseRoot) | Out-Null

    $cloneOutput = @(& $realGit -c core.longpaths=true clone --quiet --no-hardlinks $sourceRepositoryPath $repositoryPath 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "Release item-evaluation clone failed (exit $LASTEXITCODE): $($cloneOutput -join [Environment]::NewLine)"
    }
    Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('config', 'core.longpaths', 'true') | Out-Null

    # CI evaluates the committed checkout. Local TDD may have an uncommitted
    # project contract, so snapshot only its two input owners into a local
    # fixture commit before proving the clone clean.
    $null = @(& $realGit -C $sourceRepositoryPath diff --quiet HEAD -- WallpaperField.csproj .gitignore 2>&1)
    $sourceOwnerDiffExitCode = $LASTEXITCODE
    if ($sourceOwnerDiffExitCode -ne 0 -and $sourceOwnerDiffExitCode -ne 1)
    {
        throw 'Could not inspect the source item-evaluation owners.'
    }
    if ($sourceOwnerDiffExitCode -eq 1)
    {
        Copy-Item -LiteralPath $sourceProjectPath -Destination (Join-Path $repositoryPath 'WallpaperField.csproj') -Force
        Copy-Item -LiteralPath $sourceIgnorePath -Destination (Join-Path $repositoryPath '.gitignore') -Force
        Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('config', 'user.name', 'Wallpaper Field Release Probe') | Out-Null
        Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('config', 'user.email', 'release-probe@example.invalid') | Out-Null
        Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('add', '--', 'WallpaperField.csproj', '.gitignore') | Out-Null
        Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'fixture release input owners') | Out-Null
    }

    $cleanBefore = @(Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('status', '--porcelain=v1', '--untracked-files=all'))
    if ($cleanBefore.Count -ne 0)
    {
        throw "Item-evaluation clone was dirty before the ignored probe: $($cleanBefore -join ', ')"
    }

    foreach ($probeRelativePath in $probeRelativePaths)
    {
        $probePath = Join-Path $repositoryPath $probeRelativePath
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $probePath)) | Out-Null
        [System.IO.File]::WriteAllText(
            $probePath,
            "internal sealed class ReleaseIntegrityProbe { }`r`n",
            $utf8NoBom)
        $null = @(& $realGit -C $repositoryPath check-ignore --quiet -- $probeRelativePath 2>&1)
        if ($LASTEXITCODE -ne 0)
        {
            throw "The release integrity probe is not ignored by Git: $probeRelativePath"
        }
    }

    $userProject = @'
<Project>
  <ItemGroup>
    <Compile Include="release-integrity-user-import-sentinel.cs" />
  </ItemGroup>
</Project>
'@
    [System.IO.File]::WriteAllText($userProjectPath, $userProject, $utf8NoBom)
    $null = @(& $realGit -C $repositoryPath check-ignore --quiet -- $userProjectRelativePath 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "The MSBuild user import probe is not ignored by Git: $userProjectRelativePath"
    }

    $cleanWithProbe = @(Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('status', '--porcelain=v1', '--untracked-files=all'))
    if ($cleanWithProbe.Count -ne 0)
    {
        throw "Ignored release integrity probe made the clone dirty: $($cleanWithProbe -join ', ')"
    }

    $itemOutput = @(& $realDotnet msbuild (Join-Path $repositoryPath 'WallpaperField.csproj') -nologo -getItem:Compile 2>&1)
    $itemExitCode = $LASTEXITCODE
    if ($itemExitCode -ne 0)
    {
        throw "MSBuild Compile item evaluation failed (exit $itemExitCode): $($itemOutput -join [Environment]::NewLine)"
    }

    try
    {
        $itemResult = ($itemOutput -join [Environment]::NewLine) | ConvertFrom-Json
    }
    catch
    {
        throw "MSBuild Compile item evaluation did not return valid JSON: $($itemOutput -join [Environment]::NewLine)"
    }

    $compileItems = @($itemResult.Items.Compile)
    if ($compileItems.Count -eq 0)
    {
        throw 'MSBuild Compile item evaluation returned no items.'
    }
    $compiledProbePaths = @()
    foreach ($probeRelativePath in $probeRelativePaths)
    {
        $probePath = Join-Path $repositoryPath $probeRelativePath
        $probeFullPath = [System.IO.Path]::GetFullPath($probePath)
        $probeIdentity = $probeRelativePath.Replace('/', '\')
        $compileMatches = @($compileItems | Where-Object {
            $identity = ([string] $_.Identity).Replace('/', '\')
            $fullPath = [string] $_.FullPath
            $identity -eq $probeIdentity -or
                (-not [string]::IsNullOrWhiteSpace($fullPath) -and
                    [System.IO.Path]::GetFullPath($fullPath).Equals($probeFullPath, [StringComparison]::OrdinalIgnoreCase))
        })
        if ($compileMatches.Count -ne 0)
        {
            $compiledProbePaths += $probeRelativePath
        }
    }
    if ($compiledProbePaths.Count -ne 0)
    {
        throw "Ignored release integrity probes entered Compile items: $($compiledProbePaths -join ', ')"
    }

    $userCompileMatches = @($compileItems | Where-Object {
        ([string] $_.Identity).Replace('/', '\').Equals(
            $userCompileSentinel,
            [StringComparison]::OrdinalIgnoreCase)
    })
    if ($userCompileMatches.Count -ne 1)
    {
        throw "MSBuild did not consume the ignored user import exactly once: $userProjectRelativePath"
    }

    $cleanAfterEvaluation = @(Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('status', '--porcelain=v1', '--untracked-files=all'))
    if ($cleanAfterEvaluation.Count -ne 0)
    {
        throw "MSBuild item evaluation made the fixture clone dirty: $($cleanAfterEvaluation -join ', ')"
    }

    Write-Output ("RELEASE_ITEM_EVALUATION probes={0} ignored=True git_clean=True compile_contains_probes=False compile_count={1} result=PASS" -f $probeRelativePaths.Count, $compileItems.Count)
    Write-Output ("RELEASE_IGNORED_USER_IMPORT path={0} ignored=True git_clean=True compile_sentinel=True result=OBSERVED" -f $userProjectRelativePath)
}

function Invoke-ReleaseProbe
{
    param(
        [Parameter(Mandatory = $true)][string] $CaseRoot,
        [Parameter(Mandatory = $true)][string] $Mutation,
        [Parameter(Mandatory = $true)][string] $GitMode,
        [Parameter(Mandatory = $true)][string] $DotnetMode,
        [string] $PowerShellPath = $pwsh
    )

    $repositoryPath = Join-Path $CaseRoot 'repository'
    $toolsPath = Join-Path $CaseRoot 'tools'
    $outputParent = Join-Path $CaseRoot 'o'
    $outputPath = Join-Path $outputParent 'c'
    $dotnetMarker = Join-Path $CaseRoot 'dotnet-called.txt'
    $dotnetCalls = Join-Path $CaseRoot 'dotnet-calls.txt'
    $sourceObservations = Join-Path $CaseRoot 'source-observations.txt'
    $globalJsonObservation = Join-Path $CaseRoot 'observed-global.json'
    $rspObservation = Join-Path $CaseRoot 'observed-rsp.txt'
    [System.IO.Directory]::CreateDirectory($repositoryPath) | Out-Null
    [System.IO.Directory]::CreateDirectory($toolsPath) | Out-Null

    Copy-Item -LiteralPath $resolvedReleaseScript -Destination (Join-Path $repositoryPath 'build-release.ps1')
    [System.IO.File]::WriteAllText((Join-Path $repositoryPath 'tracked.txt'), "baseline`r`n", $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $repositoryPath '.gitignore'), "*.user`r`n", $utf8NoBom)
    [System.IO.File]::WriteAllText(
        (Join-Path $repositoryPath 'WallpaperField.csproj'),
        "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><!-- RELEASE_COMMITTED_SOURCE --></Project>`r`n",
        $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $repositoryPath 'WallpaperField.slnx'), "<Solution><!-- RELEASE_COMMITTED_SOURCE --></Solution>`r`n", $utf8NoBom)
    [System.IO.File]::WriteAllText(
        (Join-Path $repositoryPath 'NuGet.Config'),
        "<?xml version=`"1.0`" encoding=`"utf-8`"?><configuration><!-- RELEASE_COMMITTED_SOURCE --></configuration>`r`n",
        $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $repositoryPath 'LICENSE'), "RELEASE_COMMITTED_SOURCE license`r`n", $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $repositoryPath 'THIRD-PARTY-NOTICES.md'), "RELEASE_COMMITTED_SOURCE notices`r`n", $utf8NoBom)
    [System.IO.Directory]::CreateDirectory((Join-Path $repositoryPath 'docs\releases')) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $repositoryPath 'docs\releases\v1.3.0.md'), "RELEASE_COMMITTED_SOURCE release notes`r`n", $utf8NoBom)
    [System.IO.Directory]::CreateDirectory((Join-Path $repositoryPath 'ThirdParty\RePKG')) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $repositoryPath 'ThirdParty\RePKG\LICENSE.txt'), "RELEASE_COMMITTED_SOURCE RePKG license`r`n", $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $repositoryPath 'ThirdParty\RePKG\THIRD-PARTY-NOTICES.txt'), "RELEASE_COMMITTED_SOURCE RePKG notices`r`n", $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $repositoryPath 'ThirdParty\RePKG\UPSTREAM-PATCHES.md'), "RELEASE_COMMITTED_SOURCE RePKG patches`r`n", $utf8NoBom)
    Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('-c', 'core.longpaths=true', 'init', '--quiet') | Out-Null
    Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('config', 'core.longpaths', 'true') | Out-Null
    Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('config', 'user.name', 'Wallpaper Field Release Probe') | Out-Null
    Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('config', 'user.email', 'release-probe@example.invalid') | Out-Null
    Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('add', '--all') | Out-Null
    Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('commit', '--quiet', '-m', 'fixture baseline') | Out-Null

    if ($GitMode -eq 'post-gate-source-mutation')
    {
        $ancestorProps = '<Project><PropertyGroup><ReleaseIntegrityAncestorProps>ATTACK</ReleaseIntegrityAncestorProps></PropertyGroup></Project>'
        $ancestorTargets = '<Project><Target Name="ReleaseIntegrityAncestorTargets" BeforeTargets="PrepareForBuild"><Error Text="ATTACK" /></Target></Project>'
        $ancestorPackages = '<Project><PropertyGroup><ReleaseIntegrityAncestorPackages>ATTACK</ReleaseIntegrityAncestorPackages></PropertyGroup></Project>'
        $ancestorSolutionProps = '<Project><PropertyGroup><ReleaseIntegrityAncestorSolutionProps>ATTACK</ReleaseIntegrityAncestorSolutionProps></PropertyGroup></Project>'
        $ancestorSolutionTargets = '<Project><Target Name="ReleaseIntegrityAncestorSolutionTargets" BeforeTargets="PrepareForBuild"><Error Text="ATTACK" /></Target></Project>'
        $ancestorGlobalJson = '{"sdk":{"version":"99.0.100","rollForward":"disable","allowPrerelease":false}}'
        $ancestorNuGetConfig = '<?xml version="1.0"?><configuration><packageSources><clear /><add key="attack" value="Z:\release-integrity-attack" /></packageSources></configuration>'
        [System.IO.File]::WriteAllText((Join-Path $CaseRoot 'Directory.Build.props'), $ancestorProps, $utf8NoBom)
        [System.IO.File]::WriteAllText((Join-Path $CaseRoot 'Directory.Build.targets'), $ancestorTargets, $utf8NoBom)
        [System.IO.File]::WriteAllText((Join-Path $CaseRoot 'Directory.Packages.props'), $ancestorPackages, $utf8NoBom)
        [System.IO.File]::WriteAllText((Join-Path $CaseRoot 'Directory.Solution.props'), $ancestorSolutionProps, $utf8NoBom)
        [System.IO.File]::WriteAllText((Join-Path $CaseRoot 'Directory.Solution.targets'), $ancestorSolutionTargets, $utf8NoBom)
        [System.IO.File]::WriteAllText((Join-Path $CaseRoot 'Directory.Build.rsp'), '-p:ReleaseIntegrityAncestorRsp=ATTACK', $utf8NoBom)
        [System.IO.File]::WriteAllText((Join-Path $CaseRoot 'global.json'), $ancestorGlobalJson, $utf8NoBom)
        [System.IO.File]::WriteAllText((Join-Path $CaseRoot 'NuGet.Config'), $ancestorNuGetConfig, $utf8NoBom)
    }

    switch ($Mutation)
    {
        'unstaged'
        {
            [System.IO.File]::AppendAllText((Join-Path $repositoryPath 'tracked.txt'), "unstaged`r`n", $utf8NoBom)
        }
        'staged'
        {
            [System.IO.File]::AppendAllText((Join-Path $repositoryPath 'tracked.txt'), "staged`r`n", $utf8NoBom)
            Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('add', '--', 'tracked.txt') | Out-Null
        }
        'untracked'
        {
            [System.IO.File]::WriteAllText((Join-Path $repositoryPath 'untracked.txt'), "untracked`r`n", $utf8NoBom)
        }
        'ignored-user'
        {
            $userProjectPath = Join-Path $repositoryPath 'WallpaperField.csproj.user'
            [System.IO.File]::WriteAllText(
                $userProjectPath,
                "<Project><PropertyGroup><ReleaseIntegrityProbe>true</ReleaseIntegrityProbe></PropertyGroup></Project>`r`n",
                $utf8NoBom)
            $null = @(& $realGit -C $repositoryPath check-ignore --quiet -- 'WallpaperField.csproj.user' 2>&1)
            if ($LASTEXITCODE -ne 0)
            {
                throw 'The release-child MSBuild user file was not ignored by Git.'
            }
            $ignoredUserStatus = @(Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('status', '--porcelain=v1', '--untracked-files=all'))
            if ($ignoredUserStatus.Count -ne 0)
            {
                throw "The ignored MSBuild user file made the release-child fixture dirty: $($ignoredUserStatus -join ', ')"
            }
        }
        'info-attributes'
        {
            $infoPath = Join-Path $repositoryPath '.git\info'
            [System.IO.Directory]::CreateDirectory($infoPath) | Out-Null
            [System.IO.File]::WriteAllText(
                (Join-Path $infoPath 'attributes'),
                "WallpaperField.csproj export-ignore`r`n",
                $utf8NoBom)
        }
        'replace-ref'
        {
            $head = ((Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('rev-parse', 'HEAD')) -join '').Trim()
            $tree = ((Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('rev-parse', 'HEAD^{tree}')) -join '').Trim()
            $replacement = ((Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @(
                'commit-tree', $tree, '-m', 'fixture replacement commit')) -join '').Trim()
            Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('replace', $head, $replacement) | Out-Null
        }
        'tracked-attributes'
        {
            [System.IO.File]::WriteAllText(
                (Join-Path $repositoryPath '.gitattributes'),
                "WallpaperField.csproj export-ignore`r`n",
                $utf8NoBom)
            Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('add', '--', '.gitattributes') | Out-Null
            Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('commit', '--quiet', '-m', 'fixture tracked archive attributes') | Out-Null
        }
        'symlink-tree-entry'
        {
            $linkTargetPath = Join-Path $CaseRoot 'link-target-bytes.txt'
            [System.IO.File]::WriteAllText($linkTargetPath, "tracked.txt`n", $utf8NoBom)
            $linkBlob = ((Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @(
                '-c', 'core.autocrlf=false', 'hash-object', '-w', '--no-filters', '--', $linkTargetPath)) -join '').Trim()
            Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @(
                'update-index', '--add', '--cacheinfo', "120000,$linkBlob,fixture-link") | Out-Null
            Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @(
                'commit', '--quiet', '-m', 'fixture symlink tree entry') | Out-Null
            [System.IO.File]::WriteAllText((Join-Path $repositoryPath 'fixture-link'), "tracked.txt`n", $utf8NoBom)
        }
        'gitlink-tree-entry'
        {
            $gitlinkCommit = ((Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('rev-parse', 'HEAD')) -join '').Trim()
            Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @(
                'update-index', '--add', '--cacheinfo', "160000,$gitlinkCommit,fixture-gitlink") | Out-Null
            Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @(
                'commit', '--quiet', '-m', 'fixture gitlink tree entry') | Out-Null
            Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @(
                'update-index', '--skip-worktree', 'fixture-gitlink') | Out-Null
        }
        'none'
        {
        }
        default
        {
            throw "Unsupported fixture mutation: $Mutation"
        }
    }

    $fixtureCommit = ((Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('rev-parse', 'HEAD')) -join '').Trim()
    $fakePublishedExecutable = ''
    if ($DotnetMode -eq 'dependency-isolation')
    {
        $fakePublishedExecutable = New-FakePublishedExecutable -CaseRoot $CaseRoot -SourceCommit $fixtureCommit
    }

    $gitShim = @'
@echo off
if /I not "%~1"=="--no-replace-objects" (
    echo RELEASE_GATE_MISSING_NO_REPLACE_OBJECTS %* 1>&2
    exit /b 91
)
if /I "%RELEASE_GATE_GIT_MODE%"=="rev-parse-error" if /I "%~4"=="rev-parse" (
    echo invalid-revision
    exit /b 2
)
if /I "%RELEASE_GATE_GIT_MODE%"=="diff-error" if /I "%~4"=="diff" if /I "%~5"=="--quiet" exit /b 2
if /I "%RELEASE_GATE_GIT_MODE%"=="diff-dirty-empty" if /I "%~4"=="diff" if /I "%~5"=="--quiet" exit /b 1
if /I "%RELEASE_GATE_GIT_MODE%"=="diff-dirty-empty" if /I "%~4"=="diff" if /I "%~5"=="--name-only" exit /b 0
if /I "%RELEASE_GATE_GIT_MODE%"=="name-only-error" if /I "%~4"=="diff" if /I "%~5"=="--name-only" exit /b 2
if /I "%RELEASE_GATE_GIT_MODE%"=="status-error" if /I "%~4"=="status" exit /b 3
if /I "%RELEASE_GATE_GIT_MODE%"=="ignored-user-enumeration-error" if /I "%~4"=="ls-files" exit /b 4
if /I "%RELEASE_GATE_GIT_MODE%"=="post-gate-source-mutation" if /I "%~4"=="ls-files" goto post_gate_source_mutation
"%REAL_GIT_EXE%" %*
set "RELEASE_GATE_GIT_EXIT=%ERRORLEVEL%"
exit /b %RELEASE_GATE_GIT_EXIT%

:post_gate_source_mutation
"%REAL_GIT_EXE%" %*
if errorlevel 1 exit /b %ERRORLEVEL%
>>"%RELEASE_GATE_ACTIVE_PROJECT%" echo ^<!-- RELEASE_POST_GATE_MUTATION --^>
>>"%RELEASE_GATE_ACTIVE_SOLUTION%" echo ^<!-- RELEASE_POST_GATE_MUTATION --^>
>>"%RELEASE_GATE_ACTIVE_NUGET%" echo ^<!-- RELEASE_POST_GATE_MUTATION --^>
>>"%RELEASE_GATE_ACTIVE_LICENSE%" echo RELEASE_POST_GATE_MUTATION
>>"%RELEASE_GATE_ACTIVE_RELEASE_NOTES%" echo RELEASE_POST_GATE_MUTATION
exit /b 0
'@
    $dotnetShim = @'
@echo off
setlocal EnableExtensions EnableDelayedExpansion
>"%RELEASE_GATE_DOTNET_MARKER%" echo called %*
if /I "%RELEASE_GATE_DOTNET_MODE%"=="source-isolation" goto release_mode
if /I "%RELEASE_GATE_DOTNET_MODE%"=="dependency-isolation" goto release_mode
if /I "%RELEASE_GATE_DOTNET_MODE%"=="silent-error" exit /b 97
echo RELEASE_GATE_DOTNET_SENTINEL
exit /b 97

:release_mode
>>"%RELEASE_GATE_DOTNET_CALLS%" echo %*
if /I "%~1"=="--list-sdks" goto source_list_sdks
if /I "%~1"=="--version" goto source_version
if /I "%~1"=="restore" goto source_restore
if /I "%~1"=="publish" goto source_publish
if /I "%~1"=="list" goto source_list_packages
echo RELEASE_GATE_UNEXPECTED_DOTNET_CALL %*
exit /b 98

:source_list_sdks
echo 9.0.306 [%~dp0sdk]
echo 10.0.100 [%~dp0sdk]
echo 10.0.301-preview.1 [%~dp0sdk]
echo 10.0.302 [%~dp0sdk]
echo 11.0.100-preview.1 [%~dp0sdk]
exit /b 0

:source_version
if not exist "%CD%\global.json" exit /b 86
if not exist "%CD%\Directory.Build.rsp" exit /b 87
copy /Y "%CD%\global.json" "%RELEASE_GATE_GLOBAL_OBSERVATION%" >nul
for %%I in ("%CD%\Directory.Build.rsp") do >>"%RELEASE_GATE_RSP_OBSERVATION%" echo LENGTH=%%~zI
>>"%RELEASE_GATE_SOURCE_OBSERVATIONS%" echo VERSION_CWD=%CD%
echo 10.0.302
exit /b 0

:source_restore
call :observe_source RESTORE "%~f2"
exit /b 0

:source_publish
call :observe_source PUBLISH "%~f2"
if /I "%RELEASE_GATE_DOTNET_MODE%"=="dependency-isolation" goto source_publish_success
echo RELEASE_GATE_PUBLISH_SENTINEL
exit /b 97

:source_publish_success
set "PUBLISH_PROJECT=%~f2"
set "PUBLISH_OUTPUT="
:source_publish_scan
if "%~1"=="" goto source_publish_scanned
if /I "%~1"=="--output" set "PUBLISH_OUTPUT=%~f2"
shift
goto source_publish_scan
:source_publish_scanned
if not defined PUBLISH_OUTPUT exit /b 88
if not exist "%RELEASE_GATE_FAKE_EXE%" exit /b 89
mkdir "!PUBLISH_OUTPUT!\ThirdParty\RePKG" >nul 2>&1
copy /Y "%RELEASE_GATE_FAKE_EXE%" "!PUBLISH_OUTPUT!\WallpaperField.exe" >nul
for %%I in ("!PUBLISH_PROJECT!") do set "PUBLISH_PROJECT_ROOT=%%~dpI"
copy /Y "!PUBLISH_PROJECT_ROOT!THIRD-PARTY-NOTICES.md" "!PUBLISH_OUTPUT!\THIRD-PARTY-NOTICES.md" >nul
copy /Y "!PUBLISH_PROJECT_ROOT!ThirdParty\RePKG\LICENSE.txt" "!PUBLISH_OUTPUT!\ThirdParty\RePKG\LICENSE.txt" >nul
copy /Y "!PUBLISH_PROJECT_ROOT!ThirdParty\RePKG\THIRD-PARTY-NOTICES.txt" "!PUBLISH_OUTPUT!\ThirdParty\RePKG\THIRD-PARTY-NOTICES.txt" >nul
copy /Y "!PUBLISH_PROJECT_ROOT!ThirdParty\RePKG\UPSTREAM-PATCHES.md" "!PUBLISH_OUTPUT!\ThirdParty\RePKG\UPSTREAM-PATCHES.md" >nul
exit /b 0

:source_list_packages
set "DEPENDENCY_PROJECT=%~f2"
call :observe_source DEPENDENCY "!DEPENDENCY_PROJECT!"
if /I not "%~3"=="package" exit /b 90
set "DEPENDENCY_NO_RESTORE=False"
:source_list_scan
if "%~1"=="" goto source_list_scanned
if /I "%~1"=="--no-restore" set "DEPENDENCY_NO_RESTORE=True"
shift
goto source_list_scan
:source_list_scanned
>>"%RELEASE_GATE_SOURCE_OBSERVATIONS%" echo DEPENDENCY_NO_RESTORE=!DEPENDENCY_NO_RESTORE!
if /I not "!DEPENDENCY_NO_RESTORE!"=="True" exit /b 92
set "JSON_PROJECT=!DEPENDENCY_PROJECT:\=/!"
echo {"projects":[{"path":"!JSON_PROJECT!","frameworks":[{"topLevelPackages":[{"id":"Release.Integrity.Probe","requestedVersion":"1.0.0","resolvedVersion":"1.0.0"}],"transitivePackages":[]}]}]}
exit /b 0

:observe_source
>>"%RELEASE_GATE_SOURCE_OBSERVATIONS%" echo %~1_PROJECT=%~2
findstr /C:"RELEASE_POST_GATE_MUTATION" "%~2" >nul
if errorlevel 1 (
    >>"%RELEASE_GATE_SOURCE_OBSERVATIONS%" echo %~1_MUTATED=False
) else (
    >>"%RELEASE_GATE_SOURCE_OBSERVATIONS%" echo %~1_MUTATED=True
)
findstr /C:"RELEASE_COMMITTED_SOURCE" "%~2" >nul
if errorlevel 1 (
    >>"%RELEASE_GATE_SOURCE_OBSERVATIONS%" echo %~1_COMMITTED=False
) else (
    >>"%RELEASE_GATE_SOURCE_OBSERVATIONS%" echo %~1_COMMITTED=True
)
exit /b 0
'@
    $childWrapper = @'
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ReleaseScriptPath,
    [Parameter(Mandatory = $true)][string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$ErrorView = 'NormalView'
try
{
    & $ReleaseScriptPath -OutputDirectory $OutputDirectory
}
catch
{
    [Console]::Error.WriteLine(($_ | Format-List * -Force | Out-String))
    exit 1
}
'@
    [System.IO.File]::WriteAllText((Join-Path $toolsPath 'git.cmd'), $gitShim, $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $toolsPath 'dotnet.cmd'), $dotnetShim, $utf8NoBom)
    $childWrapperPath = Join-Path $CaseRoot 'invoke-release-child.ps1'
    [System.IO.File]::WriteAllText($childWrapperPath, $childWrapper, $utf8NoBom)

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $PowerShellPath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.ArgumentList.Add('-NoLogo')
    $startInfo.ArgumentList.Add('-NoProfile')
    $startInfo.ArgumentList.Add('-NonInteractive')
    $startInfo.ArgumentList.Add('-ExecutionPolicy')
    $startInfo.ArgumentList.Add('Bypass')
    $startInfo.ArgumentList.Add('-File')
    $startInfo.ArgumentList.Add($childWrapperPath)
    $startInfo.ArgumentList.Add('-ReleaseScriptPath')
    $startInfo.ArgumentList.Add((Join-Path $repositoryPath 'build-release.ps1'))
    $startInfo.ArgumentList.Add('-OutputDirectory')
    $startInfo.ArgumentList.Add($outputPath)
    $startInfo.Environment['PATH'] = $toolsPath + [System.IO.Path]::PathSeparator + $startInfo.Environment['PATH']
    $startInfo.Environment['REAL_GIT_EXE'] = $realGit
    $startInfo.Environment['RELEASE_GATE_GIT_MODE'] = $GitMode
    $startInfo.Environment['RELEASE_GATE_DOTNET_MARKER'] = $dotnetMarker
    $startInfo.Environment['RELEASE_GATE_DOTNET_MODE'] = $DotnetMode
    $startInfo.Environment['RELEASE_GATE_DOTNET_CALLS'] = $dotnetCalls
    $startInfo.Environment['RELEASE_GATE_SOURCE_OBSERVATIONS'] = $sourceObservations
    $startInfo.Environment['RELEASE_GATE_GLOBAL_OBSERVATION'] = $globalJsonObservation
    $startInfo.Environment['RELEASE_GATE_RSP_OBSERVATION'] = $rspObservation
    $startInfo.Environment['RELEASE_GATE_FAKE_EXE'] = $fakePublishedExecutable
    $startInfo.Environment['RELEASE_GATE_ACTIVE_PROJECT'] = Join-Path $repositoryPath 'WallpaperField.csproj'
    $startInfo.Environment['RELEASE_GATE_ACTIVE_SOLUTION'] = Join-Path $repositoryPath 'WallpaperField.slnx'
    $startInfo.Environment['RELEASE_GATE_ACTIVE_NUGET'] = Join-Path $repositoryPath 'NuGet.Config'
    $startInfo.Environment['RELEASE_GATE_ACTIVE_LICENSE'] = Join-Path $repositoryPath 'LICENSE'
    $startInfo.Environment['RELEASE_GATE_ACTIVE_RELEASE_NOTES'] = Join-Path $repositoryPath 'docs\releases\v1.3.0.md'
    if ([string]::Equals(
            $PowerShellPath,
            $windowsPowerShell,
            [StringComparison]::OrdinalIgnoreCase))
    {
        # A pwsh parent can prepend PowerShell 7 modules that PowerShell 5.1
        # cannot import. Exercise the inbox host with its own module roots.
        $startInfo.Environment['PSModulePath'] = @(
            (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\Modules'),
            (Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules')
        ) -join [System.IO.Path]::PathSeparator
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start())
    {
        throw 'Could not start the release probe child process.'
    }
    $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
    $standardErrorTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
    $standardError = $standardErrorTask.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    $process.Dispose()

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $standardOutput + [Environment]::NewLine + $standardError
        OutputParentExists = Test-Path -LiteralPath $outputParent
        DotnetCalled = Test-Path -LiteralPath $dotnetMarker -PathType Leaf
        DotnetCalls = if (Test-Path -LiteralPath $dotnetCalls -PathType Leaf) {
            [System.IO.File]::ReadAllText($dotnetCalls)
        } else { '' }
        SourceObservations = if (Test-Path -LiteralPath $sourceObservations -PathType Leaf) {
            [System.IO.File]::ReadAllText($sourceObservations)
        } else { '' }
        GlobalJson = if (Test-Path -LiteralPath $globalJsonObservation -PathType Leaf) {
            [System.IO.File]::ReadAllText($globalJsonObservation)
        } else { '' }
        RspObservation = if (Test-Path -LiteralPath $rspObservation -PathType Leaf) {
            [System.IO.File]::ReadAllText($rspObservation)
        } else { '' }
        RepositoryPath = $repositoryPath
    }
}

function Invoke-ReleaseSourceIsolationProbe
{
    param(
        [Parameter(Mandatory = $true)][string] $CaseRoot
    )

    $result = Invoke-ReleaseProbe `
        -CaseRoot $CaseRoot `
        -Mutation 'none' `
        -GitMode 'post-gate-source-mutation' `
        -DotnetMode 'source-isolation'
    $violations = [System.Collections.Generic.List[string]]::new()
    if ($result.ExitCode -eq 0)
    {
        $violations.Add('release child unexpectedly succeeded')
    }
    if (-not $result.OutputParentExists)
    {
        $violations.Add('clean source did not reach release filesystem setup')
    }
    if (-not $result.DotnetCalled)
    {
        $violations.Add('clean source did not reach the dotnet observer')
    }
    if ($result.Output -notlike '*Release publish failed with exit code 97.*')
    {
        $violations.Add('expected controlled publish failure was absent')
    }

    $callLines = @($result.DotnetCalls -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $listSdkCalls = @($callLines | Where-Object { $_ -eq '--list-sdks' })
    $versionCalls = @($callLines | Where-Object { $_ -eq '--version' })
    $restoreCalls = @($callLines | Where-Object { $_ -like 'restore *' })
    $publishCalls = @($callLines | Where-Object { $_ -like 'publish *' })
    if ($listSdkCalls.Count -ne 1)
    {
        $violations.Add("expected one installed-SDK enumeration, observed $($listSdkCalls.Count)")
    }
    if ($versionCalls.Count -ne 1)
    {
        $violations.Add("expected one controlled SDK version check, observed $($versionCalls.Count)")
    }
    if ($restoreCalls.Count -ne 1)
    {
        $violations.Add("expected one restore call, observed $($restoreCalls.Count)")
    }
    if ($publishCalls.Count -ne 1)
    {
        $violations.Add("expected one publish call, observed $($publishCalls.Count)")
    }
    foreach ($command in @($restoreCalls + $publishCalls))
    {
        if ($command -notlike '*-noAutoResponse*')
        {
            $violations.Add("dotnet build command did not disable automatic response files: $command")
        }
        if ($command -notlike '*-p:ImportDirectoryBuildProps=false*')
        {
            $violations.Add("dotnet command did not disable Directory.Build.props: $command")
        }
        if ($command -notlike '*-p:ImportDirectoryBuildTargets=false*')
        {
            $violations.Add("dotnet command did not disable Directory.Build.targets: $command")
        }
        if ($command -notlike '*-p:ImportDirectoryPackagesProps=false*')
        {
            $violations.Add("dotnet command did not disable Directory.Packages.props: $command")
        }
    }
    if ($restoreCalls.Count -eq 1)
    {
        if ($restoreCalls[0] -notlike '*--configfile*NuGet.Config*')
        {
            $violations.Add("restore did not use the private committed NuGet.Config: $($restoreCalls[0])")
        }
        if ($restoreCalls[0] -like "*$CaseRoot\NuGet.Config*")
        {
            $violations.Add('restore consumed the attacker ancestor NuGet.Config')
        }
    }

    if ([string]::IsNullOrWhiteSpace($result.GlobalJson))
    {
        $violations.Add('private global.json was not observed')
    }
    else
    {
        try
        {
            $globalModel = $result.GlobalJson | ConvertFrom-Json
            if ([string] $globalModel.sdk.version -ne '10.0.302')
            {
                $violations.Add("private global.json selected $($globalModel.sdk.version), expected 10.0.302")
            }
            if ([string] $globalModel.sdk.rollForward -ne 'disable')
            {
                $violations.Add('private global.json did not disable SDK roll-forward')
            }
            if ([bool] $globalModel.sdk.allowPrerelease)
            {
                $violations.Add('private global.json allowed prerelease SDKs')
            }
            $sdkPaths = @($globalModel.sdk.paths)
            if ($sdkPaths.Count -ne 1 -or $sdkPaths[0] -ne '$host$')
            {
                $violations.Add("private global.json did not restrict SDK discovery to `$host`$: $($sdkPaths -join ', ')")
            }
        }
        catch
        {
            $violations.Add("private global.json was invalid: $($_.Exception.Message)")
        }
    }
    if ($result.RspObservation -notmatch '(?m)^LENGTH=0\s*$')
    {
        $violations.Add("private Directory.Build.rsp was absent or non-empty: $($result.RspObservation)")
    }

    $observationLines = @($result.SourceObservations -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    foreach ($commandName in @('RESTORE', 'PUBLISH'))
    {
        $projectLine = @($observationLines | Where-Object { $_ -like "$commandName`_PROJECT=*" })
        if ($projectLine.Count -ne 1)
        {
            $violations.Add("$commandName did not record exactly one project path")
            continue
        }
        $projectPath = $projectLine[0].Substring(($commandName + '_PROJECT=').Length)
        if (Test-SameOrChildPath -Child $projectPath -Parent $result.RepositoryPath)
        {
            $violations.Add("$commandName still consumed the mutable active repository: $projectPath")
        }
        if ($observationLines -notcontains "$commandName`_MUTATED=False")
        {
            $violations.Add("$commandName observed the post-gate source mutation")
        }
        if ($observationLines -notcontains "$commandName`_COMMITTED=True")
        {
            $violations.Add("$commandName did not consume the committed source sentinel")
        }
    }

    if ($violations.Count -ne 0)
    {
        throw (($violations -join '; ') + ". Child output:`n" + $result.Output +
            "`nDotnet calls:`n" + $result.DotnetCalls +
            "`nSource observations:`n" + $result.SourceObservations +
            "`nObserved global.json:`n" + $result.GlobalJson +
            "`nObserved rsp:`n" + $result.RspObservation)
    }

    Write-Output 'RELEASE_PRIVATE_SOURCE_CASE name=post-gate-source-and-ancestor-import result=PASS'
}

function Invoke-ReleaseDependencyIsolationProbe
{
    param(
        [Parameter(Mandatory = $true)][string] $CaseRoot
    )

    $result = Invoke-ReleaseProbe `
        -CaseRoot $CaseRoot `
        -Mutation 'none' `
        -GitMode 'post-gate-source-mutation' `
        -DotnetMode 'dependency-isolation' `
        -PowerShellPath $windowsPowerShell
    $violations = [System.Collections.Generic.List[string]]::new()
    if ($result.ExitCode -ne 0)
    {
        $violations.Add("Windows PowerShell 5.1 release child exited $($result.ExitCode)")
    }
    if ($result.Output -notlike '*RELEASE_RESULT version=1.3.0*')
    {
        $violations.Add('release child did not complete the fake candidate transaction')
    }

    $callLines = @($result.DotnetCalls -split '\r?\n' | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    })
    $restoreCalls = @($callLines | Where-Object { $_ -like 'restore *' })
    $publishCalls = @($callLines | Where-Object { $_ -like 'publish *' })
    $dependencyCalls = @($callLines | Where-Object { $_ -like 'list * package *' })
    if ($restoreCalls.Count -ne 1 -or $publishCalls.Count -ne 1 -or $dependencyCalls.Count -ne 1)
    {
        $violations.Add("expected one restore, publish, and dependency call; calls=$($callLines -join ' | ')")
    }
    foreach ($command in @($restoreCalls + $publishCalls))
    {
        if ($command -notlike '*-noAutoResponse*')
        {
            $violations.Add("restore/publish did not disable automatic response files: $command")
        }
    }
    if ($dependencyCalls.Count -eq 1)
    {
        $dependencyCall = $dependencyCalls[0]
        if ($dependencyCall -notlike '*WallpaperField.csproj package*')
        {
            $violations.Add("dependency enumeration did not use the project path: $dependencyCall")
        }
        if ($dependencyCall -like '*.slnx*')
        {
            $violations.Add("dependency enumeration consumed the solution: $dependencyCall")
        }
        if ($dependencyCall -notlike '*--no-restore*')
        {
            $violations.Add("dependency enumeration could restore implicitly: $dependencyCall")
        }
    }
    if ($result.SourceObservations -notmatch '(?m)^DEPENDENCY_NO_RESTORE=True\s*$')
    {
        $violations.Add('fake dependency stage did not observe --no-restore')
    }
    $dependencyProjectLine = @($result.SourceObservations -split '\r?\n' | Where-Object {
        $_ -like 'DEPENDENCY_PROJECT=*'
    })
    if ($dependencyProjectLine.Count -ne 1)
    {
        $violations.Add('dependency stage did not record exactly one project path')
    }
    else
    {
        $dependencyProject = $dependencyProjectLine[0].Substring('DEPENDENCY_PROJECT='.Length)
        if (Test-SameOrChildPath -Child $dependencyProject -Parent $result.RepositoryPath)
        {
            $violations.Add("dependency stage consumed the mutable active repository: $dependencyProject")
        }
    }

    try
    {
        $globalModel = $result.GlobalJson | ConvertFrom-Json
        if ([string] $globalModel.sdk.version -ne '10.0.302' -or
            [string] $globalModel.sdk.rollForward -ne 'disable' -or
            [bool] $globalModel.sdk.allowPrerelease -or
            @($globalModel.sdk.paths).Count -ne 1 -or
            @($globalModel.sdk.paths)[0] -ne '$host$')
        {
            $violations.Add('dependency stage did not run beneath the exact private SDK policy')
        }
    }
    catch
    {
        $violations.Add("dependency stage global.json was invalid or absent: $($_.Exception.Message)")
    }
    if ($result.RspObservation -notmatch '(?m)^LENGTH=0\s*$')
    {
        $violations.Add('dependency stage did not run beneath an empty private Directory.Build.rsp')
    }

    if ($violations.Count -ne 0)
    {
        throw (($violations -join '; ') + ". Child output:`n" + $result.Output +
            "`nDotnet calls:`n" + $result.DotnetCalls +
            "`nSource observations:`n" + $result.SourceObservations +
            "`nObserved global.json:`n" + $result.GlobalJson +
            "`nObserved rsp:`n" + $result.RspObservation)
    }

    Write-Output 'RELEASE_DEPENDENCY_ISOLATION project=True no_restore=True ancestor_solution=False ancestor_rsp=False ancestor_global=False ancestor_nuget=False powershell=5.1 result=PASS'
}

$cases = @(
    [pscustomobject]@{ Name = 'unstaged'; Mutation = 'unstaged'; GitMode = 'normal'; DotnetMode = 'normal'; Expected = 'dirty'; DirtyPath = 'tracked.txt'; Message = 'clean source worktree' },
    [pscustomobject]@{ Name = 'staged'; Mutation = 'staged'; GitMode = 'normal'; DotnetMode = 'normal'; Expected = 'dirty'; DirtyPath = 'tracked.txt'; Message = 'clean source worktree' },
    [pscustomobject]@{ Name = 'untracked'; Mutation = 'untracked'; GitMode = 'normal'; DotnetMode = 'normal'; Expected = 'dirty'; DirtyPath = 'untracked.txt'; Message = 'clean source worktree' },
    [pscustomobject]@{ Name = 'dirty-empty-paths'; Mutation = 'none'; GitMode = 'diff-dirty-empty'; DotnetMode = 'normal'; Expected = 'dirty'; DirtyPath = '<unknown tracked change>'; Message = 'clean source worktree' },
    [pscustomobject]@{ Name = 'rev-parse-error'; Mutation = 'none'; GitMode = 'rev-parse-error'; DotnetMode = 'normal'; Expected = 'error'; DirtyPath = ''; Message = 'Could not resolve a full 40-character source commit.' },
    [pscustomobject]@{ Name = 'diff-error'; Mutation = 'none'; GitMode = 'diff-error'; DotnetMode = 'normal'; Expected = 'error'; DirtyPath = ''; Message = 'Could not inspect tracked source changes.' },
    [pscustomobject]@{ Name = 'name-only-error'; Mutation = 'unstaged'; GitMode = 'name-only-error'; DotnetMode = 'normal'; Expected = 'error'; DirtyPath = ''; Message = 'Could not enumerate tracked source changes.' },
    [pscustomobject]@{ Name = 'status-error'; Mutation = 'none'; GitMode = 'status-error'; DotnetMode = 'normal'; Expected = 'error'; DirtyPath = ''; Message = 'Could not inspect the source worktree state.' },
    [pscustomobject]@{ Name = 'ignored-user'; Mutation = 'ignored-user'; GitMode = 'normal'; DotnetMode = 'normal'; Expected = 'dirty'; DirtyPath = 'WallpaperField.csproj.user'; Message = 'ignored MSBuild user files' },
    [pscustomobject]@{ Name = 'ignored-user-enumeration-error'; Mutation = 'none'; GitMode = 'ignored-user-enumeration-error'; DotnetMode = 'normal'; Expected = 'error'; DirtyPath = ''; Message = 'Could not inspect ignored MSBuild user files.' },
    [pscustomobject]@{ Name = 'info-attributes'; Mutation = 'info-attributes'; GitMode = 'normal'; DotnetMode = 'normal'; Expected = 'error'; DirtyPath = ''; Message = 'repository-local Git attributes' },
    [pscustomobject]@{ Name = 'replace-ref'; Mutation = 'replace-ref'; GitMode = 'normal'; DotnetMode = 'normal'; Expected = 'error'; DirtyPath = ''; Message = 'Git replace refs' },
    [pscustomobject]@{ Name = 'tracked-attributes'; Mutation = 'tracked-attributes'; GitMode = 'normal'; DotnetMode = 'normal'; Expected = 'pre-dotnet'; DirtyPath = ''; Message = 'exact committed blob contract' },
    [pscustomobject]@{ Name = 'symlink-tree-entry'; Mutation = 'symlink-tree-entry'; GitMode = 'normal'; DotnetMode = 'normal'; Expected = 'error'; DirtyPath = ''; Message = 'unsupported Git tree entry' },
    [pscustomobject]@{ Name = 'gitlink-tree-entry'; Mutation = 'gitlink-tree-entry'; GitMode = 'normal'; DotnetMode = 'normal'; Expected = 'error'; DirtyPath = ''; Message = 'unsupported Git tree entry' },
    [pscustomobject]@{ Name = 'clean-control'; Mutation = 'none'; GitMode = 'normal'; DotnetMode = 'normal'; Expected = 'clean'; DirtyPath = ''; Message = 'Could not enumerate installed .NET SDKs.' },
    [pscustomobject]@{ Name = 'clean-silent-dotnet-error'; Mutation = 'none'; GitMode = 'normal'; DotnetMode = 'silent-error'; Expected = 'clean'; DirtyPath = ''; Message = 'Could not enumerate installed .NET SDKs.' }
)

[System.IO.Directory]::CreateDirectory($resolvedWorkRoot) | Out-Null
$sessionRoot = Join-Path $resolvedWorkRoot ('rsc-' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($sessionRoot) | Out-Null
$powerShell51Parent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$powerShell51CaseRoot = Join-Path $powerShell51Parent ('rsc-' + [Guid]::NewGuid().ToString('N'))
$failures = [System.Collections.Generic.List[string]]::new()
$cleanlinessFailures = [System.Collections.Generic.List[string]]::new()
$passed = 0
$privateSourcePassed = 0

try
{
    Invoke-PowerShell51AstProbe -CaseRoot (Join-Path $sessionRoot 'ast')
    Invoke-AncestorBuildImportProbe -CaseRoot (Join-Path $sessionRoot 'a')

    try
    {
        Invoke-ReleaseSourceIsolationProbe -CaseRoot (Join-Path $sessionRoot 'p')
        $privateSourcePassed++
    }
    catch
    {
        $detail = "post-gate-source-and-ancestor-import: $($_.Exception.Message)"
        $failures.Add($detail)
        Write-Output "RELEASE_PRIVATE_SOURCE_CASE name=post-gate-source-and-ancestor-import result=FAIL detail=$($_.Exception.Message)"
    }

    try
    {
        # Keep this execution fixture below MAX_PATH so the result measures
        # PowerShell 5.1/API compatibility rather than the legacy host's
        # manifest-dependent long-path policy.
        Invoke-ReleaseDependencyIsolationProbe -CaseRoot $powerShell51CaseRoot
        $privateSourcePassed++
    }
    catch
    {
        $detail = "dependency-and-powershell51-isolation: $($_.Exception.Message)"
        $failures.Add($detail)
        Write-Output "RELEASE_PRIVATE_SOURCE_CASE name=dependency-and-powershell51-isolation result=FAIL detail=$($_.Exception.Message)"
    }

    Invoke-ReleaseItemEvaluationProbe -CaseRoot (Join-Path $sessionRoot 'i')

    foreach ($case in $cases)
    {
        try
        {
            $caseRoot = Join-Path $sessionRoot $case.Name
            $result = Invoke-ReleaseProbe -CaseRoot $caseRoot -Mutation $case.Mutation -GitMode $case.GitMode -DotnetMode $case.DotnetMode
            $messageSeen = $result.Output -like "*$($case.Message)*"
            $dirtyPathSeen = [string]::IsNullOrEmpty($case.DirtyPath) -or $result.Output -like "*$($case.DirtyPath)*"
            Write-Output ("RELEASE_SOURCE_CLEANLINESS_OBSERVATION name={0} exit={1} output_parent={2} dotnet={3} message={4} dirty_path={5}" -f `
                $case.Name, $result.ExitCode, $result.OutputParentExists, $result.DotnetCalled, $messageSeen, $dirtyPathSeen)
            $violations = [System.Collections.Generic.List[string]]::new()
            if ($result.ExitCode -eq 0)
            {
                $violations.Add('release child unexpectedly succeeded')
            }

            if ($case.Expected -eq 'clean')
            {
                if (-not $result.OutputParentExists) { $violations.Add('clean source did not reach release filesystem setup') }
                if (-not $result.DotnetCalled) { $violations.Add('clean source did not reach the dotnet sentinel') }
                if (-not $messageSeen) { $violations.Add('expected downstream dotnet error message was absent') }
            }
            elseif ($case.Expected -eq 'pre-dotnet')
            {
                if (-not $result.OutputParentExists) { $violations.Add('tree-integrity case did not reach private materialization') }
                if ($result.DotnetCalled) { $violations.Add('dotnet ran before tree-integrity rejection') }
                if (-not $messageSeen) { $violations.Add('expected tree-integrity rejection message was absent') }
            }
            else
            {
                if ($result.OutputParentExists) { $violations.Add('release output parent was created before rejection') }
                if ($result.DotnetCalled) { $violations.Add('dotnet ran before rejection') }
                if (-not $messageSeen) { $violations.Add('expected rejection message was absent') }
                if ($case.Expected -eq 'dirty' -and -not $dirtyPathSeen) { $violations.Add('dirty path was not reported') }
            }

            if ($violations.Count -gt 0)
            {
                throw (($violations -join '; ') + ". Child output:`n" + $result.Output)
            }

            $passed++
            Write-Output "RELEASE_SOURCE_CLEANLINESS_CASE name=$($case.Name) result=PASS"
        }
        catch
        {
            $detail = "$($case.Name): $($_.Exception.Message)"
            $failures.Add($detail)
            $cleanlinessFailures.Add($detail)
            Write-Output "RELEASE_SOURCE_CLEANLINESS_CASE name=$($case.Name) result=FAIL detail=$($_.Exception.Message)"
        }
    }
}
finally
{
    Remove-VerifiedTestRoot -Path $sessionRoot -Parent $resolvedWorkRoot
    Remove-VerifiedTestRoot -Path $powerShell51CaseRoot -Parent $powerShell51Parent
}

$privateSourceFailed = 2 - $privateSourcePassed
Write-Output "RELEASE_PRIVATE_SOURCE_RESULT cases=2 passed=$privateSourcePassed failed=$privateSourceFailed"
Write-Output "RELEASE_SOURCE_CLEANLINESS_RESULT cases=$($cases.Count) passed=$passed failed=$($cleanlinessFailures.Count)"
if ($failures.Count -gt 0)
{
    foreach ($failure in $failures)
    {
        Write-Error $failure
    }
    exit 1
}

# Expected child failures must not leak a stale native exit code to callers.
exit 0
