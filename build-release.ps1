[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,

    [switch]$UpdateTrackedExecutable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ProductVersion = '1.2.1'
$FileVersion = '1.2.1.0'
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
$projectPath = Join-Path $projectRoot 'WallpaperField.csproj'
$solutionPath = Join-Path $projectRoot 'WallpaperField.slnx'
$nugetConfigPath = Join-Path $projectRoot 'NuGet.Config'
$rootExecutable = Join-Path $projectRoot 'GUI_for_RePKG.exe'
$releaseNotesPath = Join-Path $projectRoot 'docs\releases\v1.2.1.md'
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

Assert-NoReparseInExistingPath $projectRoot 'Repository path'
Assert-NoReparseInExistingPath $outputParent 'Release output parent'

[System.IO.Directory]::CreateDirectory($outputParent) | Out-Null
$workspacePrefix = ".$outputLeaf.build-"
$workspace = Join-Path $outputParent ($workspacePrefix + [Guid]::NewGuid().ToString('N'))
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
try
{
    $sourceCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-fA-F]{40}$')
    {
        throw 'Could not resolve a full 40-character source commit.'
    }

    # Respect the checkout's EOL normalization; overriding it can make a fresh
    # Windows checkout appear tracked-dirty before Git refreshes its index.
    & git -C $projectRoot diff --quiet HEAD --
    $dirtyTracked = $LASTEXITCODE -ne 0
    $dirtyTrackedPaths = @(& git -C $projectRoot diff --name-only HEAD --)
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Could not enumerate tracked source changes.'
    }
    $statusEntries = @(& git -C $projectRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Could not inspect the source worktree state.'
    }
    $dirtyWorktree = $statusEntries.Count -gt 0

    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdkVersion))
    {
        throw 'Could not determine the .NET SDK version.'
    }

    $restoreArguments = @(
        'restore',
        $projectPath,
        '--runtime', $RuntimeIdentifier,
        '--configfile', $nugetConfigPath
    )
    & dotnet @restoreArguments
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
        "-p:InformationalVersion=$ProductVersion+$sourceCommit",
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    & dotnet @publishArguments
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
        title = 'v1.2.1 release candidate QA'
        workshopid = '9001'
        type = 'scene'
        file = 'scene.json'
    } | ConvertTo-Json
    Write-Utf8File (Join-Path $qaDirectory 'source\9001\project.json') $qaProjectJson
    $snapshotPath = Join-Path $qaDirectory 'candidate-launch.png'
    $launchArgumentLine = '--source "{0}" --output "{1}" --scan --snapshot "{2}" --width 920 --height 680 --reduced-motion' -f (Join-Path $qaDirectory 'source'), (Join-Path $qaDirectory 'output'), $snapshotPath
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
        $solutionPath,
        'package',
        '--include-transitive',
        '--format', 'json'
    )
    $dependencyOutput = @(& dotnet @dependencyArguments)
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
        $projectPrefix = $projectRoot + [System.IO.Path]::DirectorySeparatorChar
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
    Copy-VerifiedFile (Join-Path $projectRoot 'LICENSE') (Join-Path $packageDirectory 'LICENSE')
    Copy-VerifiedFile (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') (Join-Path $packageDirectory 'THIRD-PARTY-NOTICES.md')
    Copy-VerifiedFile $releaseNotesPath (Join-Path $packageDirectory 'RELEASE-NOTES.md')
    Copy-VerifiedFile (Join-Path $projectRoot 'ThirdParty\RePKG\LICENSE.txt') (Join-Path $packageDirectory 'ThirdParty\RePKG\LICENSE.txt')
    Copy-VerifiedFile (Join-Path $projectRoot 'ThirdParty\RePKG\THIRD-PARTY-NOTICES.txt') (Join-Path $packageDirectory 'ThirdParty\RePKG\THIRD-PARTY-NOTICES.txt')
    Copy-VerifiedFile (Join-Path $projectRoot 'ThirdParty\RePKG\UPSTREAM-PATCHES.md') (Join-Path $packageDirectory 'ThirdParty\RePKG\UPSTREAM-PATCHES.md')
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
            'LICENSE' = (Join-Path $projectRoot 'LICENSE')
            'THIRD-PARTY-NOTICES.md' = (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md')
            'RELEASE-NOTES.md' = $releaseNotesPath
            'release-manifest.json' = (Join-Path $candidateDirectory 'release-manifest.json')
            'dependencies.json' = (Join-Path $candidateDirectory 'dependencies.json')
            'ThirdParty/RePKG/LICENSE.txt' = (Join-Path $projectRoot 'ThirdParty\RePKG\LICENSE.txt')
            'ThirdParty/RePKG/THIRD-PARTY-NOTICES.txt' = (Join-Path $projectRoot 'ThirdParty\RePKG\THIRD-PARTY-NOTICES.txt')
            'ThirdParty/RePKG/UPSTREAM-PATCHES.md' = (Join-Path $projectRoot 'ThirdParty\RePKG\UPSTREAM-PATCHES.md')
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
    if (Test-Path -LiteralPath $workspace)
    {
        Remove-VerifiedWorkspace $workspace $outputParent $workspacePrefix
    }
}
