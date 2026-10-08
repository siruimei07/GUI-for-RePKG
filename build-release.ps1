[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,

    [switch]$UpdateTrackedExecutable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ProductVersion = '1.4.0'
$FileVersion = '1.4.0.0'
$AssemblyVersion = '1.0.0.0'
$RuntimeIdentifier = 'win-x64'
$Configuration = 'Release'
$ZipName = "WallpaperField-v$ProductVersion-win-x64.zip"
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Get-NormalizedPath
{
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::Equals($fullPath, $root, [StringComparison]::OrdinalIgnoreCase))
    {
        return $root
    }

    return $fullPath.TrimEnd([char[]]@('\', '/'))
}

function Test-SameOrChildPath
{
    param(
        [Parameter(Mandatory = $true)][string]$Child,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $normalizedChild = Get-NormalizedPath $Child
    $normalizedParent = Get-NormalizedPath $Parent
    if ([string]::Equals(
            $normalizedChild,
            $normalizedParent,
            [StringComparison]::OrdinalIgnoreCase))
    {
        return $true
    }

    $prefix = $normalizedParent
    if (-not $prefix.EndsWith([string][System.IO.Path]::DirectorySeparatorChar))
    {
        $prefix += [System.IO.Path]::DirectorySeparatorChar
    }

    return $normalizedChild.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparseInExistingPath
{
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $fullPath = Get-NormalizedPath $Path
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if (-not (Test-Path -LiteralPath $root -PathType Container))
    {
        throw "$Description is on an unavailable volume: $root"
    }
    $rootAttributes = [System.IO.File]::GetAttributes($root)
    if (($rootAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
    {
        throw "$Description has a reparse-point volume root: $root"
    }

    $current = $root
    $segments = $fullPath.Substring($root.Length).Split(
        [char[]]@('\', '/'),
        [StringSplitOptions]::RemoveEmptyEntries)
    foreach ($segment in $segments)
    {
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current))
        {
            return
        }
        $attributes = [System.IO.File]::GetAttributes($current)
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
        {
            throw "$Description contains a reparse point: $current"
        }
    }
}

function Assert-ExactSet
{
    param(
        [Parameter(Mandatory = $true)][string[]]$Actual,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $reference = @($Expected | Sort-Object)
    $differenceSet = @($Actual | Sort-Object)
    $difference = @(Compare-Object -ReferenceObject $reference -DifferenceObject $differenceSet -CaseSensitive)
    if ($difference.Count -ne 0)
    {
        throw "$Description differs from its exact contract: $($difference | Out-String)"
    }
}

function Write-Utf8File
{
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content, $Utf8NoBom)
}

function Get-Sha256
{
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Copy-VerifiedFile
{
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $destinationParent = Split-Path -Parent $Destination
    [System.IO.Directory]::CreateDirectory($destinationParent) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination
    if ((Get-Sha256 $Source) -ne (Get-Sha256 $Destination))
    {
        throw "Copied file hash mismatch: $Source -> $Destination"
    }
}

function Get-ZipEntrySha256
{
    param(
        [Parameter(Mandatory = $true)]$Archive,
        [Parameter(Mandatory = $true)][string]$EntryName
    )

    $entry = $Archive.GetEntry($EntryName)
    if ($null -eq $entry)
    {
        throw "ZIP entry is missing: $EntryName"
    }

    $stream = $entry.Open()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try
    {
        return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '')
    }
    finally
    {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Expand-ReleaseSourceZip
{
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$DestinationRoot
    )

    $normalizedRoot = Get-NormalizedPath $DestinationRoot
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try
    {
        foreach ($entry in $archive.Entries)
        {
            $relativePath = $entry.FullName.Replace('\', '/').TrimEnd('/')
            if ([string]::IsNullOrWhiteSpace($relativePath))
            {
                continue
            }
            $segments = @($relativePath.Split([char]47))
            if ([System.IO.Path]::IsPathRooted($relativePath) -or
                @($segments | Where-Object {
                    [string]::IsNullOrWhiteSpace($_) -or $_ -eq '.' -or $_ -eq '..'
                }).Count -ne 0)
            {
                throw "Committed release ZIP contains an unsafe entry path: $($entry.FullName)"
            }

            $destinationPath = Get-NormalizedPath (Join-Path $normalizedRoot $relativePath.Replace('/', '\'))
            if (-not (Test-SameOrChildPath -Child $destinationPath -Parent $normalizedRoot) -or
                [string]::Equals($destinationPath, $normalizedRoot, [StringComparison]::OrdinalIgnoreCase))
            {
                throw "Committed release ZIP escapes its destination: $($entry.FullName)"
            }
            if ([string]::IsNullOrEmpty($entry.Name))
            {
                continue
            }
            if (Test-Path -LiteralPath $destinationPath)
            {
                throw "Committed release ZIP contains a duplicate entry path: $($entry.FullName)"
            }

            [System.IO.Directory]::CreateDirectory((Split-Path -Parent $destinationPath)) | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile(
                $entry,
                $destinationPath,
                $false)
        }
    }
    finally
    {
        $archive.Dispose()
    }
}

function Remove-VerifiedWorkspace
{
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedParent,
        [Parameter(Mandatory = $true)][string]$ExpectedLeafPrefix
    )

    $normalizedPath = Get-NormalizedPath $Path
    $actualParent = Get-NormalizedPath (Split-Path -Parent $normalizedPath)
    $leaf = Split-Path -Leaf $normalizedPath
    $isExpectedParent = [string]::Equals(
        $actualParent,
        (Get-NormalizedPath $ExpectedParent),
        [StringComparison]::OrdinalIgnoreCase)
    if (-not $isExpectedParent -or -not $leaf.StartsWith($ExpectedLeafPrefix, [StringComparison]::Ordinal))
    {
        throw "Refusing to remove an unverified build workspace: $normalizedPath"
    }

    if (Test-Path -LiteralPath $normalizedPath)
    {
        Assert-NoReparseInExistingPath $normalizedPath 'Release build workspace'
        Remove-Item -LiteralPath $normalizedPath -Recurse -Force
    }
}

function Update-TrackedExecutableAtomically
{
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$ExpectedHash
    )

    $destinationParent = Get-NormalizedPath (Split-Path -Parent $Destination)
    $temporaryPath = Join-Path $destinationParent ".GUI_for_RePKG.exe.$([Guid]::NewGuid().ToString('N')).tmp"
    $backupPath = Join-Path $destinationParent ".GUI_for_RePKG.exe.$([Guid]::NewGuid().ToString('N')).backup"

    Copy-Item -LiteralPath $Source -Destination $temporaryPath
    if ((Get-Sha256 $temporaryPath) -ne $ExpectedHash)
    {
        Remove-Item -LiteralPath $temporaryPath -Force
        throw 'Temporary tracked executable did not match the verified candidate hash.'
    }

    if (Test-Path -LiteralPath $Destination -PathType Leaf)
    {
        [System.IO.File]::Replace($temporaryPath, $Destination, $backupPath, $true)
    }
    else
    {
        [System.IO.File]::Move($temporaryPath, $Destination)
    }

    if ((Get-Sha256 $Destination) -ne $ExpectedHash)
    {
        if (Test-Path -LiteralPath $backupPath -PathType Leaf)
        {
            [System.IO.File]::Replace($backupPath, $Destination, $null, $true)
        }
        throw 'Tracked executable replacement did not preserve the verified bytes.'
    }

    if (Test-Path -LiteralPath $backupPath -PathType Leaf)
    {
        Remove-Item -LiteralPath $backupPath -Force
    }
}

$projectRoot = Get-NormalizedPath $PSScriptRoot
$gitCommand = @(Get-Command git -CommandType Application -ErrorAction Stop)[0].Source
$rootExecutable = Join-Path $projectRoot 'GUI_for_RePKG.exe'
$unresolvedOutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$outputPath = Get-NormalizedPath $unresolvedOutputPath
$outputParent = Get-NormalizedPath (Split-Path -Parent $outputPath)
$outputLeaf = Split-Path -Leaf $outputPath

if ([string]::IsNullOrWhiteSpace($outputLeaf))
{
    throw 'OutputDirectory must name a dedicated candidate directory.'
}
if (Test-SameOrChildPath -Child $projectRoot -Parent $outputPath)
{
    throw "OutputDirectory cannot be the repository root or one of its ancestors: $outputPath"
}
if (Test-SameOrChildPath -Child $outputPath -Parent $projectRoot)
{
    $allowedTemp = Join-Path $projectRoot 'temp'
    $allowedArtifacts = Join-Path $projectRoot 'artifacts'
    $isAllowedTemp = Test-SameOrChildPath -Child $outputPath -Parent $allowedTemp
    $isAllowedArtifact = Test-SameOrChildPath -Child $outputPath -Parent $allowedArtifacts
    if (-not $isAllowedTemp -and -not $isAllowedArtifact)
    {
        throw 'In-repository release output is allowed only below temp or artifacts.'
    }
}
if (Test-Path -LiteralPath $outputPath)
{
    throw "OutputDirectory must not already exist: $outputPath"
}
if (Test-Path -LiteralPath $outputParent -PathType Leaf)
{
    throw "OutputDirectory parent is a file: $outputParent"
}

function Get-CommitBlobEntries
{
    param(
        [Parameter(Mandatory = $true)][string]$GitCommand,
        [Parameter(Mandatory = $true)][string]$RepositoryPath,
        [Parameter(Mandatory = $true)][string]$Commit
    )

    $treeOutput = @(& $GitCommand '--no-replace-objects' -C $RepositoryPath `
        ls-tree -r -z --full-tree $Commit 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "Could not enumerate the committed Git tree (exit $LASTEXITCODE): $($treeOutput -join [Environment]::NewLine)"
    }
    if ($treeOutput.Count -ne 1)
    {
        throw 'Committed Git tree enumeration did not return one NUL-delimited stream.'
    }

    $rawTree = [string]$treeOutput[0]
    $records = @($rawTree.Split([char[]]@([char]0), [StringSplitOptions]::RemoveEmptyEntries))
    if ($records.Count -eq 0)
    {
        throw 'Committed Git tree did not contain any files.'
    }

    $entries = [System.Collections.Generic.List[object]]::new()
    $caseInsensitivePaths = @{}
    $invalidFileNameCharacters = [System.IO.Path]::GetInvalidFileNameChars()
    foreach ($record in $records)
    {
        $tabIndex = $record.IndexOf([char]9)
        if ($tabIndex -le 0)
        {
            throw 'Committed Git tree returned a malformed entry.'
        }
        $header = $record.Substring(0, $tabIndex)
        $path = $record.Substring($tabIndex + 1)
        $headerMatch = [regex]::Match(
            $header,
            '^(?<mode>[0-7]{6}) (?<type>[a-z]+) (?<object>[0-9a-fA-F]{40})$')
        if (-not $headerMatch.Success)
        {
            throw "Committed Git tree returned a malformed entry header: $header"
        }

        $mode = $headerMatch.Groups['mode'].Value
        $type = $headerMatch.Groups['type'].Value
        if ($type -ne 'blob' -or ($mode -ne '100644' -and $mode -ne '100755'))
        {
            throw "Committed source contains an unsupported Git tree entry: mode=$mode type=$type path=$path"
        }
        if ([string]::IsNullOrWhiteSpace($path) -or
            [System.IO.Path]::IsPathRooted($path) -or
            $path.IndexOf([char]92) -ge 0)
        {
            throw "Committed Git tree contains an unsafe Windows path: $path"
        }

        $segments = @($path.Split([char]47))
        foreach ($segment in $segments)
        {
            if ([string]::IsNullOrWhiteSpace($segment) -or
                $segment -eq '.' -or
                $segment -eq '..' -or
                $segment.IndexOfAny($invalidFileNameCharacters) -ge 0 -or
                $segment.EndsWith('.', [StringComparison]::Ordinal) -or
                $segment.EndsWith(' ', [StringComparison]::Ordinal) -or
                $segment -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$')
            {
                throw "Committed Git tree contains an unsafe Windows path segment: $path"
            }
        }
        if ($caseInsensitivePaths.ContainsKey($path))
        {
            throw "Committed Git tree contains case-insensitive duplicate paths: $path"
        }
        $caseInsensitivePaths[$path] = $true

        $entries.Add([pscustomobject]@{
            Path = $path
            ObjectId = $headerMatch.Groups['object'].Value.ToLowerInvariant()
        })
    }

    return $entries.ToArray()
}

function Assert-CommitSourceExact
{
    param(
        [Parameter(Mandatory = $true)][object[]]$Entries,
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$GitCommand,
        [Parameter(Mandatory = $true)][string]$RepositoryPath
    )

    $sourcePrefix = (Get-NormalizedPath $SourceRoot) + [System.IO.Path]::DirectorySeparatorChar
    $actualFiles = @(Get-ChildItem -LiteralPath $SourceRoot -File -Force -Recurse | ForEach-Object {
        $_.FullName.Substring($sourcePrefix.Length).Replace('\', '/')
    })
    $expectedFiles = @($Entries | ForEach-Object { [string]$_.Path })
    try
    {
        Assert-ExactSet $actualFiles $expectedFiles 'Committed release source exact committed blob contract'
    }
    catch
    {
        throw "Committed release source violates the exact committed blob contract: $($_.Exception.Message)"
    }

    foreach ($entry in $Entries)
    {
        $sourcePath = Join-Path $SourceRoot ([string]$entry.Path).Replace('/', '\')
        $hashOutput = @(& $GitCommand '--no-replace-objects' -C $RepositoryPath `
            hash-object --no-filters -- $sourcePath 2>&1)
        $hashExitCode = $LASTEXITCODE
        $actualObjectId = ($hashOutput -join '').Trim().ToLowerInvariant()
        if ($hashExitCode -ne 0 -or $actualObjectId -notmatch '^[0-9a-f]{40}$')
        {
            throw "Could not hash an extracted committed blob without filters: $($entry.Path)"
        }
        if ($actualObjectId -ne [string]$entry.ObjectId)
        {
            throw "Committed release source violates the exact committed blob contract: $($entry.Path)"
        }
    }
}

function Get-ControlledSdkSelection
{
    param(
        [Parameter(Mandatory = $true)][string]$DotnetCommand
    )

    $sdkOutput = @(& $DotnetCommand --list-sdks 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Could not enumerate installed .NET SDKs.'
    }

    $hostSdkRoot = Get-NormalizedPath (Join-Path (Split-Path -Parent $DotnetCommand) 'sdk')
    $candidates = [System.Collections.Generic.List[object]]::new()
    foreach ($lineValue in $sdkOutput)
    {
        $line = [string]$lineValue
        $match = [regex]::Match(
            $line,
            '^\s*(?<version>10\.\d+\.\d+)\s+\[(?<path>.+)\]\s*$')
        if (-not $match.Success)
        {
            continue
        }

        $sdkRoot = Get-NormalizedPath $match.Groups['path'].Value
        if (-not [string]::Equals(
                $sdkRoot,
                $hostSdkRoot,
                [StringComparison]::OrdinalIgnoreCase))
        {
            continue
        }

        $version = $null
        if (-not [Version]::TryParse($match.Groups['version'].Value, [ref]$version) -or
            $version.Major -ne 10)
        {
            continue
        }
        $candidates.Add([pscustomobject]@{
            Text = $match.Groups['version'].Value
            Version = $version
        })
    }
    if ($candidates.Count -eq 0)
    {
        throw "No stable .NET 10 SDK is installed beside the resolved dotnet host: $hostSdkRoot"
    }

    return @($candidates | Sort-Object -Property Version -Descending | Select-Object -First 1)[0]
}

$sourceCommitOutput = @(& $gitCommand '--no-replace-objects' -C $projectRoot rev-parse HEAD 2>&1)
$sourceCommitExitCode = $LASTEXITCODE
$sourceCommit = ($sourceCommitOutput -join '').Trim()
if ($sourceCommitExitCode -ne 0 -or $sourceCommit -notmatch '^[0-9a-fA-F]{40}$')
{
    throw 'Could not resolve a full 40-character source commit.'
}

# Respect the checkout's EOL normalization; overriding it can make a fresh
# Windows checkout appear tracked-dirty before Git refreshes its index.
$null = @(& $gitCommand '--no-replace-objects' -C $projectRoot diff --quiet HEAD -- 2>&1)
$trackedDiffExitCode = $LASTEXITCODE
if ($trackedDiffExitCode -ne 0 -and $trackedDiffExitCode -ne 1)
{
    throw 'Could not inspect tracked source changes.'
}

$trackedDiffPaths = @(& $gitCommand '--no-replace-objects' -C $projectRoot diff --name-only HEAD -- 2>&1)
if ($LASTEXITCODE -ne 0)
{
    throw 'Could not enumerate tracked source changes.'
}
$trackedDiffPaths = @($trackedDiffPaths | ForEach-Object { [string] $_ } | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_)
})

$worktreeStatusEntries = @(& $gitCommand '--no-replace-objects' -C $projectRoot status --porcelain=v1 --untracked-files=all 2>&1)
if ($LASTEXITCODE -ne 0)
{
    throw 'Could not inspect the source worktree state.'
}
$worktreeStatusEntries = @($worktreeStatusEntries | ForEach-Object { [string] $_ } | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_)
})

# MSBuild imports project user files even when Git intentionally hides them
# from porcelain status. A release must contain only commit-addressed inputs.
$ignoredUserPaths = @(& $gitCommand '--no-replace-objects' -C $projectRoot ls-files --others --ignored --exclude-standard -- `
    '*.user' 2>&1)
if ($LASTEXITCODE -ne 0)
{
    throw 'Could not inspect ignored MSBuild user files.'
}
$ignoredUserPaths = @($ignoredUserPaths | ForEach-Object { [string] $_ } | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_)
} | Sort-Object -Unique)
if ($ignoredUserPaths.Count -gt 0)
{
    $displayLimit = 20
    $displayPaths = @($ignoredUserPaths | Select-Object -First $displayLimit)
    $remainingCount = $ignoredUserPaths.Count - $displayPaths.Count
    $remainingSuffix = if ($remainingCount -gt 0) { " (+$remainingCount more)" } else { '' }
    throw "Release builds reject ignored MSBuild user files. Ignored paths: $($displayPaths -join ', ')$remainingSuffix"
}

if ($trackedDiffExitCode -eq 1 -or $worktreeStatusEntries.Count -gt 0)
{
    $dirtyPathEvidence = @(@(
        $trackedDiffPaths
        $worktreeStatusEntries | ForEach-Object {
            if ($_.Length -gt 3) { $_.Substring(3) } else { $_ }
        }
    ) | Sort-Object -Unique)
    if ($dirtyPathEvidence.Count -eq 0)
    {
        $dirtyPathEvidence = @('<unknown tracked change>')
    }
    $displayLimit = 20
    $displayPaths = @($dirtyPathEvidence | Select-Object -First $displayLimit)
    $remainingCount = $dirtyPathEvidence.Count - $displayPaths.Count
    $remainingSuffix = if ($remainingCount -gt 0) { " (+$remainingCount more)" } else { '' }
    throw "Release builds require a clean source worktree. Dirty paths: $($displayPaths -join ', ')$remainingSuffix"
}

$infoAttributesOutput = @(& $gitCommand '--no-replace-objects' -C $projectRoot `
    rev-parse --git-path info/attributes 2>&1)
if ($LASTEXITCODE -ne 0 -or $infoAttributesOutput.Count -ne 1)
{
    throw 'Could not resolve repository-local Git attributes.'
}
$infoAttributesPath = [string]$infoAttributesOutput[0]
if (-not [System.IO.Path]::IsPathRooted($infoAttributesPath))
{
    $infoAttributesPath = Join-Path $projectRoot $infoAttributesPath
}
if (Test-Path -LiteralPath (Get-NormalizedPath $infoAttributesPath))
{
    throw 'Release builds reject repository-local Git attributes because they can alter archive bytes.'
}

if (Test-Path -LiteralPath 'Env:GIT_REPLACE_REF_BASE')
{
    $replaceRefBase = [string](Get-Item -LiteralPath 'Env:GIT_REPLACE_REF_BASE').Value
    if (-not [string]::IsNullOrWhiteSpace($replaceRefBase))
    {
        throw 'Release builds reject custom Git replace refs.'
    }
}
$replaceRefs = @(& $gitCommand '--no-replace-objects' -C $projectRoot `
    for-each-ref 'refs/replace/' 2>&1)
if ($LASTEXITCODE -ne 0)
{
    throw 'Could not inspect Git replace refs.'
}
$replaceRefs = @($replaceRefs | ForEach-Object { [string]$_ } | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_)
})
if ($replaceRefs.Count -ne 0)
{
    throw 'Release builds reject Git replace refs.'
}

$commitBlobEntries = @(Get-CommitBlobEntries `
    -GitCommand $gitCommand `
    -RepositoryPath $projectRoot `
    -Commit $sourceCommit)
$reservedReleaseControlPaths = @('global.json', 'Directory.Build.rsp')
foreach ($reservedPath in $reservedReleaseControlPaths)
{
    if (@($commitBlobEntries | Where-Object {
            [string]::Equals([string]$_.Path, $reservedPath, [StringComparison]::OrdinalIgnoreCase)
        }).Count -ne 0)
    {
        throw "Committed source uses a release-reserved control path: $reservedPath"
    }
}

$dirtyTracked = $false
$dirtyWorktree = $false
$dirtyTrackedPaths = @()
$statusEntries = @()

Assert-NoReparseInExistingPath $projectRoot 'Repository path'
Assert-NoReparseInExistingPath $outputParent 'Release output parent'

[System.IO.Directory]::CreateDirectory($outputParent) | Out-Null
$workspacePrefix = ".$outputLeaf.build-"
$workspace = Join-Path $outputParent ($workspacePrefix + [Guid]::NewGuid().ToString('N'))
$sourceArchive = Join-Path $workspace 'source.zip'
$releaseSourceRoot = Join-Path $workspace 'source'
$projectPath = Join-Path $releaseSourceRoot 'WallpaperField.csproj'
$solutionPath = Join-Path $releaseSourceRoot 'WallpaperField.slnx'
$nugetConfigPath = Join-Path $releaseSourceRoot 'NuGet.Config'
$releaseNotesPath = Join-Path $releaseSourceRoot 'docs\releases\v1.4.0.md'
$publishDirectory = Join-Path $workspace 'publish'
$candidateDirectory = Join-Path $workspace 'candidate'
$packageDirectory = Join-Path $workspace 'package'
$qaDirectory = Join-Path $workspace 'qa'
$publishedExecutable = Join-Path $publishDirectory 'WallpaperField.exe'
$candidateExecutable = Join-Path $candidateDirectory 'WallpaperField.exe'
$candidateZip = Join-Path $candidateDirectory $ZipName
if (Test-Path -LiteralPath $rootExecutable -PathType Leaf)
{
    $rootHashBefore = Get-Sha256 $rootExecutable
}
else
{
    $rootHashBefore = $null
}

[System.IO.Directory]::CreateDirectory($workspace) | Out-Null
Assert-NoReparseInExistingPath $workspace 'Release build workspace'
$hadImportDirectoryBuildProps = Test-Path -LiteralPath 'Env:ImportDirectoryBuildProps'
$previousImportDirectoryBuildProps = $env:ImportDirectoryBuildProps
$hadImportDirectoryBuildTargets = Test-Path -LiteralPath 'Env:ImportDirectoryBuildTargets'
$previousImportDirectoryBuildTargets = $env:ImportDirectoryBuildTargets
$hadImportDirectoryPackagesProps = Test-Path -LiteralPath 'Env:ImportDirectoryPackagesProps'
$previousImportDirectoryPackagesProps = $env:ImportDirectoryPackagesProps
$releaseSourceLocationPushed = $false
$buildImportEnvironmentSet = $false
try
{
    $archiveOutput = @(& $gitCommand '--no-replace-objects' `
        -c core.autocrlf=false `
        -c core.eol=lf `
        -c core.safecrlf=false `
        -c core.attributesFile=NUL `
        -C $projectRoot archive `
        '--format=zip' `
        "--output=$sourceArchive" `
        $sourceCommit 2>&1)
    if ($LASTEXITCODE -ne 0)
    {
        throw "Could not materialize the committed release source (exit $LASTEXITCODE): $($archiveOutput -join [Environment]::NewLine)"
    }
    if (-not (Test-Path -LiteralPath $sourceArchive -PathType Leaf))
    {
        throw 'Git archive did not create the committed release source archive.'
    }

    [System.IO.Directory]::CreateDirectory($releaseSourceRoot) | Out-Null
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Expand-ReleaseSourceZip -ArchivePath $sourceArchive -DestinationRoot $releaseSourceRoot
    Remove-Item -LiteralPath $sourceArchive -Force
    Assert-NoReparseInExistingPath $releaseSourceRoot 'Committed release source'
    $sourceReparseEntries = @(Get-ChildItem -LiteralPath $releaseSourceRoot -Force -Recurse | Where-Object {
        ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
    })
    if ($sourceReparseEntries.Count -ne 0)
    {
        throw 'Committed release source contains a reparse point.'
    }
    Assert-CommitSourceExact `
        -Entries $commitBlobEntries `
        -SourceRoot $releaseSourceRoot `
        -GitCommand $gitCommand `
        -RepositoryPath $projectRoot

    $requiredSourceFiles = @(
        $projectPath,
        $solutionPath,
        $nugetConfigPath,
        $releaseNotesPath,
        (Join-Path $releaseSourceRoot 'LICENSE'),
        (Join-Path $releaseSourceRoot 'THIRD-PARTY-NOTICES.md'),
        (Join-Path $releaseSourceRoot 'ThirdParty\RePKG\LICENSE.txt'),
        (Join-Path $releaseSourceRoot 'ThirdParty\RePKG\THIRD-PARTY-NOTICES.txt'),
        (Join-Path $releaseSourceRoot 'ThirdParty\RePKG\UPSTREAM-PATCHES.md')
    )
    foreach ($requiredSourceFile in $requiredSourceFiles)
    {
        if (-not (Test-Path -LiteralPath $requiredSourceFile -PathType Leaf))
        {
            throw "Committed release source is missing a required file: $requiredSourceFile"
        }
    }

    $env:ImportDirectoryBuildProps = 'false'
    $env:ImportDirectoryBuildTargets = 'false'
    $env:ImportDirectoryPackagesProps = 'false'
    $buildImportEnvironmentSet = $true
    $dotnetCommand = @(Get-Command dotnet -CommandType Application -ErrorAction Stop)[0].Source
    $sdkSelection = Get-ControlledSdkSelection -DotnetCommand $dotnetCommand
    $sdkVersion = [string]$sdkSelection.Text
    $controlledGlobalJson = [ordered]@{
        sdk = [ordered]@{
            version = $sdkVersion
            rollForward = 'disable'
            allowPrerelease = $false
            paths = @('$host$')
        }
    } | ConvertTo-Json -Depth 4
    Write-Utf8File (Join-Path $releaseSourceRoot 'global.json') ($controlledGlobalJson + [Environment]::NewLine)
    Write-Utf8File (Join-Path $releaseSourceRoot 'Directory.Build.rsp') ''

    Push-Location -LiteralPath $releaseSourceRoot
    $releaseSourceLocationPushed = $true

    $sdkVersionOutput = @(& $dotnetCommand --version 2>&1)
    $sdkVersionExitCode = $LASTEXITCODE
    $resolvedSdkVersion = ($sdkVersionOutput -join '').Trim()
    if ($sdkVersionExitCode -ne 0 -or $resolvedSdkVersion -ne $sdkVersion)
    {
        throw "Private release source resolved SDK '$resolvedSdkVersion', expected '$sdkVersion'."
    }

    $restoreArguments = @(
        'restore',
        $projectPath,
        '-noAutoResponse',
        '--runtime', $RuntimeIdentifier,
        '--configfile', $nugetConfigPath,
        '-p:ImportDirectoryBuildProps=false',
        '-p:ImportDirectoryBuildTargets=false',
        '-p:ImportDirectoryPackagesProps=false'
    )
    & $dotnetCommand @restoreArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Release restore failed with exit code $LASTEXITCODE."
    }

    $publishArguments = @(
        'publish',
        $projectPath,
        '--configuration', $Configuration,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $publishDirectory,
        '--no-restore',
        '-noAutoResponse',
        '-p:ImportDirectoryBuildProps=false',
        '-p:ImportDirectoryBuildTargets=false',
        '-p:ImportDirectoryPackagesProps=false',
        "-p:InformationalVersion=$ProductVersion+$sourceCommit",
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    & $dotnetCommand @publishArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Release publish failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf))
    {
        throw "Publish did not create the expected executable: $publishedExecutable"
    }

    $publishPrefix = $publishDirectory + [System.IO.Path]::DirectorySeparatorChar
    $actualPublishFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse | ForEach-Object {
        $_.FullName.Substring($publishPrefix.Length).Replace('\', '/')
    })
    $expectedPublishFiles = @(
        'WallpaperField.exe',
        'THIRD-PARTY-NOTICES.md',
        'ThirdParty/RePKG/LICENSE.txt',
        'ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt',
        'ThirdParty/RePKG/UPSTREAM-PATCHES.md'
    )
    Assert-ExactSet $actualPublishFiles $expectedPublishFiles 'Publish output'

    $publishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExecutable)
    $expectedInformationalVersion = "$ProductVersion+$sourceCommit"
    if ($publishedVersion.FileVersion -ne $FileVersion)
    {
        throw "Published FileVersion was $($publishedVersion.FileVersion), expected $FileVersion."
    }
    if ($publishedVersion.ProductVersion -ne $expectedInformationalVersion)
    {
        throw "Published ProductVersion was $($publishedVersion.ProductVersion), expected $expectedInformationalVersion."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $publishedExecutable
    $signatureStatus = $signature.Status.ToString()
    $isSigned = $null -ne $signature.SignerCertificate
    $executableHash = Get-Sha256 $publishedExecutable
    $executableLength = (Get-Item -LiteralPath $publishedExecutable).Length

    [System.IO.Directory]::CreateDirectory((Join-Path $qaDirectory 'source\9001')) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $qaDirectory 'output')) | Out-Null
    $qaProjectJson = @{
        title = 'v1.4.0 release candidate QA'
        workshopid = '9001'
        type = 'scene'
        file = 'scene.json'
    } | ConvertTo-Json
    Write-Utf8File (Join-Path $qaDirectory 'source\9001\project.json') $qaProjectJson
    $snapshotPath = Join-Path $qaDirectory 'candidate-launch.png'
    $launchArgumentLine = '--source "{0}" --output "{1}" --scan --page browse --snapshot "{2}" --width 920 --height 680 --reduced-motion' -f (Join-Path $qaDirectory 'source'), (Join-Path $qaDirectory 'output'), $snapshotPath
    $candidateProcess = Start-Process -FilePath $publishedExecutable -ArgumentList $launchArgumentLine -WindowStyle Hidden -PassThru
    if (-not $candidateProcess.WaitForExit(30000))
    {
        $candidateProcess.Kill()
        $candidateProcess.WaitForExit()
        throw 'Published candidate launch did not exit within 30 seconds.'
    }
    if ($candidateProcess.ExitCode -ne 0)
    {
        throw "Published candidate launch exited with $($candidateProcess.ExitCode)."
    }
    if (-not (Test-Path -LiteralPath $snapshotPath -PathType Leaf) -or (Get-Item -LiteralPath $snapshotPath).Length -le 0)
    {
        throw 'Published candidate did not produce its controlled PNG snapshot.'
    }

    $dependencyArguments = @(
        'list',
        $projectPath,
        'package',
        '--include-transitive',
        '--format', 'json',
        '--no-restore'
    )
    $dependencyOutput = @(& $dotnetCommand @dependencyArguments)
    if ($LASTEXITCODE -ne 0)
    {
        throw "Dependency enumeration failed with exit code $LASTEXITCODE."
    }
    $dependencyJson = $dependencyOutput -join [Environment]::NewLine
    try
    {
        $dependencyModel = $dependencyJson | ConvertFrom-Json
    }
    catch
    {
        throw "Dependency enumeration did not return valid JSON: $($_.Exception.Message)"
    }
    $dependencyPackageCount = 0
    foreach ($dependencyProject in @($dependencyModel.projects))
    {
        $absoluteProjectPath = Get-NormalizedPath ([string]$dependencyProject.path)
        $projectPrefix = $releaseSourceRoot + [System.IO.Path]::DirectorySeparatorChar
        if (-not $absoluteProjectPath.StartsWith(
                $projectPrefix,
                [StringComparison]::OrdinalIgnoreCase))
        {
            throw "Dependency project is outside the repository: $absoluteProjectPath"
        }
        $dependencyProject.path = $absoluteProjectPath.Substring(
            $projectPrefix.Length).Replace('\', '/')

        foreach ($framework in @($dependencyProject.frameworks))
        {
            if ($null -ne $framework.PSObject.Properties['topLevelPackages'])
            {
                $dependencyPackageCount += @($framework.topLevelPackages).Count
            }
            if ($null -ne $framework.PSObject.Properties['transitivePackages'])
            {
                $dependencyPackageCount += @($framework.transitivePackages).Count
            }
        }
    }
    if (@($dependencyModel.projects).Count -le 0 -or $dependencyPackageCount -le 0)
    {
        throw 'Dependency JSON must contain projects and resolved packages.'
    }
    $dependencyJson = $dependencyModel | ConvertTo-Json -Depth 12

    [System.IO.Directory]::CreateDirectory($candidateDirectory) | Out-Null
    [System.IO.Directory]::CreateDirectory($packageDirectory) | Out-Null
    Copy-VerifiedFile $publishedExecutable $candidateExecutable
    Write-Utf8File (Join-Path $candidateDirectory 'dependencies.json') $dependencyJson
    $dependencyHash = Get-Sha256 (Join-Path $candidateDirectory 'dependencies.json')

    Copy-VerifiedFile $publishedExecutable (Join-Path $packageDirectory 'WallpaperField.exe')
    Copy-VerifiedFile (Join-Path $releaseSourceRoot 'LICENSE') (Join-Path $packageDirectory 'LICENSE')
    Copy-VerifiedFile (Join-Path $releaseSourceRoot 'THIRD-PARTY-NOTICES.md') (Join-Path $packageDirectory 'THIRD-PARTY-NOTICES.md')
    Copy-VerifiedFile $releaseNotesPath (Join-Path $packageDirectory 'RELEASE-NOTES.md')
    Copy-VerifiedFile (Join-Path $releaseSourceRoot 'ThirdParty\RePKG\LICENSE.txt') (Join-Path $packageDirectory 'ThirdParty\RePKG\LICENSE.txt')
    Copy-VerifiedFile (Join-Path $releaseSourceRoot 'ThirdParty\RePKG\THIRD-PARTY-NOTICES.txt') (Join-Path $packageDirectory 'ThirdParty\RePKG\THIRD-PARTY-NOTICES.txt')
    Copy-VerifiedFile (Join-Path $releaseSourceRoot 'ThirdParty\RePKG\UPSTREAM-PATCHES.md') (Join-Path $packageDirectory 'ThirdParty\RePKG\UPSTREAM-PATCHES.md')
    Copy-VerifiedFile (Join-Path $candidateDirectory 'dependencies.json') (Join-Path $packageDirectory 'dependencies.json')

    $manifest = [ordered]@{
        schemaVersion = 1
        product = 'Wallpaper Field'
        version = $ProductVersion
        fileVersion = $FileVersion
        assemblyVersion = $AssemblyVersion
        informationalVersion = $expectedInformationalVersion
        sourceCommit = $sourceCommit
        sourceTree = [ordered]@{
            dirtyTracked = $dirtyTracked
            dirtyWorktree = $dirtyWorktree
            dirtyTrackedPaths = @($dirtyTrackedPaths)
            statusEntries = @($statusEntries)
        }
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
        sdkVersion = $sdkVersion
        sdkPolicy = [ordered]@{
            rollForward = 'disable'
            allowPrerelease = $false
            searchPaths = @('$host$')
            privateGlobalJson = $true
            privateEmptyDirectoryBuildRsp = $true
        }
        rid = $RuntimeIdentifier
        configuration = $Configuration
        publishProperties = [ordered]@{
            selfContained = $true
            publishSingleFile = $true
            includeSourceRevisionInInformationalVersion = $false
            includeNativeLibrariesForSelfExtract = $true
            enableCompressionInSingleFile = $true
            publishTrimmed = $false
            debugType = 'None'
            debugSymbols = $false
        }
        executable = [ordered]@{
            path = 'WallpaperField.exe'
            sha256 = $executableHash
            size = $executableLength
        }
        signing = [ordered]@{
            signed = $isSigned
            authenticodeStatus = $signatureStatus
        }
        dependencies = [ordered]@{
            path = 'dependencies.json'
            sha256 = $dependencyHash
            resolvedPackageEntries = $dependencyPackageCount
        }
        distribution = [ordered]@{
            zip = $ZipName
            releaseNotes = 'RELEASE-NOTES.md'
        }
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 12
    Write-Utf8File (Join-Path $candidateDirectory 'release-manifest.json') $manifestJson
    Copy-VerifiedFile (Join-Path $candidateDirectory 'release-manifest.json') (Join-Path $packageDirectory 'release-manifest.json')

    $expectedZipFiles = @(
        'WallpaperField.exe',
        'LICENSE',
        'THIRD-PARTY-NOTICES.md',
        'RELEASE-NOTES.md',
        'release-manifest.json',
        'dependencies.json',
        'ThirdParty/RePKG/LICENSE.txt',
        'ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt',
        'ThirdParty/RePKG/UPSTREAM-PATCHES.md'
    )
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $writeArchive = [System.IO.Compression.ZipFile]::Open(
        $candidateZip,
        [System.IO.Compression.ZipArchiveMode]::Create)
    try
    {
        foreach ($zipEntryName in $expectedZipFiles)
        {
            $packageFile = Join-Path $packageDirectory $zipEntryName.Replace('/', '\')
            if (-not (Test-Path -LiteralPath $packageFile -PathType Leaf))
            {
                throw "Package file is missing before ZIP creation: $zipEntryName"
            }
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $writeArchive,
                $packageFile,
                $zipEntryName,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally
    {
        $writeArchive.Dispose()
    }

    $zipHash = Get-Sha256 $candidateZip
    $shaLines = @(
        "$executableHash  WallpaperField.exe",
        "$zipHash  $ZipName"
    ) -join [Environment]::NewLine
    Write-Utf8File (Join-Path $candidateDirectory 'SHA256SUMS') ($shaLines + [Environment]::NewLine)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($candidateZip)
    try
    {
        $actualZipFiles = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) } | ForEach-Object {
            $_.FullName.Replace('\', '/')
        })
        Assert-ExactSet $actualZipFiles $expectedZipFiles 'ZIP contents'

        $zipSourcePairs = @{
            'WallpaperField.exe' = $publishedExecutable
            'LICENSE' = (Join-Path $releaseSourceRoot 'LICENSE')
            'THIRD-PARTY-NOTICES.md' = (Join-Path $releaseSourceRoot 'THIRD-PARTY-NOTICES.md')
            'RELEASE-NOTES.md' = $releaseNotesPath
            'release-manifest.json' = (Join-Path $candidateDirectory 'release-manifest.json')
            'dependencies.json' = (Join-Path $candidateDirectory 'dependencies.json')
            'ThirdParty/RePKG/LICENSE.txt' = (Join-Path $releaseSourceRoot 'ThirdParty\RePKG\LICENSE.txt')
            'ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt' = (Join-Path $releaseSourceRoot 'ThirdParty\RePKG\THIRD-PARTY-NOTICES.txt')
            'ThirdParty/RePKG/UPSTREAM-PATCHES.md' = (Join-Path $releaseSourceRoot 'ThirdParty\RePKG\UPSTREAM-PATCHES.md')
        }
        foreach ($zipEntryName in $zipSourcePairs.Keys)
        {
            $zipEntryHash = Get-ZipEntrySha256 $archive $zipEntryName
            $sourceHash = Get-Sha256 $zipSourcePairs[$zipEntryName]
            if ($zipEntryHash -ne $sourceHash)
            {
                throw "ZIP entry hash differs from its source: $zipEntryName"
            }
        }
    }
    finally
    {
        $archive.Dispose()
    }

    if ((Get-Sha256 $candidateExecutable) -ne $executableHash -or (Get-Sha256 $candidateZip) -ne $zipHash)
    {
        throw 'Candidate hashes changed after assembly.'
    }
    $actualCandidateFiles = @(Get-ChildItem -LiteralPath $candidateDirectory -File | Select-Object -ExpandProperty Name)
    $expectedCandidateFiles = @(
        'WallpaperField.exe',
        $ZipName,
        'release-manifest.json',
        'dependencies.json',
        'SHA256SUMS'
    )
    Assert-ExactSet $actualCandidateFiles $expectedCandidateFiles 'Candidate output'

    if (Test-Path -LiteralPath $outputPath)
    {
        throw "OutputDirectory appeared during the build: $outputPath"
    }
    [System.IO.Directory]::Move($candidateDirectory, $outputPath)

    if ($UpdateTrackedExecutable)
    {
        $finalCandidateExecutable = Join-Path $outputPath 'WallpaperField.exe'
        $sameExecutablePath = [string]::Equals(
            (Get-NormalizedPath $finalCandidateExecutable),
            (Get-NormalizedPath $rootExecutable),
            [StringComparison]::OrdinalIgnoreCase)
        if ($sameExecutablePath)
        {
            throw 'Candidate and tracked executable paths unexpectedly resolve to the same file.'
        }
        Update-TrackedExecutableAtomically $finalCandidateExecutable $rootExecutable $executableHash
    }
    else
    {
        if (Test-Path -LiteralPath $rootExecutable -PathType Leaf)
        {
            $rootHashAfter = Get-Sha256 $rootExecutable
        }
        else
        {
            $rootHashAfter = $null
        }
        if ($rootHashBefore -ne $rootHashAfter)
        {
            throw 'Default release build changed the tracked root executable.'
        }
    }

    Write-Host "RELEASE_RESULT version=$ProductVersion commit=$sourceCommit signed=$($isSigned.ToString().ToLowerInvariant()) output=$outputPath"
    Write-Host "EXE_SHA256 $executableHash"
    Write-Host "ZIP_SHA256 $zipHash"
}
finally
{
    try
    {
        if ($releaseSourceLocationPushed)
        {
            Pop-Location
            $releaseSourceLocationPushed = $false
        }
        if ($buildImportEnvironmentSet)
        {
            if ($hadImportDirectoryBuildProps)
            {
                Set-Item -LiteralPath 'Env:ImportDirectoryBuildProps' -Value $previousImportDirectoryBuildProps
            }
            else
            {
                Remove-Item -LiteralPath 'Env:ImportDirectoryBuildProps' -ErrorAction SilentlyContinue
            }
            if ($hadImportDirectoryBuildTargets)
            {
                Set-Item -LiteralPath 'Env:ImportDirectoryBuildTargets' -Value $previousImportDirectoryBuildTargets
            }
            else
            {
                Remove-Item -LiteralPath 'Env:ImportDirectoryBuildTargets' -ErrorAction SilentlyContinue
            }
            if ($hadImportDirectoryPackagesProps)
            {
                Set-Item -LiteralPath 'Env:ImportDirectoryPackagesProps' -Value $previousImportDirectoryPackagesProps
            }
            else
            {
                Remove-Item -LiteralPath 'Env:ImportDirectoryPackagesProps' -ErrorAction SilentlyContinue
            }
        }
    }
    finally
    {
        if (Test-Path -LiteralPath $workspace)
        {
            Remove-VerifiedWorkspace $workspace $outputParent $workspacePrefix
        }
    }
}
