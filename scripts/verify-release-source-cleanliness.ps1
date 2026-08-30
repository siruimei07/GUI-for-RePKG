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
        $leaf -notmatch '^release-source-cleanliness-[0-9a-f]{32}$')
    {
        throw "Refusing to remove an unverified test root: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath)
    {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
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
    Copy-Item -LiteralPath $sourceProjectPath -Destination (Join-Path $repositoryPath 'WallpaperField.csproj') -Force
    Copy-Item -LiteralPath $sourceIgnorePath -Destination (Join-Path $repositoryPath '.gitignore') -Force
    $null = @(& $realGit -C $repositoryPath diff --quiet -- WallpaperField.csproj .gitignore 2>&1)
    $snapshotDiffExitCode = $LASTEXITCODE
    if ($snapshotDiffExitCode -ne 0 -and $snapshotDiffExitCode -ne 1)
    {
        throw 'Could not inspect the item-evaluation fixture snapshot.'
    }
    if ($snapshotDiffExitCode -eq 1)
    {
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

    $cleanAfterEvaluation = @(Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('status', '--porcelain=v1', '--untracked-files=all'))
    if ($cleanAfterEvaluation.Count -ne 0)
    {
        throw "MSBuild item evaluation made the fixture clone dirty: $($cleanAfterEvaluation -join ', ')"
    }

    Write-Output ("RELEASE_ITEM_EVALUATION probes={0} ignored=True git_clean=True compile_contains_probes=False compile_count={1} result=PASS" -f $probeRelativePaths.Count, $compileItems.Count)
}

function Invoke-ReleaseProbe
{
    param(
        [Parameter(Mandatory = $true)][string] $CaseRoot,
        [Parameter(Mandatory = $true)][string] $Mutation,
        [Parameter(Mandatory = $true)][string] $GitMode,
        [Parameter(Mandatory = $true)][string] $DotnetMode
    )

    $repositoryPath = Join-Path $CaseRoot 'repository'
    $toolsPath = Join-Path $CaseRoot 'tools'
    $outputParent = Join-Path $CaseRoot 'release-output'
    $outputPath = Join-Path $outputParent 'candidate'
    $dotnetMarker = Join-Path $CaseRoot 'dotnet-called.txt'
    [System.IO.Directory]::CreateDirectory($repositoryPath) | Out-Null
    [System.IO.Directory]::CreateDirectory($toolsPath) | Out-Null

    Copy-Item -LiteralPath $resolvedReleaseScript -Destination (Join-Path $repositoryPath 'build-release.ps1')
    [System.IO.File]::WriteAllText((Join-Path $repositoryPath 'tracked.txt'), "baseline`r`n", $utf8NoBom)
    Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('-c', 'core.longpaths=true', 'init', '--quiet') | Out-Null
    Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('config', 'core.longpaths', 'true') | Out-Null
    Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('config', 'user.name', 'Wallpaper Field Release Probe') | Out-Null
    Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('config', 'user.email', 'release-probe@example.invalid') | Out-Null
    Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('add', '--', 'build-release.ps1', 'tracked.txt') | Out-Null
    Invoke-GitChecked -RepositoryPath $repositoryPath -Arguments @('commit', '--quiet', '-m', 'fixture baseline') | Out-Null

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
        'none'
        {
        }
        default
        {
            throw "Unsupported fixture mutation: $Mutation"
        }
    }

    $gitShim = @'
@echo off
if /I "%RELEASE_GATE_GIT_MODE%"=="rev-parse-error" if /I "%~3"=="rev-parse" (
    echo invalid-revision
    exit /b 2
)
if /I "%RELEASE_GATE_GIT_MODE%"=="diff-error" if /I "%~3"=="diff" if /I "%~4"=="--quiet" exit /b 2
if /I "%RELEASE_GATE_GIT_MODE%"=="diff-dirty-empty" if /I "%~3"=="diff" if /I "%~4"=="--quiet" exit /b 1
if /I "%RELEASE_GATE_GIT_MODE%"=="diff-dirty-empty" if /I "%~3"=="diff" if /I "%~4"=="--name-only" exit /b 0
if /I "%RELEASE_GATE_GIT_MODE%"=="name-only-error" if /I "%~3"=="diff" if /I "%~4"=="--name-only" exit /b 2
if /I "%RELEASE_GATE_GIT_MODE%"=="status-error" if /I "%~3"=="status" exit /b 3
"%REAL_GIT_EXE%" %*
set "RELEASE_GATE_GIT_EXIT=%ERRORLEVEL%"
exit /b %RELEASE_GATE_GIT_EXIT%
'@
    $dotnetShim = @'
@echo off
>"%RELEASE_GATE_DOTNET_MARKER%" echo called %*
if /I "%RELEASE_GATE_DOTNET_MODE%"=="silent-error" exit /b 97
echo RELEASE_GATE_DOTNET_SENTINEL
exit /b 97
'@
    $childWrapper = @'
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ReleaseScriptPath,
    [Parameter(Mandatory = $true)][string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$ErrorView = 'DetailedView'
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
    $startInfo.FileName = $pwsh
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.ArgumentList.Add('-NoLogo')
    $startInfo.ArgumentList.Add('-NoProfile')
    $startInfo.ArgumentList.Add('-NonInteractive')
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

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start())
    {
        throw 'Could not start the release probe child process.'
    }
    $standardOutput = $process.StandardOutput.ReadToEnd()
    $standardError = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $process.Dispose()

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $standardOutput + [Environment]::NewLine + $standardError
        OutputParentExists = Test-Path -LiteralPath $outputParent
        DotnetCalled = Test-Path -LiteralPath $dotnetMarker -PathType Leaf
    }
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
    [pscustomobject]@{ Name = 'clean-control'; Mutation = 'none'; GitMode = 'normal'; DotnetMode = 'normal'; Expected = 'clean'; DirtyPath = ''; Message = 'Could not determine the .NET SDK version.' },
    [pscustomobject]@{ Name = 'clean-silent-dotnet-error'; Mutation = 'none'; GitMode = 'normal'; DotnetMode = 'silent-error'; Expected = 'clean'; DirtyPath = ''; Message = 'Could not determine the .NET SDK version.' }
)

[System.IO.Directory]::CreateDirectory($resolvedWorkRoot) | Out-Null
$sessionRoot = Join-Path $resolvedWorkRoot ('release-source-cleanliness-' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($sessionRoot) | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()
$passed = 0

try
{
    Invoke-ReleaseItemEvaluationProbe -CaseRoot (Join-Path $sessionRoot 'item-evaluation')

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
            $failures.Add("$($case.Name): $($_.Exception.Message)")
            Write-Output "RELEASE_SOURCE_CLEANLINESS_CASE name=$($case.Name) result=FAIL detail=$($_.Exception.Message)"
        }
    }
}
finally
{
    Remove-VerifiedTestRoot -Path $sessionRoot -Parent $resolvedWorkRoot
}

Write-Output "RELEASE_SOURCE_CLEANLINESS_RESULT cases=$($cases.Count) passed=$passed failed=$($failures.Count)"
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
